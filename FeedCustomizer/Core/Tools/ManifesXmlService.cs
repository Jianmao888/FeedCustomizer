using FeedCustomizer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;

namespace FeedCustomizer.Core.Tools
{
    public class ManifestXmlService
    {
        // XML命名空间定义（对应文件中的xmlns声明）
        private static readonly XNamespace DefaultNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        private static readonly XNamespace Uap3Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
        // A package exposes one feed provider implementation. The provider can
        // contain any number of feed Definitions. Keep this ID stable across
        // edits so Widgets does not interpret every save as a new provider.
        private const string FeedProviderId = "feedcustomizer";

        private static string DefauleXmlFilePath => AppDataPaths.ManifestPath;

        /// <summary>
        /// 从XML文件读取所有FeedItem
        /// </summary>
        /// <param name="xmlFilePath">XML文件路径</param>
        /// <returns>FeedItem列表</returns>
        public static async Task<List<Feed>> Read(string? xmlFilePath = null)
        {
            Debug.WriteLine(xmlFilePath);
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
        /// 将FeedItem列表写入XML文件（每个源使用独立的FeedProvider扩展）
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
        /// Migrates manifests produced by older builds. Older builds emitted
        /// one AppExtension/FeedProvider for every feed, but Widgets treats the
        /// package as one provider whose Definitions collection contains all
        /// feeds. This migration is intentionally synchronous so it can run
        /// while the registration manifest is being refreshed at startup.
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

            string providerDisplayName = ResourcesCopier.ProviderPackageDisplayName;
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
