using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace FeedCustomizer.Core.Infrastructure.Deployment;

/// <summary>AppX 和 COM 服务器的平台实现；查询与卸载使用系统 API，注册仍使用包外 PowerShell。</summary>
internal sealed class WindowsProviderRegistration : IProviderRegistrationPlatform
{
    private static readonly IAppLog Log = AppLog.For<WindowsProviderRegistration>();

    // 与 Provider 模板清单的 Identity 保持一致，不能使用主应用的包身份。
    private const string PackageName = "D454B137.Jianmao.FeedCustomizerContainer";
    private const string PackagePublisher = "CN=jianm";

    /// <summary>查询当前用户是否注册了模板身份对应的 Provider；查询异常不能当作未注册。</summary>
    public Task<bool> IsInstalledAsync(CancellationToken token)
    {
        return Task.FromResult(FindPackageFullName(token) is not null);
    }

    /// <summary>停止本应用的 Provider 进程并卸载其当前用户注册，确认不可见后才允许发布新文件。</summary>
    public async Task RemoveAsync(CancellationToken token)
    {
        await StopAsync(token);

        string? fullName = FindPackageFullName(token);
        if (fullName is null)
        {
            return;
        }

        // Windows 不支持取消已发起的包卸载。必须等待它结束并确认状态，才能释放协调器的部署锁。
        token.ThrowIfCancellationRequested();
        Stopwatch stopwatch = Stopwatch.StartNew();
        IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress>? operation = null;
        try
        {
            operation = new PackageManager().RemovePackageAsync(fullName);
            await operation;
            Log.Information(
                "Provider 卸载 API 完成，包={PackageFullName}，耗时毫秒={ElapsedMilliseconds}",
                fullName,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            string diagnostic = "卸载 API 未返回扩展结果";
            if (operation is not null)
            {
                try
                {
                    var result = operation.GetResults();
                    string extendedErrorCode = result.ExtendedErrorCode is null
                        ? "（无）"
                        : $"0x{result.ExtendedErrorCode.HResult:X8}";
                    diagnostic = $"ActivityId={result.ActivityId}; ExtendedErrorCode={extendedErrorCode}; ErrorText={LogPrivacy.PrepareDiagnostic(result.ErrorText)}";
                }
                catch (Exception resultException)
                {
                    diagnostic = $"无法取得扩展结果，HRESULT=0x{resultException.HResult:X8}";
                }
            }

            Log.Error(
                ex,
                "Provider 卸载 API 失败，包={PackageFullName}，耗时毫秒={ElapsedMilliseconds}，HRESULT={HResult}，诊断={Diagnostic}",
                fullName,
                stopwatch.ElapsedMilliseconds,
                $"0x{ex.HResult:X8}",
                diagnostic);
            throw new IOException($"RemoveProvider: Package={fullName}; HRESULT=0x{ex.HResult:X8}; {diagnostic}", ex);
        }

        // AppX 移除可能延后可见。轮询有明确上限，超时直接报错，不能继续覆盖仍被注册的文件。
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (FindPackageFullName(CancellationToken.None) is null)
            {
                await StopAsync(CancellationToken.None);
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("卸载 Provider 后仍能查询到注册信息。");
    }

    /// <summary>按精确包名和发布者查找当前用户的完整包名，避免卸载其他用户或其他包。</summary>
    private static string? FindPackageFullName(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            string? fullName = null;
            foreach (Package package in new PackageManager().FindPackagesForUser(
                string.Empty,
                PackageName,
                PackagePublisher))
            {
                if (fullName is not null)
                {
                    throw new InvalidDataException("当前用户存在多个匹配的 Provider 包，无法安全选择卸载目标。");
                }

                fullName = package.Id.FullName;
            }

            token.ThrowIfCancellationRequested();
            Log.Debug(
                "Provider 包查询完成，包名={PackageName}，已注册={Installed}，耗时毫秒={ElapsedMilliseconds}",
                PackageName,
                fullName is not null,
                stopwatch.ElapsedMilliseconds);
            return fullName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(
                ex,
                "Provider 包查询失败，包名={PackageName}，耗时毫秒={ElapsedMilliseconds}，HRESULT={HResult}",
                PackageName,
                stopwatch.ElapsedMilliseconds,
                $"0x{ex.HResult:X8}");
            throw new IOException($"QueryProvider: Package={PackageName}; HRESULT=0x{ex.HResult:X8}", ex);
        }
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
}
