using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Tools;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace FeedCustomizer.Core.Feedback;

/// <summary>
/// 将私有日志目录中的全部保留日志导出到用户下载目录。ZIP 先在应用临时目录完整构建，
/// 任意失败都会删除临时文件和不完整的下载文件。
/// </summary>
internal sealed class LogArchiveService
{
    private static readonly IAppLog Log = AppLog.For<LogArchiveService>();

    /// <summary>导出日志并返回 Windows 实际创建的完整路径。</summary>
    internal async Task<LogArchiveResult> ExportToDownloadsAsync(
        string emptyArchiveInformation,
        CancellationToken cancellationToken = default)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string fileName = $"FeedCustomizer-Logs-{timestamp}.zip";
        string temporaryFolder = ApplicationData.Current.TemporaryFolder.Path;
        string temporaryPath = Path.Combine(
            temporaryFolder,
            $"FeedCustomizer-Logs-{Guid.NewGuid():N}.tmp");
        StorageFile? exportedFile = null;

        try
        {
            Log.Information("开始导出日志归档，目标文件={ArchiveName}", fileName);
            // 临时文件名完全由应用生成，并再次验证仍位于私有临时目录，避免清理路径越界。
            EnsureChildPath(temporaryFolder, temporaryPath);
            int logFileCount;
            await using (var temporaryStream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                logFileCount = await LogArchiveBuilder.CreateAsync(
                    FeedCustomizer.Core.Tools.AppDataPaths.PackageLocalLogPath,
                    temporaryStream,
                    emptyArchiveInformation,
                    cancellationToken);
                await temporaryStream.FlushAsync(cancellationToken);
            }

            exportedFile = await DownloadsFolder.CreateFileAsync(
                fileName,
                CreationCollisionOption.GenerateUniqueName);
            await using (Stream source = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (Stream destination = await exportedFile.OpenStreamForWriteAsync())
            {
                destination.SetLength(0);
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            string fullPath = string.IsNullOrWhiteSpace(exportedFile.Path)
                ? exportedFile.Name
                : exportedFile.Path;
            Log.Information(
                "日志归档已导出，文件={ArchiveName}，日志数量={LogFileCount}",
                exportedFile.Name,
                logFileCount);
            return LogArchiveResult.Success(exportedFile.Name, fullPath, logFileCount);
        }
        catch (OperationCanceledException)
        {
            if (exportedFile is not null)
            {
                await DeleteIncompleteExportAsync(exportedFile);
            }

            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出日志归档失败");
            if (exportedFile is not null)
            {
                await DeleteIncompleteExportAsync(exportedFile);
            }

            return LogArchiveResult.Failure(LogPrivacy.RedactException(ex));
        }
        finally
        {
            DeleteTemporaryArchive(temporaryFolder, temporaryPath);
        }
    }

    private static void EnsureChildPath(string parentPath, string childPath)
    {
        string fullParent = Path.GetFullPath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullChild = Path.GetFullPath(childPath);
        if (!fullChild.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("日志归档临时路径超出应用私有临时目录。");
        }
    }

    private static async Task DeleteIncompleteExportAsync(StorageFile exportedFile)
    {
        try
        {
            await exportedFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch (Exception cleanupException)
        {
            // 下载文件删除失败不能覆盖原始导出异常；文件名足以提示开发者人工检查，不记录完整用户路径。
            Log.Warning(
                cleanupException,
                "删除不完整日志归档失败，文件={ArchiveName}",
                exportedFile.Name);
        }
    }

    private static void DeleteTemporaryArchive(string temporaryFolder, string temporaryPath)
    {
        try
        {
            EnsureChildPath(temporaryFolder, temporaryPath);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception cleanupException)
        {
            // 临时文件位于应用私有临时目录，清理失败记录警告并交由系统临时目录策略兜底。
            Log.Warning(cleanupException, "清理日志归档临时文件失败");
        }
    }
}
