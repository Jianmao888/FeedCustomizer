using System;

namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// 一次受控的 PowerShell 脚本执行请求。
    /// </summary>
    internal sealed record PowerShellScript(
        string FileName,
        string Content,
        bool RequiresElevation = false,
        TimeSpan? Timeout = null);

    /// <summary>
    /// PowerShell 进程使用的稳定退出码。
    /// </summary>
    internal static class PowerShellExitCodes
    {
        internal const int StartFailed = -1;
        internal const int TimedOut = -2;
        internal const int Cancelled = -3;
        internal const int ElevationCancelled = 1223;
    }

    /// <summary>
    /// 将外部值编码为 PowerShell 单引号字符串，避免各适配器重复实现转义规则。
    /// </summary>
    internal static class PowerShellLiteral
    {
        internal static string Quote(string value) => $"'{value.Replace("'", "''")}'";
    }
}
