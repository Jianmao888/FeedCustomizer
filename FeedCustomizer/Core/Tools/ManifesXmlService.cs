using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Infrastructure.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using Windows.ApplicationModel;

namespace FeedCustomizer.Core.Tools
{
    public class ManifestXmlService
    {
        private static readonly IAppLog Log = AppLog.For<ManifestXmlService>();

        // XML命名空间定义（对应文件中的xmlns声明）
        private static readonly XNamespace DefaultNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        private static readonly XNamespace Uap3Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
        // A package exposes one feed provider implementation. The provider can
        // contain any number of feed Definitions. Keep this ID stable across
        // edits so Widgets does not interpret every save as a new provider.
        private const string FeedProviderId = "feedcustomizer";
        private static readonly XNamespace UapNs = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        private static readonly XNamespace ComNs = "http://schemas.microsoft.com/appx/manifest/com/windows10";

        private static string DefauleXmlFilePath => AppDataPaths.PackageLocalManifestPath;

        private static string ProviderPackageDisplayName
        {
            get
            {
                try
                {
                    var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
                    string displayName = resourceLoader.GetString("ProviderPackageDisplayName");
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        return displayName;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "读取 Provider 包显示名称失败，将使用默认名称");
                }

                return "Feed Customization Container";
            }
        }

        private static string ProviderPublisherDisplayName
        {
            get
            {
                try
                {
                    string publisherDisplayName = Package.Current.PublisherDisplayName;
                    if (!string.IsNullOrWhiteSpace(publisherDisplayName))
                    {
                        return publisherDisplayName;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "读取发布者显示名称失败，将使用默认名称");
                }

                return "窗边的贱猫";
            }
        }

        /// <summary>
        /// 从XML文件读取所有FeedItem
        /// </summary>
        /// <param name="xmlFilePath">XML文件路径</param>
        /// <returns>FeedItem列表</returns>
        public static async Task<List<Feed>> Read(string? xmlFilePath = null)
        {
            Log.Debug("开始读取 Provider 清单，使用自定义路径={UsesCustomPath}", xmlFilePath is not null);
            xmlFilePath ??= DefauleXmlFilePath;
            return await Task.Run(() =>
            {
                var doc = XDocument.Load(xmlFilePath);
                return ParseDefinitions(doc);
            });
        }

        /// <summary>
        /// 从XML字符串读取所有FeedDefinition
        /// </summary>
        /// <param name="xmlContent">XML内容字符串</param>
        /// <returns>FeedItem列表</returns>
        public static List<Feed> ReadFromString(string xmlContent)
        {
            var doc = XDocument.Parse(xmlContent);
            return ParseDefinitions(doc);
        }

        /// <summary>
        /// 将订阅源写入同一个 FeedProvider 的 Definitions 集合。
        /// 部署调用方传入候选文件路径，完成校验后再原子替换用户配置。
        /// </summary>
        /// <param name="xmlFilePath">XML文件路径</param>
        /// <param name="definitions">要写入的FeedItem列表</param>
        public static async Task Write(List<Feed> definitions, string? xmlFilePath = null)
        {
            xmlFilePath ??= DefauleXmlFilePath;
            await Task.Run(() =>
            {
                var doc = XDocument.Load(xmlFilePath);
                UpdateDefinitions(doc, definitions);
                doc.Save(xmlFilePath, SaveOptions.None);
            });
        }

        /// <summary>
        /// 将FeedItem列表转换为XML字符串
        /// </summary>
        /// <param name="definitions">要转换的FeedItem列表</param>
        /// <returns>XML字符串</returns>
        public static string WriteToString(List<Feed> definitions)
        {
            // 创建一个包含Definitions节点的临时文档
            var doc = new XDocument(
                new XElement(DefaultNs + "Root",
                    new XElement(DefaultNs + "Definitions")
                )
            );

            var container = doc.Descendants(DefaultNs + "Definitions").First();
            foreach (var def in definitions)
            {
                container.Add(CreateDefinitionElement(def));
            }

            return doc.ToString(SaveOptions.None);
        }

