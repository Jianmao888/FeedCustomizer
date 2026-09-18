using FeedCustomizer.Core.Tools;
using System;
using System.IO;

namespace FeedCustomizer.Core.Infrastructure.Logging;

/// <summary>
/// 集中保存文件日志的滚动与保留策略，避免调用方自行决定目录或清理范围。
/// </summary>
internal static class AppLogConfiguration
{
    internal const long FileSizeLimitBytes = 2 * 1024 * 1024;
    internal const int RetainedFileCountLimit = 28;
    internal static readonly TimeSpan RetainedFileTimeLimit = TimeSpan.FromDays(14);

    internal const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] " +
        "[会话:{SessionId}] {Message:lj}{NewLine}";

    /// <summary>
    /// 返回 Serilog 的滚动文件模板。目录沿用应用已有的 MSIX 私有数据路径，
    /// 文件前缀固定后，Sink 的保留清理不会触碰同目录中的地区策略诊断文件。
    /// </summary>
    internal static string GetRollingFilePath()
    {
        Directory.CreateDirectory(AppDataPaths.PackageLocalLogPath);
        return Path.Combine(AppDataPaths.PackageLocalLogPath, "FeedCustomizer-.log");
    }
}
