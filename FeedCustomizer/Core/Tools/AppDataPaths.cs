using System;
using System.IO;
using Windows.Storage;

namespace FeedCustomizer.Core.Tools
{
    internal static class AppDataPaths
    {
        // The provider cache is ordinary per-user data. Use the real Local
        // AppData folder (not the package's LocalCache) so the out-of-package
        // PowerShell registration process reads and registers the same files.
        // This app runs full trust, so its writes here are not redirected by
        // the MSIX container.
        internal static string FeedProviderFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FeedCustomProvider");

        // Package-local folder inside the app container where the app should
        // store user-editable copies before they are published to the real
        // LocalAppData. Prefer LocalCacheFolder and fall back to LocalFolder.
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
            FeedProviderFolder,
            "Images");

        internal static string HelpDocFolder => Path.Combine(
            FeedProviderFolder,
            "HelpDoc");
    }
}
