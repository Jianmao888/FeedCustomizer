using FeedCustomizer.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Deployment;

/// <summary>
/// Provider 唯一生命周期入口。准备资源、保存配置、发布、注册、卸载和图片清理共用一把锁。
/// 文件实现和 AppX 实现均不持有业务锁，避免递归等待，也便于用替身验证失败顺序。
/// </summary>
internal sealed class ProviderDeploymentCoordinator(
    IProviderDeploymentStorage storage, IProviderRegistrationPlatform platform)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>先恢复上次中断的部署，再探测是否需要展示资源复制遮罩。</summary>
    internal Task<DeploymentInspection> InspectAsync() => SerializedAsync(async () =>
    {
        await RecoverAsync();
        return new DeploymentInspection(await platform.IsInstalledAsync(default), await storage.IsWorkCurrentAsync());
    });

    /// <summary>更新工作副本无需卸载正在运行的包；候选保留用户配置，清单按单文件原子替换。</summary>
    internal Task PrepareAsync() => SerializedAsync(async () =>
    {
        await RecoverAsync();
        await storage.PrepareWorkAsync();
        return true;
    });

    /// <summary>保存和发布属于同一用例，UI 不再自行串联卸载、写清单和注册。</summary>
    internal Task<ProviderRegistrationResult> ApplyAsync(List<Feed>? feeds, bool enable, bool allowDeveloperMode) =>
        SerializedAsync(() => ApplyCoreAsync(feeds, enable, allowDeveloperMode));

    /// <summary>清理也受部署锁保护，避免删除候选清单仍在引用的图片。</summary>
    internal Task<ImageCleanupResult> CleanImagesAsync(IReadOnlyCollection<string> draftImages) => SerializedAsync(async () =>
    {
        // 未完成部署的引用状态不完整，此时暂缓清理，避免删掉重试仍需要的图片。
        if (storage.HasTransaction)
            return new ImageCleanupResult(0, ["存在未完成部署事务，暂缓图片清理。"]);
        return await storage.CleanImagesAsync(draftImages, await platform.IsInstalledAsync(default));
    });

    private async Task<ProviderRegistrationResult> ApplyCoreAsync(List<Feed>? feeds, bool enable, bool allowDeveloperMode)
    {
        DeploymentStage stage = DeploymentStage.Inspecting;
        bool saved = false;
        bool? installed = null;
        try
        {
            await RecoverAsync();
            installed = await platform.IsInstalledAsync(default);
            stage = DeploymentStage.Preparing;
            // 单纯关闭不依赖资源完整性；即使模板/配置损坏，用户仍能关闭已安装 Provider。
            if (enable || feeds is not null) await storage.PrepareWorkAsync();
            if (feeds is not null)
            {
                stage = DeploymentStage.Saving;
                await storage.SaveFeedsAsync(feeds);
                saved = true;
            }

            if (enable && installed.Value && await storage.IsDeploymentCurrentAsync())
                return Success(saved, true);

            // 先构建并验证当前版本候选，再卸载。无需备份旧程序，准备失败时现有注册仍可用。
            stage = DeploymentStage.Staging;
            if (enable) await storage.StageAsync();
            await storage.BeginAsync(installed.Value);
            stage = DeploymentStage.Unregistering;
            await storage.RecordStageAsync(stage);
            if (installed.Value) await platform.RemoveAsync(default);
            await platform.StopAsync(default);

            if (enable)
            {
                stage = DeploymentStage.Publishing;
                await storage.RecordStageAsync(stage);
                await storage.PublishAsync();
                stage = DeploymentStage.Registering;
                await storage.RecordStageAsync(stage);
                ProviderRegistrationResult registration = await platform.RegisterAsync(allowDeveloperMode, default);
                if (!registration.Succeeded)
                    return await CompensateAsync(registration with { Stage = stage, ConfigurationSaved = saved });
                stage = DeploymentStage.Verifying;
                await storage.RecordStageAsync(stage);
                if (!await platform.IsInstalledAsync(default))
                    throw new InvalidOperationException("注册命令返回成功，但未能查询到 Provider 包。");
            }

            // 日志最后标为完成；中断恢复只清理未完成注册，不把新版应用重新绑定到旧版程序。
            stage = DeploymentStage.Committing;
            await storage.RecordStageAsync(stage);
            await storage.CommitAsync();
            await storage.FinishAsync();
            return Success(saved, enable);
        }
        catch (Exception ex)
        {
            var failure = ProviderRegistrationResult.Failed(storage.ManifestPath, null, ex.ToString(), string.Empty)
                with { Stage = stage, ConfigurationSaved = saved, ProviderEnabled = installed };
            return await CompensateAsync(failure);
        }
    }

    private ProviderRegistrationResult Success(bool saved, bool enabled) =>
        ProviderRegistrationResult.Success(storage.ManifestPath) with
        { Stage = DeploymentStage.Completed, ConfigurationSaved = saved, ProviderEnabled = enabled };

    /// <summary>补偿只停止不完整的注册；保留当前工作配置和候选，供下一次幂等重试。</summary>
    private async Task<ProviderRegistrationResult> CompensateAsync(ProviderRegistrationResult failure)
    {
        if (!storage.HasTransaction) return failure;
        try
        {
            // 提交后的清理失败不得倒退一个已经成功注册的部署。
            if (storage.TransactionCommitted)
                return failure with { Compensation = DeploymentCompensation.Failed,
                    CompensationError = "部署已提交，事务清理将在下次启动重试。", ProviderEnabled = null };
            if (storage.PendingStage >= DeploymentStage.Unregistering)
            {
                // 注册命令可能失败前已产生副作用；先查询，再确保清除不完整注册。
                if (await platform.IsInstalledAsync(default)) await platform.RemoveAsync(default);
                await platform.StopAsync(default);
                await storage.FinishAsync();
                return failure with { Compensation = DeploymentCompensation.ProviderDisabled, ProviderEnabled = false };
            }
            await storage.FinishAsync();
            return failure;
        }
        catch (Exception ex)
        {
            // 补偿失败时保留日志，同时保留最初的失败阶段与诊断，不能伪装为关闭成功。
            return failure with { Compensation = DeploymentCompensation.Failed,
                CompensationError = ex.ToString(), ProviderEnabled = null };
        }
    }

    private async Task RecoverAsync()
    {
        if (!storage.HasTransaction) return;
        if (storage.TransactionCommitted) { await storage.FinishAsync(); return; }
        var recovered = await CompensateAsync(ProviderRegistrationResult.Failed(
            storage.ManifestPath, null, "恢复上次中断的部署。", string.Empty));
        if (recovered.Compensation == DeploymentCompensation.Failed)
            throw new InvalidOperationException(recovered.CompensationError);
    }

    private async Task<T> SerializedAsync<T>(Func<Task<T>> operation)
    {
        // 文件哈希与复制在工作线程执行；锁覆盖整个用例，而不仅是某一个 PowerShell 调用。
        if (!await _gate.WaitAsync(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("等待 Provider 部署操作超时。");
        try { return await Task.Run(operation); }
        finally { _gate.Release(); }
    }
}
