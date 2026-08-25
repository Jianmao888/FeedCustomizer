using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FeedCustomizer.Models;
using Windows.ApplicationModel;

namespace FeedCustomizer.Core.Tools
{
    public class ResourcesCopier
    {
        private static string UserAppFolder => AppDataPaths.FeedProviderFolder;

        // Native AOT publishes the provider as a single executable.  The
        // framework DLL and runtimeconfig files that existed in older
        // CoreCLR publishes must not be treated as required files.
        private static readonly string[] RequiredProviderFiles =
        [
            "FeedProvider.exe"
        ];

        private static string FlagFilePath => Path.Combine(UserAppFolder, ".first_run_complete");
        private static string LegacyMigrationFlagFilePath => Path.Combine(UserAppFolder, ".legacy_cache_migrated");
        private static string LegacyManifestPath => Path.Combine(
            AppDataPaths.LegacyFeedProviderFolder,
            "AppxManifest.xml");
        private static string LegacyImagesFolder => Path.Combine(
            AppDataPaths.LegacyFeedProviderFolder,
            "Images");
        private static readonly SemaphoreSlim CopyGate = new(1, 1);
        private static readonly TimeSpan FileCopyRetryDelay = TimeSpan.FromMilliseconds(150);
        private const int FileCopyAttempts = 4;