        /// <summary>
        /// 兼容旧版每个源一个 AppExtension 的清单格式，将所有源归并为同一个 Provider。
        /// 这是清单结构规范化，不改变用户文件的目录或配置来源。
        /// </summary>
        internal static void NormalizeProviderManifest(XDocument document)
        {
            if (!document.Descendants(Uap3Ns + "AppExtension").Any(element =>
                    string.Equals(
                        element.Attribute("Name")?.Value,
                        "com.microsoft.windows.widgets.feeds",
                        StringComparison.Ordinal)))
            {
                return;
            }

            var feeds = ParseDefinitions(document)
                .GroupBy(feed => feed.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            UpdateDefinitions(document, feeds);
        }

        /// <summary>
        /// 在候选清单中同步当前架构、显示名称、徽标和可执行路径，保持模板与运行时清单职责独立。
        /// </summary>
        internal static void SynchronizePresentation(string? manifestPath = null)
        {
            // 可在尚未发布的候选清单中完成变换，避免更新过程中直接覆盖唯一的用户配置。
            manifestPath ??= AppDataPaths.PackageLocalManifestPath;
            string processorArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string displayName = ProviderPackageDisplayName;
            string relativeExecutablePath = Path.Combine(
                "FeedProvider",
                "FeedProvider.exe").Replace(Path.DirectorySeparatorChar, '\\');

            var document = XDocument.Load(manifestPath);
            var root = document.Root
                ?? throw new InvalidDataException("源提供程序清单缺少 Package 根节点。");
            var identity = root.Element(DefaultNs + "Identity")
                ?? throw new InvalidDataException("源提供程序清单中缺少 Identity 节点。");
            identity.SetAttributeValue("ProcessorArchitecture", processorArchitecture);

            var properties = root.Element(DefaultNs + "Properties")
                ?? throw new InvalidDataException("源提供程序清单中缺少 Properties 节点。");
            SetElementValue(properties, DefaultNs + "DisplayName", displayName);
            SetElementValue(properties, DefaultNs + "PublisherDisplayName", ProviderPublisherDisplayName);
            SetElementValue(properties, DefaultNs + "Logo", "Assets\\StoreLogo.scale-200.png");

            var application = root.Element(DefaultNs + "Applications")?.Element(DefaultNs + "Application")
                ?? throw new InvalidDataException("源提供程序清单中缺少 Application 节点。");

            application.SetAttributeValue("Executable", relativeExecutablePath);
            var visualElements = application.Elements().FirstOrDefault(element =>
                    element.Name.LocalName == "VisualElements")
                ?? throw new InvalidDataException("源提供程序清单中缺少 VisualElements 节点。");

            // uap3:VisualElements exposes AppListEntry, which keeps this COM/feed
            // host package out of Start while retaining its package identity.
            visualElements.Name = Uap3Ns + "VisualElements";
            visualElements.SetAttributeValue("DisplayName", displayName);
            visualElements.SetAttributeValue("Description", displayName);
            visualElements.SetAttributeValue("Square150x150Logo", "Assets\\Square150x150Logo.scale-200.png");
            visualElements.SetAttributeValue("Square44x44Logo", "Assets\\Square44x44Logo.scale-200.png");
            visualElements.SetAttributeValue("AppListEntry", "none");

            var defaultTile = visualElements.Elements().FirstOrDefault(element =>
                element.Name.LocalName == "DefaultTile");
            if (defaultTile is not null)
            {
                defaultTile.Name = UapNs + "DefaultTile";
                defaultTile.SetAttributeValue("Square71x71Logo", "Assets\\SmallTile.scale-200.png");
                defaultTile.SetAttributeValue("Wide310x150Logo", "Assets\\WideTile.scale-200.png");
                defaultTile.SetAttributeValue("Square310x310Logo", "Assets\\LargeTile.scale-200.png");
            }

            var splashScreen = visualElements.Elements().FirstOrDefault(element =>
                element.Name.LocalName == "SplashScreen");
            if (splashScreen is not null)
            {
                splashScreen.Name = UapNs + "SplashScreen";
                splashScreen.SetAttributeValue("Image", "Assets\\SplashScreen.scale-200.png");
            }

            foreach (var exeServer in application.Descendants(ComNs + "ExeServer"))
            {
                exeServer.SetAttributeValue("Executable", relativeExecutablePath);
                exeServer.SetAttributeValue("DisplayName", displayName);
                foreach (var comClass in exeServer.Elements(ComNs + "Class"))
                {
                    comClass.SetAttributeValue("DisplayName", displayName);
                }
            }

            foreach (var appExtension in application
                .Descendants(Uap3Ns + "AppExtension")
                .Where(element => element.Attribute("Id")?.Value == "feedcustomizer"))
            {
                appExtension.SetAttributeValue("DisplayName", displayName);
                var provider = appExtension
                    .Descendants(DefaultNs + "FeedProvider")
                    .FirstOrDefault();
                if (provider is not null)
                {
                    provider.SetAttributeValue("DisplayName", displayName);
                    provider.SetAttributeValue("Description", displayName);
                    provider.SetAttributeValue("Icon", "Assets\\StoreLogo.scale-200.png");
                }
            }

            // Migrate manifests written by pre-fix builds before registering
            // the package again. Without this, an existing multi-provider
            // manifest remains in place until the user edits/saves feeds, so
            // toggling the provider can still leave only the switch visible.
            NormalizeProviderManifest(document);

            document.Save(manifestPath);
        }

        /// <summary>
        /// Checks whether the staged manifest's presentation metadata still
        /// matches the current package.
        /// </summary>
        internal static bool IsPresentationCurrent(string manifestPath)
        {
            try
            {
                var document = XDocument.Load(manifestPath);
                var root = document.Root;
                var properties = root?.Element(DefaultNs + "Properties");
                var application = root?
                    .Element(DefaultNs + "Applications")?
                    .Element(DefaultNs + "Application");
                var visualElements = application?.Element(Uap3Ns + "VisualElements");
                if (properties is null || visualElements is null)
                {
                    return false;
                }

                var feedExtensions = application?
                    .Descendants(Uap3Ns + "AppExtension")
                    .Where(element => string.Equals(
                        element.Attribute("Name")?.Value,
                        "com.microsoft.windows.widgets.feeds",
                        StringComparison.Ordinal))
                    .ToList();

                if (feedExtensions is not { Count: 1 } ||
                    feedExtensions[0].Attribute("Id")?.Value != "feedcustomizer")
                {
                    return false;
                }

                var feedProvider = feedExtensions[0]
                    .Descendants(DefaultNs + "FeedProvider")
                    .SingleOrDefault();
                if (feedProvider?.Attribute("Id")?.Value != "feedcustomizer")
                {
                    return false;
                }

                return properties.Element(DefaultNs + "DisplayName")?.Value == ProviderPackageDisplayName &&
                    properties.Element(DefaultNs + "PublisherDisplayName")?.Value == ProviderPublisherDisplayName &&
                    properties.Element(DefaultNs + "Logo")?.Value == "Assets\\StoreLogo.scale-200.png" &&
                    visualElements.Attribute("DisplayName")?.Value == ProviderPackageDisplayName &&
                    visualElements.Attribute("AppListEntry")?.Value == "none" &&
                    visualElements.Attribute("Square150x150Logo")?.Value == "Assets\\Square150x150Logo.scale-200.png" &&
                    visualElements.Attribute("Square44x44Logo")?.Value == "Assets\\Square44x44Logo.scale-200.png";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "检查 Provider 清单展示资源失败");
                return false;
            }
        }

