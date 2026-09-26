using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Deployment;

/// <summary>
/// Provider 唯一生命周期入口。准备资源、保存配置、发布、注册、卸载和图片清理共用一把锁。
/// 文件实现和 AppX 实现均不持有业务锁，避免递归等待，也便于用替身验证失败顺序。
/// </summary>
internal sealed class ProviderDeploymentCoordinator(
    IProviderDeploymentStorage storage,
    IProviderDeploymentPlatform platform)
{
    private static readonly IAppLog Log = AppLog.For<ProviderDeploymentCoordinator>();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>启动时先恢复中断部署，再检查工作版本；同一进程重复初始化不重新扫描工作目录。</summary>
    internal Task<DeploymentInspection> InspectAsync()
    {
        return SerializedAsync(async () =>
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Log.Information("开始检查 Provider 部署状态");
            await RecoverAsync();
            var inspection = new DeploymentInspection(
                await platform.IsInstalledAsync(default),
                await storage.InspectWorkAtStartupAsync());
            Log.Information(
                "Provider 部署状态检查完成，已安装={Installed}，工作副本最新={WorkCurrent}，耗时毫秒={ElapsedMilliseconds}",
                inspection.Installed,
                inspection.ResourcesCurrent,
                stopwatch.ElapsedMilliseconds);
            return inspection;
        });
    }

    /// <summary>仅在启动阶段按需修复工作版本，不影响当前注册；保留用户配置并原子替换清单。</summary>
    internal Task PrepareAsync()
    {
        return SerializedAsync(async () =>
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Log.Information("开始准备 Provider 工作副本");
            await RecoverAsync();
            await storage.PrepareWorkAtStartupAsync();
            Log.Information("Provider 工作副本准备完成，耗时毫秒={ElapsedMilliseconds}", stopwatch.ElapsedMilliseconds);
            return true;
        });
    }

    /// <summary>保存和发布属于同一用例；确认重试只影响本次注册路径，仍重新进入完整部署事务。</summary>
    /// <param name="feeds">待保存配置；为空时使用当前工作配置。</param>
    /// <param name="enable">是否启用 Provider；关闭时不检查开发者模式。</param>
    /// <param name="allowDeveloperMode">应用设置是否允许自动临时开启开发者模式。</param>
    /// <param name="developerModeConfirmed">本次重试已确认需要开发者模式，跳过预检查和普通注册。</param>
    internal Task<ProviderRegistrationResult> ApplyAsync(
        List<Feed>? feeds,
        bool enable,
        bool allowDeveloperMode,
        bool developerModeConfirmed = false)
    {
        // 确认标记只影响本次已获授权的注册，不能绕过自动设置获得提权能力。
        if (developerModeConfirmed && (!enable || !allowDeveloperMode))
        {
            throw new ArgumentException("开发者模式确认重试必须启用源并允许自动开发者模式。");
        }

        return SerializedAsync(() => ApplyCoreAsync(feeds, enable, allowDeveloperMode, developerModeConfirmed));
    }

    /// <summary>清理也受部署锁保护，避免删除候选清单仍在引用的图片。</summary>
    internal Task<ImageCleanupResult> CleanImagesAsync(IReadOnlyCollection<string> draftImages)
    {
        return SerializedAsync(async () =>
        {
            if (storage.HasTransaction)
            {
                Log.Warning("检测到未完成部署事务，暂缓图片清理");
                return new ImageCleanupResult(0, ["存在未完成部署事务，暂缓图片清理。"]);
            }

            ImageCleanupResult result = await storage.CleanImagesAsync(
                draftImages,
                await platform.IsInstalledAsync(default));
            Log.Information(
                "图片清理完成，删除数量={DeletedCount}，诊断数量={DiagnosticCount}",
                result.Deleted,
                result.Diagnostics.Count);
            return result;
        });
    }

    private async Task<ProviderRegistrationResult> ApplyCoreAsync(
        List<Feed>? feeds,
        bool enable,
        bool allowDeveloperMode,
        bool developerModeConfirmed)
    {
        string operationId = Guid.NewGuid().ToString("N")[..8];
        Stopwatch stopwatch = Stopwatch.StartNew();
        DeploymentStage stage = DeploymentStage.Inspecting;
        bool saved = false;
        bool? installed = null;

        Log.Information(
            "开始应用 Provider 配置，操作={OperationId}，目标启用={Enable}，配置数量={FeedCount}，允许自动开发者模式={AllowDeveloperMode}",
            operationId,
            enable,
            feeds?.Count,
            allowDeveloperMode);

        try
        {
            await RecoverAsync();
            installed = await platform.IsInstalledAsync(default);
            Log.Debug("Provider 当前注册状态，操作={OperationId}，已安装={Installed}", operationId, installed);

            stage = DeploymentStage.Preparing;
            Log.Debug("Provider 部署进入阶段，操作={OperationId}，阶段={Stage}", operationId, stage);
            // 运行时直接使用启动已准备的工作版本，不重新校验或修复；单纯关闭不依赖启动准备成功。
            if ((enable || feeds is not null) && !storage.WorkReady)
            {
                throw new InvalidOperationException("Provider 工作版本尚未完成启动准备。");
            }

            if (feeds is not null)
            {
                stage = DeploymentStage.Saving;
                Log.Debug("Provider 部署进入阶段，操作={OperationId}，阶段={Stage}", operationId, stage);
                await storage.SaveFeedsAsync(feeds);
                saved = true;
            }

            ProviderDeploymentPlan? plan = null;
            if (enable)
            {
                // 先规划并验证候选，再卸载现有注册；仅配置变化时不构建程序文件候选。
                stage = DeploymentStage.Staging;
                Log.Debug("Provider 部署进入阶段，操作={OperationId}，阶段={Stage}", operationId, stage);
                plan = await storage.PlanAsync(installed.Value);
                if (installed.Value && plan.Scope == ProviderDeploymentScope.Current)
                {
                    Log.Information(
                        "Provider 已是当前版本，无需重新注册，操作={OperationId}，耗时毫秒={ElapsedMilliseconds}",
                        operationId,
                        stopwatch.ElapsedMilliseconds);
                    return Success(saved, true);
                }

                await storage.StageAsync(plan);
            }

            await storage.BeginAsync(installed.Value);
            stage = DeploymentStage.Unregistering;
            Log.Debug("Provider 部署进入阶段，操作={OperationId}，阶段={Stage}", operationId, stage);
            await storage.RecordStageAsync(stage);
            if (installed.Value)
            {
                await platform.RemoveAsync(default);
            }

            await platform.StopAsync(default);

            if (enable)
            {
                // 关闭与无需更新不进入此处。先完成卸载，再检查注册权限；不跨 UI 弹窗持有部署事务。
                DeveloperModeState developerMode = developerModeConfirmed
                    ? DeveloperModeState.Unknown
                    : platform.ReadDeveloperModeState();
                Log.Information("Provider 注册前检查，操作={OperationId}，开发者模式={DeveloperMode}，已确认重试={Confirmed}",
                    operationId, developerMode, developerModeConfirmed);
                if (developerMode == DeveloperModeState.Disabled && !allowDeveloperMode)
                {
                    stage = DeploymentStage.Registering;
                    await storage.RecordStageAsync(stage);
                    return await CompensateAsync(new ProviderRegistrationResult(
                        ProviderRegistrationStatus.DeveloperModeConfirmationRequired,
                        null,
                        "注册前检查发现开发者模式未开启。",
                        string.Empty,
                        storage.ManifestPath)
                    {
                        Stage = stage,
                        ConfigurationSaved = saved
                    }, operationId);
                }

                ProviderRegistrationMode mode = developerModeConfirmed || developerMode == DeveloperModeState.Disabled
                    ? ProviderRegistrationMode.TemporaryDeveloperMode
                    : ProviderRegistrationMode.Normal;
                stage = DeploymentStage.Publishing;
                Log.Debug("Provider 部署进入阶段，操作={OperationId}，阶段={Stage}", operationId, stage);
                await storage.RecordStageAsync(stage);
                // 普通路径保持合并执行；已知需要开发者模式时跳过预期失败的普通注册。
                ProviderRegistrationResult registration = await platform.PublishAndRegisterAsync(
                    storage.CandidatePath,
                    plan!,
                    mode,
                    default);
                if (mode == ProviderRegistrationMode.Normal &&
                    registration.Stage == DeploymentStage.Registering &&
                    registration.Status == ProviderRegistrationStatus.DeveloperModeConfirmationRequired &&
                    allowDeveloperMode)
                {
                    // 读取失败或设置在检查后发生变化时仅兜底一次，重用已发布文件，不形成重试循环。
                    stage = DeploymentStage.Registering;
                    await storage.RecordStageAsync(stage);
                    Log.Information("普通注册需要开发者模式，执行单次提权兜底，操作={OperationId}", operationId);
                    registration = await platform.RegisterWithTemporaryDeveloperModeAsync(default);
                }

                stage = registration.Stage;
                Log.Debug("Provider 部署进入阶段，操作={OperationId}，阶段={Stage}", operationId, stage);
                await storage.RecordStageAsync(stage);
                if (!registration.Succeeded)
                {
                    Log.Warning(
                        "Provider 注册命令失败，操作={OperationId}，阶段={Stage}，状态={Status}，退出码={ExitCode}",
                        operationId,
                        stage,
                        registration.Status,
                        registration.ExitCode);
                    return await CompensateAsync(
                        registration with
                        {
                            Stage = stage,
                            ConfigurationSaved = saved
                        },
                        operationId);
                }

                stage = DeploymentStage.Verifying;
                Log.Debug("Provider 部署进入阶段，操作={OperationId}，阶段={Stage}", operationId, stage);
                await storage.RecordStageAsync(stage);
                if (!await platform.IsInstalledAsync(default))
                {
                    throw new InvalidOperationException("注册命令返回成功，但未能查询到 Provider 包。");
                }
            }

            // 事务最后标为完成；中断恢复只清理未完成注册，不把新版应用重新绑定到旧版程序。
            stage = DeploymentStage.Committing;
            Log.Debug("Provider 部署进入阶段，操作={OperationId}，阶段={Stage}", operationId, stage);
            await storage.RecordStageAsync(stage);
            await storage.CommitAsync();
            await storage.FinishAsync();

            ProviderRegistrationResult result = Success(saved, enable);
            Log.Information(
                "Provider 配置应用完成，操作={OperationId}，最终启用={Enabled}，配置已保存={ConfigurationSaved}，耗时毫秒={ElapsedMilliseconds}",
                operationId,
                enable,
                saved,
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Provider 配置应用失败，操作={OperationId}，阶段={Stage}，耗时毫秒={ElapsedMilliseconds}",
                operationId,
                stage,
                stopwatch.ElapsedMilliseconds);
            var failure = ProviderRegistrationResult.Failed(
                storage.ManifestPath,
                null,
                ex.ToString(),
                string.Empty) with
            {
                Stage = stage,
                ConfigurationSaved = saved,
                ProviderEnabled = installed
            };
            return await CompensateAsync(failure, operationId);
        }
    }

    private ProviderRegistrationResult Success(bool saved, bool enabled)
    {
        return ProviderRegistrationResult.Success(storage.ManifestPath) with
        {
            Stage = DeploymentStage.Completed,
            ConfigurationSaved = saved,
            ProviderEnabled = enabled
        };
    }

    /// <summary>补偿只停止不完整的注册；保留当前工作配置和候选，供下一次幂等重试。</summary>
    private async Task<ProviderRegistrationResult> CompensateAsync(
        ProviderRegistrationResult failure,
        string operationId)
    {
        if (!storage.HasTransaction)
        {
            Log.Debug("失败发生在部署事务创建前，无需补偿，操作={OperationId}", operationId);
            return failure;
        }

        try
        {
            // 提交后的清理失败不得倒退一个已经成功注册的部署。
            if (storage.TransactionCommitted)
            {
                Log.Warning("部署已提交但事务清理未完成，将在下次启动重试，操作={OperationId}", operationId);
                return failure with
                {
                    Compensation = DeploymentCompensation.Failed,
                    CompensationError = "部署已提交，事务清理将在下次启动重试。",
                    ProviderEnabled = null
                };
            }

            if (storage.PendingStage >= DeploymentStage.Unregistering)
            {
                // 注册命令可能失败前已产生副作用；先查询，再确保清除不完整注册。
                if (await platform.IsInstalledAsync(default))
                {
                    await platform.RemoveAsync(default);
                }

                await platform.StopAsync(default);
                await storage.FinishAsync();
                Log.Warning("Provider 部署补偿完成，已禁用不完整注册，操作={OperationId}", operationId);
                return failure with
                {
                    Compensation = DeploymentCompensation.ProviderDisabled,
                    ProviderEnabled = false
                };
            }

            await storage.FinishAsync();
            Log.Information("Provider 部署事务已在产生注册副作用前清理，操作={OperationId}", operationId);
            return failure;
        }
        catch (Exception ex)
        {
            // 补偿失败时保留事务，同时保留最初的失败阶段与诊断，不能伪装为关闭成功。
            Log.Error(ex, "Provider 部署补偿失败，操作={OperationId}，原失败阶段={Stage}", operationId, failure.Stage);
            return failure with
            {
                Compensation = DeploymentCompensation.Failed,
                CompensationError = ex.ToString(),
                ProviderEnabled = null
            };
        }
    }

    private async Task RecoverAsync()
    {
        if (!storage.HasTransaction)
        {
            return;
        }

        string operationId = $"恢复-{Guid.NewGuid():N}"[..11];
        Log.Warning(
            "检测到上次未完成的 Provider 部署事务，操作={OperationId}，待处理阶段={Stage}，已提交={Committed}",
            operationId,
            storage.PendingStage,
            storage.TransactionCommitted);

        if (storage.TransactionCommitted)
        {
            await storage.FinishAsync();
            Log.Information("已清理上次完成提交后遗留的事务记录，操作={OperationId}", operationId);
            return;
        }

        ProviderRegistrationResult recovered = await CompensateAsync(
            ProviderRegistrationResult.Failed(
                storage.ManifestPath,
                null,
                "恢复上次中断的部署。",
                string.Empty),
            operationId);
        if (recovered.Compensation == DeploymentCompensation.Failed)
        {
            throw new InvalidOperationException(recovered.CompensationError);
        }
    }

    private async Task<T> SerializedAsync<T>(Func<Task<T>> operation)
    {
        // 文件哈希与复制在工作线程执行；锁覆盖整个用例，而不仅是某一个 PowerShell 调用。
        if (!await _gate.WaitAsync(TimeSpan.FromMinutes(5)))
        {
            Log.Error("等待 Provider 部署串行锁超时");
            throw new TimeoutException("等待 Provider 部署操作超时。");
        }

        try
        {
            return await Task.Run(operation);
        }
        finally
        {
            _gate.Release();
        }
    }
}