        /// <summary>
        /// 检查并复制资源，在 App 启动时调用
        /// </summary>
        public static async Task ResourcesCopyAsync()
        {
            await CopyGate.WaitAsync();
            try
            {
                // AppxManifest.xml inside the installed package is only a
                // template and intentionally has an empty Definitions node.
                // Preserve the user's feeds before copying that template, then
                // serialize them back after the provider files are refreshed.
                // A previous build used the registration cache as its data
                // location. Read that cache once when the current manifest is
                // empty so an update cannot discard the user's feeds.
                List<Feed>? existingFeeds = null;
                bool sourceManifestWasReadable = false;
                string? sourceManifestBackupPath = null;
                if (File.Exists(AppDataPaths.ManifestPath))
                {
                    try
                    {
                        existingFeeds = await ManifestXmlService.Read(AppDataPaths.ManifestPath);
                        sourceManifestWasReadable = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to preserve existing feed definitions: {ex.Message}");
                        sourceManifestBackupPath = AppDataPaths.ManifestPath + ".backup";
                        if (!TryCopyFile(AppDataPaths.ManifestPath, sourceManifestBackupPath))
                        {
                            throw new IOException(
                                $"无法创建损坏清单的备份文件：{sourceManifestBackupPath}",
                                ex);
                        }
                    }
                }

                bool shouldCheckLegacyCache = !File.Exists(LegacyMigrationFlagFilePath) &&
                    (!sourceManifestWasReadable || existingFeeds is { Count: 0 });
                if (shouldCheckLegacyCache)
                {
                    List<Feed>? legacyFeeds = await TryReadFeedsAsync(LegacyManifestPath);
                    if (legacyFeeds is { Count: > 0 })
                    {
                        existingFeeds = legacyFeeds;
                        Debug.WriteLine($"Migrating {legacyFeeds.Count} feed definitions from the legacy registration cache.");
                    }
                }

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
                // CopyDirectoryAsync intentionally does not delete files.  A
                // previous non-AOT publish can therefore leave old runtime
                // files in the architecture-specific provider directory.
                // Only that directory is replaced; the manifest, definitions
                // and downloaded assets are user data and must remain intact.
                await ReplaceDirectoryContentsAsync(
                    Path.Combine(resourcesFolderPath, "FeedProvider"),
                    Path.Combine(UserAppFolder, "FeedProvider"));
                await CopyDirectoryAsync(assetsFolderPath, Path.Combine(UserAppFolder, "Assets"));

                // User-downloaded icons are data, not package resources. Copy
                // only files missing from the current cache so an old cache can
                // supply icons referenced by migrated definitions without
                // overwriting newer files.
                await CopyDirectoryIfMissingAsync(LegacyImagesFolder, AppDataPaths.ImagesFolder);

                if (sourceManifestBackupPath is not null && existingFeeds is null)
                {
                    // Keep an unreadable manifest available instead of silently
                    // replacing it with the package template. The backup can be
                    // recovered manually and the next startup will retry.
                    await CopyFileWithRetryAsync(sourceManifestBackupPath, AppDataPaths.ManifestPath);
                }
                else
                {
                    ManifestXmlService.SynchronizePresentation();
                    if (existingFeeds is not null)
                    {
                        // Write() also normalizes older manifests that had one
                        // AppExtension per feed into one provider with many
                        // Definitions.
                        await ManifestXmlService.Write(existingFeeds, AppDataPaths.ManifestPath);
                    }
                }
                if (!File.Exists(AppDataPaths.ManifestPath) || !File.Exists(AppDataPaths.ProviderExecutablePath))
                {
                    throw new FileNotFoundException(
                        $"资源复制完成后找不到 FeedProvider 文件。Manifest={AppDataPaths.ManifestPath}; Executable={AppDataPaths.ProviderExecutablePath}");
                }
                if (sourceManifestWasReadable || existingFeeds is not null)
                {
                    // This marker prevents a later intentional deletion of all
                    // feeds from resurrecting stale definitions in the legacy
                    // registration directory.
                    File.WriteAllText(LegacyMigrationFlagFilePath, "1");
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

                // Treat a malformed manifest as stale instead of replacing it
                // blindly on the next startup. ResourcesCopyAsync keeps a
                // backup and can recover from the legacy cache when available.
                List<Feed>? currentFeeds = await TryReadFeedsAsync(AppDataPaths.ManifestPath);
                if (currentFeeds is null)
                {
                    return false;
                }

                if (!File.Exists(LegacyMigrationFlagFilePath) && currentFeeds.Count == 0)
                {
                    List<Feed>? legacyFeeds = await TryReadFeedsAsync(LegacyManifestPath);
                    if (legacyFeeds is { Count: > 0 })
                    {
                        return false;
                    }
                }

                string currentVersion = GetCurrentVersion();
                string savedVersion = File.ReadAllText(FlagFilePath);
                string packagedProviderPath = Path.Combine(
                    GetResourcesFolderPath(),
                    "FeedProvider",
                    "FeedProvider.exe");

                // During local development and fixed-version MSIX rebuilds the
                // package version often remains 1.0.0.0. Comparing only the
                // flag file leaves an older AOT COM server in place forever.
                return currentVersion == savedVersion &&
                    FilesHaveSameContent(packagedProviderPath, AppDataPaths.ProviderExecutablePath);
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
                    "FeedProvider");
                if (!Directory.Exists(sourceProviderFolder))
                {
                    throw new DirectoryNotFoundException(
                        $"当前安装包缺少源提供程序目录：{sourceProviderFolder}");
                }

                string destinationProviderFolder = Path.Combine(
                    UserAppFolder,
                    "FeedProvider");
                await ReplaceDirectoryContentsAsync(sourceProviderFolder, destinationProviderFolder);

                string sourceAssetsFolder = GetAssetsFolderPath();
                if (!Directory.Exists(sourceAssetsFolder))
                {
                    throw new DirectoryNotFoundException(
                        $"当前安装包缺少图标资源目录：{sourceAssetsFolder}");
                }

                await CopyDirectoryAsync(
                    sourceAssetsFolder,
                    Path.Combine(UserAppFolder, "Assets"));
                ManifestXmlService.SynchronizePresentation();
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
                "FeedProvider");
            string registeredProviderFolder = Path.Combine(
                AppDataPaths.RegistrationFolder,
                "FeedProvider");

            bool providerFilesAreCurrent = RequiredProviderFiles.All(fileName => FilesHaveSameContent(
                Path.Combine(sourceProviderFolder, fileName),
                Path.Combine(registeredProviderFolder, fileName))) &&
                // A previous CoreCLR publish can leave dozens of runtime files
                // beside the AOT executable in the registration staging folder.
                // Treat that layout as stale so the installer gets one chance
                // to mirror the provider directory and remove the residue.
                DirectoryFilesExactlyMatch(sourceProviderFolder, registeredProviderFolder);

            return providerFilesAreCurrent &&
                ManifestXmlService.IsPresentationCurrent(AppDataPaths.ManifestPath) &&
                FilesHaveSameContent(AppDataPaths.ManifestPath, AppDataPaths.RegistrationManifestPath) &&
                DirectoryFilesHaveSameContent(
                    Path.Combine(UserAppFolder, "Assets"),
                    Path.Combine(AppDataPaths.RegistrationFolder, "Assets")) &&
                DirectoryFilesHaveSameContent(
                    AppDataPaths.ImagesFolder,
                    Path.Combine(AppDataPaths.RegistrationFolder, "Images"));
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

        private static bool DirectoryFilesExactlyMatch(string sourceFolder, string destinationFolder)
        {
            if (!Directory.Exists(sourceFolder) || !Directory.Exists(destinationFolder))
            {
                return false;
            }

            string[] sourceFiles = Directory
                .GetFiles(sourceFolder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(sourceFolder, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] destinationFiles = Directory
                .GetFiles(destinationFolder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(destinationFolder, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return sourceFiles.SequenceEqual(destinationFiles, StringComparer.OrdinalIgnoreCase) &&
                sourceFiles.All(relativePath => FilesHaveSameContent(
                    Path.Combine(sourceFolder, relativePath),
                    Path.Combine(destinationFolder, relativePath)));
        }

        private static bool FilesHaveSameContent(string firstPath, string secondPath)
        {
            try
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
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
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
            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceDir, filePath);
                string destFilePath = Path.Combine(destDir, relativePath);
                await CopyFileWithRetryAsync(filePath, destFilePath);
            }
        }

        private static async Task CopyDirectoryIfMissingAsync(string sourceDir, string destDir)
        {
            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceDir, filePath);
                string destFilePath = Path.Combine(destDir, relativePath);
                if (!File.Exists(destFilePath))
                {
                    await CopyFileWithRetryAsync(filePath, destFilePath);
                }
            }
        }

