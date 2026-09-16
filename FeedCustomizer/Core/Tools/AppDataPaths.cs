using System;
using System.IO;
using Windows.Storage;

namespace FeedCustomizer.Core.Tools
{
    internal static class AppDataPaths
    {
        // 这是注册目录的逻辑地址，不保证应用进程直接访问时绕过 MSIX 重定向。
        // 实际部署文件的读写由包外 PowerShell 完成；用户数据始终使用下面的私有目录。
        internal static string FeedProviderFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FeedCustomProvider");

        // 保持已发布版本的目录选择与回退顺序，更新时无需搬迁配置或图片。
        internal static string PackageLocalBase
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalCacheFolder.Path;
                }
                catch
                {
                    try
                    {
                        return ApplicationData.Current.LocalFolder.Path;
                    }
                    catch
                    {
                        return Path.Combine(AppContext.BaseDirectory, "LocalCache");
                    }
                }
            }
        }

        internal static string PackageLocalFeedProviderFolder => Path.Combine(PackageLocalBase, "Local", "FeedCustomProvider");

        internal static string PackageLocalManifestPath => Path.Combine(PackageLocalFeedProviderFolder, "AppxManifest.xml");

        internal static string PackageLocalProviderExecutablePath => Path.Combine(PackageLocalFeedProviderFolder, "FeedProvider", "FeedProvider.exe");

        internal static string PackageLocalLogPath => Path.Combine(PackageLocalBase, "Local", "Logs");

        internal static string ManifestPath => Path.Combine(
            FeedProviderFolder,
            "AppxManifest.xml");

        internal static string ProviderExecutablePath => Path.Combine(
            FeedProviderFolder,
            "FeedProvider",
            "FeedProvider.exe");

        internal static string ImagesFolder => Path.Combine(
            PackageLocalFeedProviderFolder,
            "Images");

        internal static string HelpDocFolder => Path.Combine(
            PackageLocalFeedProviderFolder,
            "HelpDoc");
    }
}
