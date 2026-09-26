namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// Provider 注册命令的唯一脚本来源，普通与临时开发者模式注册均复用此片段。
    /// </summary>
    internal static class AppxPackagePowerShellScript
    {
        /// <summary>仅生成注册片段，权限和临时设置由外部包装，保留现有参数转义和注册选项。</summary>
        internal static string CreateRegisterBody(string manifestPath)
        {
            return $"Add-AppxPackage -Register -ForceApplicationShutdown -ErrorAction Stop {PowerShellLiteral.Quote(manifestPath)}";
        }
    }
}
