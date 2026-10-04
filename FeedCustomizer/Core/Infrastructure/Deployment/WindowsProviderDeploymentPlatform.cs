using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Infrastructure.Storage;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace FeedCustomizer.Core.Infrastructure.Deployment;

/// <summary>Provider 平台实现；查询与卸载使用系统 API，发布和注册在包外执行，提权仅覆盖临时设置与注册。</summary>
internal sealed class WindowsProviderDeploymentPlatform(ProviderDeploymentPowerShellAdapter deployed) : IProviderDeploymentPlatform
{
    private static readonly IAppLog Log = AppLog.For<WindowsProviderDeploymentPlatform>();

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

    /// <summary>通过注册表 API 预检查，不发起注册或提权。</summary>
    public DeveloperModeState ReadDeveloperModeState()
    {
        return DeveloperModeRegistry.ReadState();
    }

    /// <summary>执行协调器指定的注册路径；发布失败禁止注册，不在平台层自行重试。</summary>
    public async Task<ProviderRegistrationResult> PublishAndRegisterAsync(
        string candidatePath,
        ProviderDeploymentPlan plan,
        ProviderRegistrationMode mode,
        CancellationToken token)
    {
        string manifest = AppDataPaths.ManifestPath;
        ProviderPublicationAttempt attempt = mode == ProviderRegistrationMode.Normal
            ? await deployed.PublishAndRegisterAsync(candidatePath, plan, manifest, token)
            : await deployed.PublishAsync(candidatePath, plan, token);
        Log.Information(
            "Provider 发布脚本完成，范围={Scope}，覆盖文件数={CopyCount}，最后确认阶段={Stage}，退出码={ExitCode}，耗时毫秒={ElapsedMilliseconds}，发布片段耗时毫秒={PublicationElapsedMilliseconds}，普通注册片段耗时毫秒={RegistrationElapsedMilliseconds}，注册方式={Mode}",
            plan.Scope,
            plan.CopyPaths.Count,
            attempt.Stage,
            attempt.Result.ExitCode,
            attempt.ElapsedMilliseconds,
            attempt.PublicationElapsedMilliseconds,
            attempt.RegistrationElapsedMilliseconds,
            mode);

        if (attempt.Result.ExitCode != 0)
        {
            return CreateRegistrationResult(attempt.Result,
                allowDeveloperModeConfirmation: mode == ProviderRegistrationMode.Normal && attempt.Stage == DeploymentStage.Registering) with
            {
                Stage = attempt.Stage
            };
        }

        if (mode == ProviderRegistrationMode.TemporaryDeveloperMode)
        {
            return await RegisterWithTemporaryDeveloperModeAsync(token);
        }

        return ProviderRegistrationResult.Success(manifest) with
        {
            Stage = DeploymentStage.Registering
        };
    }

    /// <summary>仅注册已发布文件；提权失败不会再次要求开发者模式确认，避免循环弹窗。</summary>
    public async Task<ProviderRegistrationResult> RegisterWithTemporaryDeveloperModeAsync(CancellationToken token)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        PowerShellResult result = await PowerShellInfrastructure.DeveloperMode.RegisterPackageAsync(AppDataPaths.ManifestPath, token);
        Log.Information("Provider 临时开发者模式注册完成，退出码={ExitCode}，耗时毫秒={ElapsedMilliseconds}",
            result.ExitCode, stopwatch.ElapsedMilliseconds);
        return CreateRegistrationResult(result, allowDeveloperModeConfirmation: false);
    }

    /// <summary>只在普通注册错误中识别开发者模式需求；所有路径保留原始退出码及诊断。</summary>
    private static ProviderRegistrationResult CreateRegistrationResult(PowerShellResult result, bool allowDeveloperModeConfirmation)
    {
        string manifest = AppDataPaths.ManifestPath;
        ProviderRegistrationStatus status = result.ExitCode switch
        {
            0 => ProviderRegistrationStatus.Success,
            PowerShellExitCodes.ElevationCancelled => ProviderRegistrationStatus.ElevationCancelled,
            _ when allowDeveloperModeConfirmation &&
                (result.Error.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) ||
                 result.Output.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase)) => ProviderRegistrationStatus.DeveloperModeConfirmationRequired,
            _ => ProviderRegistrationStatus.Failed
        };
        return new ProviderRegistrationResult(status, result.ExitCode, result.Error, result.Output, manifest)
        {
            Stage = DeploymentStage.Registering
        };
    }
}
