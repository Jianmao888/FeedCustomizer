using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.Deployment;

/// <summary>AppX 和 COM 服务器的平台实现；成功必须经过退出码/实际查询确认，不将失败伪装成未安装。</summary>
internal sealed class WindowsProviderRegistration : IProviderRegistrationPlatform
{
    // 使用确切包名，避免宽泛通配符匹配到用户安装的其他包。
    private const string PackageName = "D454B137.Jianmao.FeedCustomizerContainer";

    public async Task<bool> IsInstalledAsync(CancellationToken token)
    {
        var result = await PowerShellInfrastructure.AppxPackages.QueryFullNameAsync(PackageName, token);
        EnsureSuccess("QueryProvider", result);
        return !string.IsNullOrWhiteSpace(result.Output);
    }

    public async Task RemoveAsync(CancellationToken token)
    {
        await StopAsync(token);
        EnsureSuccess("RemoveProvider", await PowerShellInfrastructure.AppxPackages.RemoveAsync(PackageName, token));
        // AppX 移除可能延后可见。轮询有明确上限，超时直接报错，不能继续覆盖仍被注册的文件。
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!await IsInstalledAsync(token)) { await StopAsync(token); return; }
            await Task.Delay(200, token);
        }
        throw new TimeoutException("卸载 Provider 后仍能查询到注册信息。");
    }

    public async Task StopAsync(CancellationToken token)
    {
        string expected = Path.GetFullPath(AppDataPaths.ProviderExecutablePath);
        foreach (var process in Process.GetProcessesByName("FeedProvider"))
        {
            using (process)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) continue;
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch (InvalidOperationException) when (process.HasExited) { continue; }
                // 无法确认路径时绝不能仅凭进程名结束进程；读取失败向上返回，停止本次部署。
                if (executable is null) throw new IOException($"无法验证 Provider 进程路径：{process.Id}");
                if (!Path.GetFullPath(executable).Equals(expected, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // 本地 COM 服务器没有子进程，只终止已确认路径的实例，避免遍历受保护的系统进程树。
                    if (!process.HasExited) process.Kill();
                    if (!process.WaitForExit(3000)) throw new TimeoutException($"Provider 进程未退出：{process.Id}");
                }
                catch (InvalidOperationException) when (process.HasExited) { /* 查询与停止之间已自然退出，无需再操作。 */ }
            }
        }
        await Task.CompletedTask;
    }

    public async Task<ProviderRegistrationResult> RegisterAsync(bool allowDeveloperMode, CancellationToken token)
    {
        string manifest = AppDataPaths.ManifestPath;
        var result = await PowerShellInfrastructure.AppxPackages.RegisterAsync(manifest, token);
        if (result.ExitCode == 0) return ProviderRegistrationResult.Success(manifest);
        bool developerModeRequired = result.Error.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase);
        if (developerModeRequired)
        {
            if (!allowDeveloperMode)
                return new(ProviderRegistrationStatus.DeveloperModeConfirmationRequired, result.ExitCode, result.Error, result.Output, manifest);
            result = await PowerShellInfrastructure.DeveloperMode.RegisterPackageAsync(manifest, token);
            if (result.ExitCode == 0) return ProviderRegistrationResult.Success(manifest);
            if (result.ExitCode == PowerShellExitCodes.ElevationCancelled)
                return new(ProviderRegistrationStatus.ElevationCancelled, result.ExitCode, result.Error, result.Output, manifest);
        }
        return ProviderRegistrationResult.Failed(manifest, result.ExitCode, result.Error, result.Output);
    }

    private static void EnsureSuccess(string operation, PowerShellResult result)
    {
        if (result.ExitCode != 0)
            throw new IOException($"{operation}: ExitCode={result.ExitCode}; Error={result.Error}; Output={result.Output}");
    }
}