        private static async Task CopyFileWithRetryAsync(string sourcePath, string destinationPath)
        {
            if (PathsReferToSameFile(sourcePath, destinationPath) ||
                (File.Exists(destinationPath) && FilesHaveSameContent(sourcePath, destinationPath)))
            {
                return;
            }

            if (Path.GetDirectoryName(destinationPath) is string destinationDirectory)
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            Exception? lastException = null;
            for (int attempt = 0; attempt < FileCopyAttempts; attempt++)
            {
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        File.SetAttributes(destinationPath, FileAttributes.Normal);
                    }

                    File.Copy(sourcePath, destinationPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < FileCopyAttempts - 1)
                {
                    lastException = ex;
                    await Task.Delay(FileCopyRetryDelay);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            throw new IOException(
                $"复制资源失败：{sourcePath} -> {destinationPath}",
                lastException);
        }

        /// <summary>
        /// Makes a dedicated provider directory match the packaged provider
        /// exactly, removing files left by an older CoreCLR publish first.
        /// This is deliberately limited to the provider architecture folder;
        /// user manifests and downloaded images live elsewhere.
        /// </summary>
        private static async Task ReplaceDirectoryContentsAsync(string sourceDir, string destDir)
        {
            if (!Directory.Exists(sourceDir))
            {
                throw new DirectoryNotFoundException($"源提供程序目录不存在：{sourceDir}");
            }

            Directory.CreateDirectory(destDir);

            foreach (string destinationFile in Directory.GetFiles(destDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(destDir, destinationFile);
                string sourceFile = Path.Combine(sourceDir, relativePath);
                if (!File.Exists(sourceFile))
                {
                    await DeleteFileWithRetryAsync(destinationFile);
                }
            }

            foreach (string destinationDirectory in Directory
                .GetDirectories(destDir, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length))
            {
                string relativePath = Path.GetRelativePath(destDir, destinationDirectory);
                string sourceDirectory = Path.Combine(sourceDir, relativePath);
                if (!Directory.Exists(sourceDirectory))
                {
                    await DeleteDirectoryWithRetryAsync(destinationDirectory);
                }
            }

            await CopyDirectoryAsync(sourceDir, destDir);
        }

        private static async Task DeleteFileWithRetryAsync(string path)
        {
            Exception? lastException = null;
            for (int attempt = 0; attempt < FileCopyAttempts; attempt++)
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return;
                }
                catch (Exception ex) when (attempt < FileCopyAttempts - 1)
                {
                    lastException = ex;
                    await Task.Delay(FileCopyRetryDelay);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            throw new IOException($"删除旧资源失败：{path}", lastException);
        }

        private static async Task DeleteDirectoryWithRetryAsync(string path)
        {
            Exception? lastException = null;
            for (int attempt = 0; attempt < FileCopyAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (Exception ex) when (attempt < FileCopyAttempts - 1)
                {
                    lastException = ex;
                    await Task.Delay(FileCopyRetryDelay);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            throw new IOException($"删除旧资源目录失败：{path}", lastException);
        }

        private static async Task<List<Feed>?> TryReadFeedsAsync(string manifestPath)
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                return await ManifestXmlService.Read(manifestPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read feed manifest {manifestPath}: {ex.Message}");
                return null;
            }
        }

        private static bool TryCopyFile(string sourcePath, string destinationPath)
        {
            try
            {
                if (!File.Exists(sourcePath) || PathsReferToSameFile(sourcePath, destinationPath))
                {
                    return false;
                }

                if (Path.GetDirectoryName(destinationPath) is string directory)
                {
                    Directory.CreateDirectory(directory);
                }

                File.Copy(sourcePath, destinationPath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to preserve manifest backup: {ex.Message}");
                return false;
            }
        }

        private static bool PathsReferToSameFile(string firstPath, string secondPath)
        {
            return string.Equals(
                Path.GetFullPath(firstPath),
                Path.GetFullPath(secondPath),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetCurrentVersion()
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
    }
}