        private static void SetElementValue(XElement parent, XName elementName, string value)
        {
            var element = parent.Element(elementName)
                ?? throw new InvalidDataException($"源提供程序清单中缺少 {elementName.LocalName} 节点。");
            element.Value = value;
        }

        /// <summary>
        /// 从XML文档解析Definition节点
        /// </summary>
        private static List<Feed> ParseDefinitions(XDocument doc)
        {
            var feedItems = new List<Feed>();

            // 查找所有Definition元素（使用本地名称忽略命名空间前缀）
            var elements = doc.XPathSelectElements("//*[local-name()='Definition']");

            foreach (var element in elements)
            {
                var def = new Feed
                {
                    Id = GetAttr(element, "Id"),
                    Name = GetAttr(element, "DisplayName"),
                    Description = GetAttr(element, "Description"),
                    Url = GetAttr(element, "ContentUri"),
                    ImagePath = GetAttr(element, "Icon")
                };
                feedItems.Add(def);
            }

            return feedItems;
        }

        /// <summary>
        /// 更新XML文档中的Definition节点
        /// </summary>
        private static void UpdateDefinitions(XDocument doc, List<Feed> feedItems)
        {
            var appExtensions = doc
                .Descendants(Uap3Ns + "AppExtension")
                .Where(element => string.Equals(
                    element.Attribute("Name")?.Value,
                    "com.microsoft.windows.widgets.feeds",
                    StringComparison.Ordinal))
                .ToList();

            var extensionElements = appExtensions
                .Select(element => element.Parent)
                .Where(element => element?.Name == Uap3Ns + "Extension")
                .Cast<XElement>()
                .Distinct()
                .ToList();

            if (extensionElements.Count == 0)
            {
                throw new InvalidOperationException("在XML中找不到FeedProvider扩展节点");
            }

            XElement extensionContainer = extensionElements[0].Parent
                ?? throw new InvalidOperationException("FeedProvider扩展缺少父节点");
            var extensionTemplate = new XElement(extensionElements[0]);

            foreach (var extensionElement in extensionElements)
            {
                extensionElement.Remove();
            }

            // The Widgets manifest schema models one FeedProvider containing a
            // Definitions collection. Creating one AppExtension per feed makes
            // the board keep only one registration (usually the last one) and
            // causes the provider to disappear after a toggle/re-registration.
            extensionContainer.Add(CreateProviderExtension(extensionTemplate, feedItems));
        }

