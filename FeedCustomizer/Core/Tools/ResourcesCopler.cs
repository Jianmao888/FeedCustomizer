using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using System.Xml.Linq;

namespace FeedCustomizer.Core.Tools
{
    public class ResourcesCopier
    {
        private static string UserAppFolder => AppDataPaths.FeedProviderFolder;

        private static string FlagFilePath => Path.Combine(UserAppFolder, ".first_run_complete");
        private static readonly SemaphoreSlim CopyGate = new(1, 1);

        internal static string ProviderPackageDisplayName
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
                    Debug.WriteLine($"Failed to load provider package display name: {ex.Message}");
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
                    Debug.WriteLine($"Failed to read publisher display name: {ex.Message}");
                }

                return "窗边的贱猫";
            }
        }

        /// <summary>
        /// 检查并复制资源，在 App 启动时调用
        /// </summary>
        public static async Task ResourcesCopyAsync()
        {
            await CopyGate.WaitAsync();
            try
            {
                // 获取 Resources 文件夹的路径
                string resourcesFolderPath = GetResourcesFolderPath();
                if (!Directory.Exists(resourcesFolderPath))
                {
                    throw new DirectoryNotFoundException($"Resources folder not found: {resourcesFolderPath}");
                }

                string assetsFolderPath = GetAssetsFolderPath();
                if (!Directory.Exists(assetsFolderPath))
                {
                    throw new DirectoryNotFoundException($"Assets folder not found: {assetsFolderPath}");
                }

                string sourceManifestPath = Path.Combine(resourcesFolderPath, "AppxManifest.xml");
                string sourceProviderPath = Path.Combine(
                    resourcesFolderPath,
                    "FeedProvider",
                    AppDataPaths.ProviderArchitectureFolder,
                    "FeedProvider.exe");
                if (!File.Exists(sourceManifestPath) || !File.Exists(sourceProviderPath))
                {
                    throw new FileNotFoundException(
                        $"当前安装包缺少源提供程序资源。Resources={resourcesFolderPath}; " +
                        $"Manifest={sourceManifestPath}; Provider={sourceProviderPath}");
                }
                // 创建用户目标文件夹
                Directory.CreateDirectory(UserAppFolder);
                // 复制所有文件和子文件夹
                await CopyDirectoryAsync(resourcesFolderPath, UserAppFolder);
                await CopyDirectoryAsync(assetsFolderPath, Path.Combine(UserAppFolder, "Assets"));
                SynchronizeManifestExecutablePath();
                if (!File.Exists(AppDataPaths.ManifestPath) || !File.Exists(AppDataPaths.ProviderExecutablePath))
                {
                    throw new FileNotFoundException(
                        $"资源复制完成后找不到 FeedProvider 文件。Manifest={AppDataPaths.ManifestPath}; Executable={AppDataPaths.ProviderExecutablePath}");
                }
                // 写入标志文件（版本号），表示首次初始化完成
                File.WriteAllText(FlagFilePath, GetCurrentVersion());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to copy resources: {ex.Message}");
                throw;
            }
            finally
            {
                CopyGate.Release();
            }
        }

        private static void SynchronizeManifestExecutablePath()
        {
            XNamespace foundationNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            XNamespace uapNamespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
            XNamespace uap3Namespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
            XNamespace comNamespace = "http://schemas.microsoft.com/appx/manifest/com/windows10";
            string processorArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string displayName = ProviderPackageDisplayName;
            string relativeExecutablePath = Path.Combine(
                "FeedProvider",
                AppDataPaths.ProviderArchitectureFolder,
                "FeedProvider.exe").Replace(Path.DirectorySeparatorChar, '\\');

            var document = XDocument.Load(AppDataPaths.ManifestPath);
            var root = document.Root
                ?? throw new InvalidDataException("源提供程序清单缺少 Package 根节点。");
            var identity = root.Element(foundationNamespace + "Identity")
                ?? throw new InvalidDataException("源提供程序清单中缺少 Identity 节点。");
            identity.SetAttributeValue("ProcessorArchitecture", processorArchitecture);

            var properties = root.Element(foundationNamespace + "Properties")
                ?? throw new InvalidDataException("源提供程序清单中缺少 Properties 节点。");
            SetElementValue(properties, foundationNamespace + "DisplayName", displayName);
            SetElementValue(properties, foundationNamespace + "PublisherDisplayName", ProviderPublisherDisplayName);
            SetElementValue(properties, foundationNamespace + "Logo", "Assets\\StoreLogo.scale-200.png");

            var application = root.Element(foundationNamespace + "Applications")?.Element(foundationNamespace + "Application")
                ?? throw new InvalidDataException("源提供程序清单中缺少 Application 节点。");

            application.SetAttributeValue("Executable", relativeExecutablePath);
            var visualElements = application.Elements().FirstOrDefault(element =>
                    element.Name.LocalName == "VisualElements")
                ?? throw new InvalidDataException("源提供程序清单中缺少 VisualElements 节点。");

            // uap3:VisualElements exposes AppListEntry, which keeps this COM/feed
            // host package out of Start while retaining its package identity.
            visualElements.Name = uap3Namespace + "VisualElements";
            visualElements.SetAttributeValue("DisplayName", displayName);
            visualElements.SetAttributeValue("Description", displayName);
            visualElements.SetAttributeValue("Square150x150Logo", "Assets\\Square150x150Logo.scale-200.png");
            visualElements.SetAttributeValue("Square44x44Logo", "Assets\\Square44x44Logo.scale-200.png");
            visualElements.SetAttributeValue("AppListEntry", "none");

            var defaultTile = visualElements.Elements().FirstOrDefault(element =>
                element.Name.LocalName == "DefaultTile");
            if (defaultTile is not null)
            {
                defaultTile.Name = uapNamespace + "DefaultTile";
                defaultTile.SetAttributeValue("Square71x71Logo", "Assets\\SmallTile.scale-200.png");
                defaultTile.SetAttributeValue("Wide310x150Logo", "Assets\\WideTile.scale-200.png");
                defaultTile.SetAttributeValue("Square310x310Logo", "Assets\\LargeTile.scale-200.png");
            }

            var splashScreen = visualElements.Elements().FirstOrDefault(element =>
                element.Name.LocalName == "SplashScreen");
            if (splashScreen is not null)
            {
                splashScreen.Name = uapNamespace + "SplashScreen";
                splashScreen.SetAttributeValue("Image", "Assets\\SplashScreen.scale-200.png");
            }

            foreach (var exeServer in application.Descendants(comNamespace + "ExeServer"))
            {
                exeServer.SetAttributeValue("Executable", relativeExecutablePath);
                exeServer.SetAttributeValue("DisplayName", displayName);
                foreach (var comClass in exeServer.Elements(comNamespace + "Class"))
                {
                    comClass.SetAttributeValue("DisplayName", displayName);
                }
            }

            foreach (var appExtension in application
                .Descendants(uap3Namespace + "AppExtension")
                .Where(element => element.Attribute("Id")?.Value == "feedcustomizer"))
            {
                appExtension.SetAttributeValue("DisplayName", displayName);
                var provider = appExtension
                    .Descendants(foundationNamespace + "FeedProvider")
                    .FirstOrDefault();
                if (provider is not null)
                {
                    provider.SetAttributeValue("DisplayName", displayName);
                    provider.SetAttributeValue("Description", displayName);
                    provider.SetAttributeValue("Icon", "Assets\\StoreLogo.scale-200.png");
                }
            }

            document.Save(AppDataPaths.ManifestPath);
        }

        private static void SetElementValue(XElement parent, XName elementName, string value)
        {
            var element = parent.Element(elementName)
                ?? throw new InvalidDataException($"源提供程序清单中缺少 {elementName.LocalName} 节点。");
            element.Value = value;
        }


        /// <summary>
        /// 检查资源是否已经更新（通过比较版本号）
        /// </summary>
        /// <returns></returns>
        public static async Task<bool> IsResourceUpToDate()
        {
            try
            {
                Debug.WriteLine($"Checking resource version. Flag file path: {FlagFilePath}");
                if (!File.Exists(FlagFilePath) ||
                    !File.Exists(AppDataPaths.ManifestPath) ||
                    !File.Exists(AppDataPaths.ProviderExecutablePath))
                {
                    return false; // 标志文件不存在，资源未初始化
                }
                string currentVersion = GetCurrentVersion();
                string savedVersion = File.ReadAllText(FlagFilePath);
                return currentVersion == savedVersion;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking resource version: {ex.Message}");
                return false; // 出现异常时，认为资源未更新
            }
        }

        /// <summary>
        /// 确保注册所需的清单和当前架构 FeedProvider 已经存在。
        /// </summary>
        public static async Task EnsureResourcesReadyAsync()
        {
            if (!File.Exists(AppDataPaths.ManifestPath) ||
                !File.Exists(AppDataPaths.ProviderExecutablePath) ||
                !await IsResourceUpToDate())
            {
                await ResourcesCopyAsync();
            }
            else
            {
                // Package versions can remain unchanged during development. Always
                // refresh the provider runtime without replacing the user's manifest
                // definitions or downloaded icons.
                await SynchronizeProviderFilesAsync();
            }

            if (!File.Exists(AppDataPaths.ManifestPath) ||
                !File.Exists(AppDataPaths.ProviderExecutablePath))
            {
                throw new FileNotFoundException(
                    $"资源复制后仍缺少注册文件。Manifest={AppDataPaths.ManifestPath}; " +
                    $"Provider={AppDataPaths.ProviderExecutablePath}");
            }
        }

        private static async Task SynchronizeProviderFilesAsync()
        {
            await CopyGate.WaitAsync();
            try
            {
                string sourceProviderFolder = Path.Combine(
                    GetResourcesFolderPath(),
                    "FeedProvider",
                    AppDataPaths.ProviderArchitectureFolder);
                if (!Directory.Exists(sourceProviderFolder))
                {
                    throw new DirectoryNotFoundException(
                        $"当前安装包缺少源提供程序目录：{sourceProviderFolder}");
                }

                string destinationProviderFolder = Path.Combine(
                    UserAppFolder,
                    "FeedProvider",
                    AppDataPaths.ProviderArchitectureFolder);
                Directory.CreateDirectory(destinationProviderFolder);
                await CopyDirectoryAsync(sourceProviderFolder, destinationProviderFolder);

                string sourceAssetsFolder = GetAssetsFolderPath();
                if (!Directory.Exists(sourceAssetsFolder))
                {
                    throw new DirectoryNotFoundException(
                        $"当前安装包缺少图标资源目录：{sourceAssetsFolder}");
                }

                await CopyDirectoryAsync(
                    sourceAssetsFolder,
                    Path.Combine(UserAppFolder, "Assets"));
                SynchronizeManifestExecutablePath();
            }
            finally
            {
                CopyGate.Release();
            }
        }

        public static bool IsRegisteredProviderCurrent()
        {
            string sourceProviderFolder = Path.Combine(
                GetResourcesFolderPath(),
                "FeedProvider",
                AppDataPaths.ProviderArchitectureFolder);
            string registeredProviderFolder = Path.Combine(
                AppDataPaths.RegistrationFolder,
                "FeedProvider",
                AppDataPaths.ProviderArchitectureFolder);

            string[] requiredFiles =
            [
                "FeedProvider.exe",
                "FeedProvider.dll",
                "FeedProvider.runtimeconfig.json"
            ];

            bool providerFilesAreCurrent = requiredFiles.All(fileName => FilesHaveSameContent(
                Path.Combine(sourceProviderFolder, fileName),
                Path.Combine(registeredProviderFolder, fileName)));

            return providerFilesAreCurrent &&
                IsManifestPresentationCurrent(AppDataPaths.ManifestPath) &&
                FilesHaveSameContent(AppDataPaths.ManifestPath, AppDataPaths.RegistrationManifestPath) &&
                DirectoryFilesHaveSameContent(
                    Path.Combine(UserAppFolder, "Assets"),
                    Path.Combine(AppDataPaths.RegistrationFolder, "Assets"));
        }

        private static bool IsManifestPresentationCurrent(string manifestPath)
        {
            try
            {
                XNamespace foundationNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                XNamespace uap3Namespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
                var document = XDocument.Load(manifestPath);
                var root = document.Root;
                var properties = root?.Element(foundationNamespace + "Properties");
                var application = root?
                    .Element(foundationNamespace + "Applications")?
                    .Element(foundationNamespace + "Application");
                var visualElements = application?.Element(uap3Namespace + "VisualElements");
                if (properties is null || visualElements is null)
                {
                    return false;
                }

                return properties.Element(foundationNamespace + "DisplayName")?.Value == ProviderPackageDisplayName &&
                    properties.Element(foundationNamespace + "PublisherDisplayName")?.Value == ProviderPublisherDisplayName &&
                    properties.Element(foundationNamespace + "Logo")?.Value == "Assets\\StoreLogo.scale-200.png" &&
                    visualElements.Attribute("DisplayName")?.Value == ProviderPackageDisplayName &&
                    visualElements.Attribute("AppListEntry")?.Value == "none" &&
                    visualElements.Attribute("Square150x150Logo")?.Value == "Assets\\Square150x150Logo.scale-200.png" &&
                    visualElements.Attribute("Square44x44Logo")?.Value == "Assets\\Square44x44Logo.scale-200.png";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to inspect provider package manifest presentation: {ex.Message}");
                return false;
            }
        }

        private static bool DirectoryFilesHaveSameContent(string sourceFolder, string destinationFolder)
        {
            if (!Directory.Exists(sourceFolder) || !Directory.Exists(destinationFolder))
            {
                return false;
            }

            return Directory
                .GetFiles(sourceFolder, "*", SearchOption.AllDirectories)
                .All(sourcePath => FilesHaveSameContent(
                    sourcePath,
                    Path.Combine(destinationFolder, Path.GetRelativePath(sourceFolder, sourcePath))));
        }

        private static bool FilesHaveSameContent(string firstPath, string secondPath)
        {
            if (!File.Exists(firstPath) || !File.Exists(secondPath))
            {
                return false;
            }

            var firstInfo = new FileInfo(firstPath);
            var secondInfo = new FileInfo(secondPath);
            if (firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            using FileStream firstStream = File.OpenRead(firstPath);
            using FileStream secondStream = File.OpenRead(secondPath);
            byte[] firstHash = SHA256.HashData(firstStream);
            byte[] secondHash = SHA256.HashData(secondStream);
            return firstHash.AsSpan().SequenceEqual(secondHash);
        }

        /// <summary>
        /// 获取应用包中的 Resources 文件夹的路径
        /// </summary>
        /// <returns></returns>
        private static string GetResourcesFolderPath()
        {
            return Path.Combine(GetPackageFolderPath(), "Resources");
        }

        private static string GetAssetsFolderPath()
        {
            return Path.Combine(GetPackageFolderPath(), "Assets");
        }

        private static string GetPackageFolderPath()
        {
            string packageFolder;
            try
            {
                packageFolder = Package.Current.InstalledLocation.Path;
            }
            catch
            {
                // Unpackaged/debug launch fallback.
                packageFolder = AppContext.BaseDirectory;
            }

            return packageFolder;
        }

        /// <summary>
        /// 异步复制目录及其内容
        /// </summary>
        /// <param name="sourceDir">源文件目录</param>
        /// <param name="destDir">目标文件目录</param>
        /// <returns></returns>
        private static async Task CopyDirectoryAsync(string sourceDir, string destDir)
        {
            // 复制所有文件
            foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                // 计算相对路径
                var relativePath = Path.GetRelativePath(sourceDir, filePath);
                var destFilePath = Path.Combine(destDir, relativePath);

                if (Path.GetDirectoryName(destFilePath) is string path)
                {
                    // 确保目标子目录存在
                    Directory.CreateDirectory(path);
                }

                // 复制文件（覆盖已存在的）
                File.Copy(filePath, destFilePath, true);
            }

            // 可选：复制文件夹结构（上面已经用 SearchOption.AllDirectories 处理了）
            await Task.CompletedTask;
        }

        private static string GetCurrentVersion()
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
    }
}
