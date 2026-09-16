using FeedCustomizer.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Deployment;

/// <summary>部署的可观察阶段；失败结果保留出错阶段，补偿不覆盖原始原因。</summary>
public enum DeploymentStage { Inspecting, Preparing, Saving, Staging, Unregistering, Publishing, Registering, Verifying, Committing, Completed }

/// <summary>补偿不回滚到旧版程序；停止不完整注册后，由用户重试当前版本。</summary>
public enum DeploymentCompensation { NotNeeded, ProviderDisabled, Failed }

/// <summary>启动只读探测结果，UI 可在准备大量文件之前切换加载遮罩。</summary>
internal sealed record DeploymentInspection(bool Installed, bool ResourcesCurrent);

/// <summary>图片清理允许部分失败；调用方可观察跳过原因，而不是误以为全部删除成功。</summary>
internal sealed record ImageCleanupResult(int Deleted, IReadOnlyList<string> Diagnostics);

/// <summary>
/// 平台副作用边界。查询失败必须抛出异常，不能把“无法查询”解释成“未安装”。
/// 协调器负责调用顺序与串行化，实现负责 AppX 和进程细节。
/// </summary>
internal interface IProviderRegistrationPlatform
{
    Task<bool> IsInstalledAsync(CancellationToken token);
    Task RemoveAsync(CancellationToken token);
    Task StopAsync(CancellationToken token);
    Task<ProviderRegistrationResult> RegisterAsync(bool allowDeveloperMode, CancellationToken token);
}

/// <summary>
/// 部署文件边界。候选构建完成后记录操作日志；不保存或重新注册旧版本副本。
/// 未完成日志跨进程保留，下次启动先停止不完整注册，再允许重试当前版本。
/// </summary>
internal interface IProviderDeploymentStorage
{
    string ManifestPath { get; }
    bool HasTransaction { get; }
    bool TransactionCommitted { get; }
    DeploymentStage PendingStage { get; }
    Task<bool> IsWorkCurrentAsync();
    Task PrepareWorkAsync();
    Task SaveFeedsAsync(List<Feed> feeds);
    Task<bool> IsDeploymentCurrentAsync();
    Task StageAsync();
    Task BeginAsync(bool installed);
    Task RecordStageAsync(DeploymentStage stage);
    Task PublishAsync();
    Task CommitAsync();
    Task FinishAsync();
    Task<ImageCleanupResult> CleanImagesAsync(IReadOnlyCollection<string> draftImages, bool installed);
}
