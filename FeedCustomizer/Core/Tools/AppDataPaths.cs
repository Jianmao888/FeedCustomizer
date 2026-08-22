using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace FeedCustomizer.Core.Tools
{
    internal static class AppDataPaths
    {
        internal static string FeedProviderFolder => Path.Combine(
            GetPhysicalAppDataFolder(),
            "FeedCustomProvider");

        private static string GetPhysicalAppDataFolder()
        {
            try
            {
                // Environment.LocalApplicationData is virtualized for packaged
                // processes. PowerShell runs outside the package and therefore
                // cannot resolve that virtual path. LocalCacheFolder.Path is the
                // physical package-data path visible to both processes.
                return ApplicationData.Current.LocalCacheFolder.Path;
            }
            catch
            {
                // Unpackaged/debug launch fallback.
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
        }

        internal static string ManifestPath => Path.Combine(
            FeedProviderFolder,
            "AppxManifest.xml");

        // AppX deployment rejects manifests beneath another package's LocalCache
        // mount point. PowerShell stages the registration copy at this ordinary,
        // non-virtualized user path before calling Add-AppxPackage.
        internal static string RegistrationFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FeedCustomProvider");

        internal static string RegistrationManifestPath => Path.Combine(
            RegistrationFolder,
            "AppxManifest.xml");

        internal static string ProviderArchitectureFolder
        {
            get
            {
                var architecture = RuntimeInformation.ProcessArchitecture;
                if (architecture is not Architecture.X64 and not Architecture.Arm64)
                {
                    throw new PlatformNotSupportedException($"不支持的处理器架构：{architecture}");
                }

                return $"win-{architecture.ToString().ToLowerInvariant()}";
            }
        }

        // The package build selects the matching RID and places the provider
        // files directly under the stable FeedProvider directory.
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
