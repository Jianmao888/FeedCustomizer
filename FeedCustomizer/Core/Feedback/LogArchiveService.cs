using AppConstants = FeedCustomizer.Core.Constants.Constants;
using FeedCustomizer.Core.Infrastructure.Feedback;
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

    /// <summary>将日志写入真实 Downloads\FeedCustomizer 目录，并返回可直接打开和附加的完整路径。</summary>
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
        string exportDirectory = string.Empty;
        string exportedPath = string.Empty;

        try
        {
            Log.Information("开始导出日志归档，目标文件={ArchiveName}", fileName);
            // 临时文件名完全由应用生成，并再次验证仍位于私有临时目录，避免清理路径越界。
            LogArchiveDestination.EnsureChildPath(temporaryFolder, temporaryPath);
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

            string downloadsDirectory = WindowsDownloadsDirectory.GetPath();
            exportDirectory = LogArchiveDestination.CreateDirectoryPath(
                downloadsDirectory,
                AppConstants.FeedbackExportFolderName);
            Directory.CreateDirectory(exportDirectory);

            await using (Stream source = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destination = CreateExportDestinationStream(
                exportDirectory,
                fileName,
                out exportedPath))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            Log.Information(
                "日志归档已导出，文件={ArchiveName}，日志数量={LogFileCount}",
                Path.GetFileName(exportedPath),
                logFileCount);
            return LogArchiveResult.Success(
                Path.GetFileName(exportedPath),
                exportedPath,
                logFileCount);
        }
        catch (OperationCanceledException)
        {
            DeleteIncompleteExport(exportDirectory, exportedPath);

            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出日志归档失败");
            DeleteIncompleteExport(exportDirectory, exportedPath);

            return LogArchiveResult.Failure(LogPrivacy.RedactException(ex));
        }
        finally
        {
            DeleteTemporaryArchive(temporaryFolder, temporaryPath);
        }
    }

    private static FileStream CreateExportDestinationStream(
        string exportDirectory,
        string requestedFileName,
        out string exportedPath)
    {
        const int maximumCollisionCount = 100;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(requestedFileName);
        string extension = Path.GetExtension(requestedFileName);

        for (int collisionIndex = 0; collisionIndex < maximumCollisionCount; collisionIndex++)
        {
            string fileName = collisionIndex == 0
                ? requestedFileName
                : $"{fileNameWithoutExtension} ({collisionIndex}){extension}";
            string candidatePath = Path.Combine(exportDirectory, fileName);
            LogArchiveDestination.EnsureChildPath(exportDirectory, candidatePath);

            try
            {
                FileStream stream = new(
                    candidatePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                exportedPath = candidatePath;
                return stream;
            }
            catch (IOException) when (File.Exists(candidatePath))
            {
                // 与 DownloadsFolder.GenerateUniqueName 等价：只对已存在的同名文件有限次改名重试。
            }
        }

        throw new IOException("无法在下载目录中分配唯一的日志归档文件名。");
    }

    private static void DeleteIncompleteExport(string exportDirectory, string exportedPath)
    {
        if (string.IsNullOrWhiteSpace(exportDirectory) || string.IsNullOrWhiteSpace(exportedPath))
        {
            return;
        }

        try
        {
            LogArchiveDestination.EnsureChildPath(exportDirectory, exportedPath);
            if (File.Exists(exportedPath))
            {
                File.Delete(exportedPath);
            }
        }
        catch (Exception cleanupException)
        {
            // 下载文件删除失败不能覆盖原始导出异常；只记录文件名，避免将完整用户路径写入日志。
            Log.Warning(
                cleanupException,
                "删除不完整日志归档失败，文件={ArchiveName}",
                Path.GetFileName(exportedPath));
        }
    }

    private static void DeleteTemporaryArchive(string temporaryFolder, string temporaryPath)
    {
        try
        {
            LogArchiveDestination.EnsureChildPath(temporaryFolder, temporaryPath);
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
