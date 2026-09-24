namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// Provider 普通注册命令的唯一脚本来源，由组合器加入发布后的同一进程。
    /// </summary>
    internal static class AppxPackagePowerShellScript
    {
        /// <summary>仅生成普通注册片段，保留现有参数转义和注册选项。</summary>
        internal static string CreateRegisterBody(string manifestPath)
        {
            return $"Add-AppxPackage -Register -ForceApplicationShutdown -ErrorAction Stop {PowerShellLiteral.Quote(manifestPath)}";
        }
    }
}