        private static XElement CreateProviderExtension(XElement template, IReadOnlyCollection<Feed> feedItems)
        {
            var extension = new XElement(template);
            var appExtension = extension.Element(Uap3Ns + "AppExtension")
                ?? throw new InvalidOperationException("FeedProvider扩展缺少AppExtension节点");
            var provider = appExtension.Descendants(DefaultNs + "FeedProvider").FirstOrDefault()
                ?? throw new InvalidOperationException("FeedProvider扩展缺少FeedProvider节点");
            var definitions = provider.Element(DefaultNs + "Definitions")
                ?? throw new InvalidOperationException("FeedProvider扩展缺少Definitions节点");

            string providerDisplayName = ProviderPackageDisplayName;
            const string providerIcon = "Assets\\StoreLogo.scale-200.png";

            appExtension.SetAttributeValue("Id", FeedProviderId);
            appExtension.SetAttributeValue("DisplayName", providerDisplayName);
            provider.SetAttributeValue("Id", FeedProviderId);
            provider.SetAttributeValue("DisplayName", providerDisplayName);
            provider.SetAttributeValue("Description", providerDisplayName);
            provider.SetAttributeValue("Icon", providerIcon);

            definitions.RemoveNodes();
            foreach (Feed feed in feedItems)
            {
                definitions.Add(CreateDefinitionElement(feed));
            }
            return extension;
        }

        private static string GetDescription(Feed feed)
        {
            return string.IsNullOrWhiteSpace(feed.Description) || feed.Description == "string.Empty"
                ? feed.Name
                : feed.Description;
        }

        /// <summary>
        /// 创建单个Definition的XElement
        /// </summary>
        private static XElement CreateDefinitionElement(Feed def)
        {
            Uri contentUri = NormalizeHttpUri(def.Url);
            string description = GetDescription(def);
            var element = new XElement(DefaultNs + "Definition");

            element.Add(
                new XAttribute("Id", def.Id ?? string.Empty),
                new XAttribute("DisplayName", def.Name ?? string.Empty),
                new XAttribute("Description", description ?? string.Empty),
                new XAttribute("ContentUri", contentUri.AbsoluteUri),
                new XAttribute("Icon", def.ImagePath ?? string.Empty)
            );

            // 添加自定义属性（仅当值不为空时）
            //if (!string.IsNullOrEmpty(def.Acccc))
            //    element.Add(new XAttribute("Acccc", def.Acccc));

            //if (!string.IsNullOrEmpty(def.Bnnnnn))
            //    element.Add(new XAttribute("Bnnnnn", def.Bnnnnn));

            return element;
        }

        public static Uri NormalizeHttpUri(string value)
        {
            string candidate = value.Trim();
            if (!candidate.Contains("://", StringComparison.Ordinal))
            {
                candidate = $"https://{candidate}";
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new UriFormatException($"无效的 HTTP(S) 网页地址：{value}");
            }

            return uri;
        }

        /// <summary>
        /// 安全获取XML属性值
        /// </summary>
        private static string GetAttr(XElement element, string attrName)
        {
            return element.Attribute(attrName)?.Value ?? string.Empty;
        }
    }
}
