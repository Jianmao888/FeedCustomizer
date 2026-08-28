using FeedCustomizer.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace FeedCustomizer.Core.Tools
{
    public class ResourcesCopier
    {
        private static string UserAppFolder => AppDataPaths.PackageLocalFeedProviderFolder;

        // Native AOT 发布会将提供程序发布为单个可执行文件。
        // 旧版 CoreCLR 发布中的 framework DLL 和 runtimeconfig 文件不再需要，
        // 因此不能将它们当作提供程序的必需文件。
        private static readonly string[] RequiredProviderFiles =
        [
            "FeedProvider.exe"
        ];

        private static string FlagFilePath => Path.Combine(UserAppFolder, ".first_run_complete");
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
                // 安装包中的 AppxManifest.xml 只是模板，Definitions 节点按设计为空。
                // 复制模板前先保存暂存清单中的订阅源，刷新提供程序文件后再将订阅源写回清单。
                List<Feed>? existingFeeds = null;
                string? sourceManifestBackupPath = null;

                // 真实的 LocalAppData 清单只是注册副本，不作为应用源配置的读取来源。
                string existingManifestPath = AppDataPaths.PackageLocalManifestPath;

                if (File.Exists(existingManifestPath))
                {
                    try
                    {
                        existingFeeds = await ManifestXmlService.Read(existingManifestPath);
                    }
                    catch (Exception ex)
                    {
                        // 清单损坏时先保留原文件，避免后续复制模板时覆盖唯一的数据副本。
                        Debug.WriteLine($"Failed to preserve existing feed definitions: {ex.Message}");
                        sourceManifestBackupPath = existingManifestPath + ".backup";
                        if (!TryCopyFile(existingManifestPath, sourceManifestBackupPath))
                        {
                            throw new IOException(
                                $"无法创建损坏清单的备份文件：{sourceManifestBackupPath}",
                                ex);
                        }
                    }
                }

                // 获取安装包中只读的 Resources 文件夹路径。
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
                // 创建用户可写的包本地目标文件夹。
                Directory.CreateDirectory(UserAppFolder);
                // 复制资源目录中的文件和子目录；此操作不会删除目标中的旧文件。
                await CopyDirectoryAsync(resourcesFolderPath, UserAppFolder);
                // CopyDirectoryAsync 按设计不会删除文件，因此旧版非 AOT 发布可能会在
                // 与架构相关的提供程序目录中留下旧运行时文件。
                // 这里只替换提供程序目录；清单、订阅源定义和已下载的图标属于用户数据，
                // 必须保留。
                await ReplaceDirectoryContentsAsync(
                    Path.Combine(resourcesFolderPath, "FeedProvider"),
                    Path.Combine(UserAppFolder, "FeedProvider"));
                await CopyDirectoryAsync(assetsFolderPath, Path.Combine(UserAppFolder, "Assets"));

                if (sourceManifestBackupPath is not null && existingFeeds is null)
                {
                    // 清单无法读取时，将备份保留为包本地清单，而不是静默替换为模板。
                    // 用户可以手动恢复该备份；下次启动时程序也会再次尝试读取。
                    await CopyFileWithRetryAsync(sourceManifestBackupPath, AppDataPaths.PackageLocalManifestPath);
                }
                else
                {
                    ManifestXmlService.SynchronizePresentation();
                    if (existingFeeds is not null)
                    {
                        // Write() 还会规范化旧格式清单：将每个订阅源一个 AppExtension
                        // 的结构转换为一个提供程序及多个 Definitions 的结构。
                        await ManifestXmlService.Write(existingFeeds, AppDataPaths.PackageLocalManifestPath);
                    }
                }
                if (!File.Exists(AppDataPaths.PackageLocalManifestPath) || !File.Exists(AppDataPaths.PackageLocalProviderExecutablePath))
                {
                    throw new FileNotFoundException(
                        $"资源复制完成后找不到 FeedProvider 文件。Manifest={AppDataPaths.PackageLocalManifestPath}; Executable={AppDataPaths.PackageLocalProviderExecutablePath}");
                }
                // 写入标志文件（内容为当前版本号），表示资源初始化已完成。
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
                    !File.Exists(AppDataPaths.PackageLocalManifestPath) ||
                    !File.Exists(AppDataPaths.PackageLocalProviderExecutablePath))
                {
                    // 任一关键文件缺失，都视为资源尚未初始化完成。
                    return false;
                }

                // 清单格式错误时视为资源已过期，但不在此处直接覆盖清单，
                // 以免在启动检查阶段丢失用户数据。
                List<Feed>? currentFeeds = await TryReadFeedsAsync(AppDataPaths.PackageLocalManifestPath);
                if (currentFeeds is null)
                {
                    return false;
                }

                string currentVersion = GetCurrentVersion();
                string savedVersion = File.ReadAllText(FlagFilePath);
                string packagedProviderPath = Path.Combine(
                    GetResourcesFolderPath(),
                    "FeedProvider",
                    "FeedProvider.exe");

                // 在本地开发和固定版本号的 MSIX 重建过程中，安装包版本号经常仍为 1.0.0.0。
                // 如果只比较标志文件，旧版 AOT COM 服务器可能会一直被保留，
                // 因此还必须比较安装包和暂存目录中的可执行文件内容。
                return currentVersion == savedVersion &&
                    FilesHaveSameContent(packagedProviderPath, AppDataPaths.PackageLocalProviderExecutablePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking resource version: {ex.Message}");
                // 检查过程出现任何异常时，采取保守策略，要求重新同步资源。
                return false;
            }
        }

        /// <summary>
        /// 确保注册所需的清单和当前架构 FeedProvider 已经存在。
        /// </summary>
        public static async Task EnsureResourcesReadyAsync()
        {
            if (!File.Exists(AppDataPaths.PackageLocalManifestPath) ||
                !File.Exists(AppDataPaths.PackageLocalProviderExecutablePath) ||
                !await IsResourceUpToDate())
            {
                await ResourcesCopyAsync();
            }
            else
            {
                // 开发过程中安装包版本号可能保持不变，因此始终刷新提供程序运行时文件，
                // 但不替换用户的清单定义或已下载的图标。
                await SynchronizeProviderFilesAsync();
            }

            if (!File.Exists(AppDataPaths.PackageLocalManifestPath) ||
                !File.Exists(AppDataPaths.PackageLocalProviderExecutablePath))
            {
                throw new FileNotFoundException(
                    $"资源复制后仍缺少注册文件。Manifest={AppDataPaths.PackageLocalManifestPath}; " +
                    $"Provider={AppDataPaths.PackageLocalProviderExecutablePath}");
            }
        }

        private static async Task SynchronizeProviderFilesAsync()
        {
            // 与资源复制共用同一把锁，避免两个同步操作同时修改目标目录。
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
            // 已注册的包位于真实的 LocalAppData 目录，而用户编辑的内容暂存于包本地目录。
            // 只有已注册文件与暂存源完全一致时，注册状态才算最新；否则安装流程需要
            // 重新复制并重新注册。
            string stagedProviderFolder = Path.Combine(
                AppDataPaths.PackageLocalFeedProviderFolder,
                "FeedProvider");
            string registeredProviderFolder = Path.Combine(
                AppDataPaths.FeedProviderFolder,
                "FeedProvider");

            bool providerFilesAreCurrent = RequiredProviderFiles.All(fileName => FilesHaveSameContent(
                Path.Combine(stagedProviderFolder, fileName),
                Path.Combine(registeredProviderFolder, fileName)));

            return providerFilesAreCurrent &&
                FilesHaveSameContent(
                    AppDataPaths.PackageLocalManifestPath,
                    AppDataPaths.ManifestPath);
        }

        private static bool FilesHaveSameContent(string firstPath, string secondPath)
        {
            try
            {
                // 先检查文件是否存在和大小是否一致，再计算 SHA-256，
                // 以便快速排除明显不同的文件。
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
                // 哈希相同表示两个文件内容一致；这里比较的是内容，不是文件时间戳。
                return firstHash.AsSpan().SequenceEqual(secondHash);
            }
            catch (IOException)
            {
                // 文件可能正在被其他进程使用，无法读取时按“不一致”处理。
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // 没有访问权限时同样按“不一致”处理，交由上层触发同步。
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
            // 图标等静态资源位于安装包根目录下的 Assets 文件夹。
            return Path.Combine(GetPackageFolderPath(), "Assets");
        }

        private static string GetPackageFolderPath()
        {
            string packageFolder;
            try
            {
                // 正式打包运行时，从当前 MSIX 包获取安装目录。
                packageFolder = Package.Current.InstalledLocation.Path;
            }
            catch
            {
                // 未打包或调试启动时没有可用的 Package.Current，退回到程序基目录。
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
            // 源目录不存在时不报错，调用方可以根据具体场景决定是否提前校验。
            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                // 使用相对路径复制，确保源目录的层级结构在目标目录中保持不变。
                string relativePath = Path.GetRelativePath(sourceDir, filePath);
                string destFilePath = Path.Combine(destDir, relativePath);
                await CopyFileWithRetryAsync(filePath, destFilePath);
            }
        }

        private static async Task CopyFileWithRetryAsync(string sourcePath, string destinationPath)
        {
            // 同一文件无需复制；内容相同也无需重复写入，以减少文件锁冲突和磁盘操作。
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
                    // 目标文件可能带有只读属性，覆盖前先恢复为普通属性。
                    if (File.Exists(destinationPath))
                    {
                        File.SetAttributes(destinationPath, FileAttributes.Normal);
                    }

                    File.Copy(sourcePath, destinationPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < FileCopyAttempts - 1)
                {
                    // 文件可能暂时被系统或提供程序占用，短暂等待后重试。
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
            // 此方法用于让提供程序目录与安装包中的目录保持精确一致，
            // 包括删除旧版本遗留的文件和子目录。
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
                    // 目标中存在而源中不存在的文件，属于旧版本遗留文件。
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
                    // 目录按从深到浅的顺序删除，确保父目录为空后才能删除。
                    await DeleteDirectoryWithRetryAsync(destinationDirectory);
                }
            }

            await CopyDirectoryAsync(sourceDir, destDir);
        }

        private static async Task DeleteFileWithRetryAsync(string path)
        {
            // 删除操作也采用重试机制，因为正在运行的 COM 提供程序可能暂时锁定文件。
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
            // 删除目录时使用递归模式，处理目录中仍残留的旧文件。
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
            // 这是用于状态检查的容错读取：读取失败返回 null，
            // 不让启动检查直接中断应用。
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
                // 该方法只用于创建损坏清单的保护性备份，失败时由调用方决定是否终止流程。
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
            // 统一为绝对路径并忽略大小写，避免将同一文件误判为两个文件。
            return string.Equals(
                Path.GetFullPath(firstPath),
                Path.GetFullPath(secondPath),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetCurrentVersion()
        {
            // 使用安装包的四段版本号作为资源同步标记。
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
    }
}
