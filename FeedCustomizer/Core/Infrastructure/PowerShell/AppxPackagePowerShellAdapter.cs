using FeedCustomizer.Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// Provider 包注册的 PowerShell 适配器；查询与卸载由 PackageManager API 处理。
    /// </summary>
    internal sealed class AppxPackagePowerShellAdapter(IPowerShellExecutor executor)
    {
        /// <summary>
        /// 注册指定清单对应的 AppX 包，并由执行器统一返回退出码与诊断。
        /// </summary>
        internal Task<PowerShellResult> RegisterAsync(
            string manifestPath,
            CancellationToken cancellationToken = default)
        {
            string script = string.Join(
                System.Environment.NewLine,
                "$ErrorActionPreference = 'Stop'",
                $"Add-AppxPackage -Register -ForceApplicationShutdown -ErrorAction Stop {PowerShellLiteral.Quote(manifestPath)}");
            return executor.ExecuteAsync(
                new PowerShellScript("RegisterAppxPackage.ps1", script),
                cancellationToken);
        }
    }
}
