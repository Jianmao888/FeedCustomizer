using FeedCustomizer.Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// AppX 包注册基础设施适配器。调用方只表达注册、卸载和查询意图，
    /// 不接触 PowerShell cmdlet 或脚本转义。
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

        /// <summary>
        /// 卸载名称匹配的当前用户 AppX 包。
        /// </summary>
        internal Task<PowerShellResult> RemoveAsync(
            string packageNamePattern,
            CancellationToken cancellationToken = default)
        {
            string script = string.Join(
                System.Environment.NewLine,
                "$ErrorActionPreference = 'Stop'",
                $"Get-AppxPackage -Name {PowerShellLiteral.Quote(packageNamePattern)} | Remove-AppxPackage -ErrorAction Stop");
            return executor.ExecuteAsync(
                new PowerShellScript("RemoveAppxPackage.ps1", script),
                cancellationToken);
        }

        /// <summary>
        /// 查询名称匹配的包完整名称，供上层决定是否需要重建部署副本。
        /// </summary>
        internal Task<PowerShellResult> QueryFullNameAsync(
            string packageNamePattern,
            CancellationToken cancellationToken = default)
        {
            string script =
                $"$ErrorActionPreference = 'Stop'; Get-AppxPackage -Name {PowerShellLiteral.Quote(packageNamePattern)} -ErrorAction Stop | " +
                "Select-Object -ExpandProperty PackageFullName";
            return executor.ExecuteAsync(
                new PowerShellScript("QueryAppxPackage.ps1", script, Timeout: System.TimeSpan.FromSeconds(15)),
                cancellationToken);
        }
    }
}
