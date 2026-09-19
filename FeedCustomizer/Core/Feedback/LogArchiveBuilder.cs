using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Feedback;

/// <summary>
/// 将一轮日志快照写入 ZIP。该类型不接触下载目录，便于使用临时目录验证锁定、筛选和完整性行为。
/// </summary>
internal static class LogArchiveBuilder
{
    private const string LogSearchPattern = "FeedCustomizer-*.log";
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// 将日志目录顶层的应用日志写入目标流。每个文件只复制打开瞬间的长度，
    /// 防止当前日志持续追加导致导出永不结束。
    /// </summary>
    internal static async Task<int> CreateAsync(
        string logDirectory,
        Stream destination,
        string emptyArchiveInformation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(destination);

        string fullLogDirectory = Path.GetFullPath(logDirectory);
        IReadOnlyList<string> logPaths = Directory.Exists(fullLogDirectory)
            ? Directory.EnumerateFiles(fullLogDirectory, LogSearchPattern, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        if (logPaths.Count == 0)
        {
            // 即使文件日志初始化失败，也生成可附加的诊断包，让开发者知道“没有日志”本身就是诊断结果。
            ZipArchiveEntry informationEntry = archive.CreateEntry("ExportInfo.txt", CompressionLevel.Optimal);
            await using Stream informationStream = informationEntry.Open();
            await using var writer = new StreamWriter(
                informationStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: false);
            await writer.WriteAsync(emptyArchiveInformation.AsMemory(), cancellationToken);
            return 0;
        }

        foreach (string logPath in logPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CopySnapshotAsync(archive, logPath, cancellationToken);
        }

        return logPaths.Count;
    }

    private static async Task CopySnapshotAsync(
        ZipArchive archive,
        string logPath,
        CancellationToken cancellationToken)
    {
        // Serilog 仍可能追加或滚动当前文件。共享读取与固定长度快照可以在不中断日志的前提下取得一致边界。
        await using var source = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long remaining = source.Length;
        ZipArchiveEntry entry = archive.CreateEntry(Path.GetFileName(logPath), CompressionLevel.Optimal);
        await using Stream entryStream = entry.Open();
        byte[] buffer = new byte[CopyBufferSize];

        while (remaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException($"日志快照在复制完成前被截断：{Path.GetFileName(logPath)}");
            }

            await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }
}
