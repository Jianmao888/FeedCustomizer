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

/// <summary>开发者模式开关的预检查结果；无法读取时仍允许尝试普通注册。</summary>
internal enum DeveloperModeState
{
    Enabled,
    Disabled,
    Unknown
}

/// <summary>协调器选定的注册方式；平台不再自行决定是否提权重试。</summary>
internal enum ProviderRegistrationMode
{
    Normal,
    TemporaryDeveloperMode
}

/// <summary>图片清理允许部分失败；调用方可观察跳过原因，而不是误以为全部删除成功。</summary>
internal sealed record ImageCleanupResult(int Deleted, IReadOnlyList<string> Diagnostics);

/// <summary>发布范围由完整预期版本和注册目录的实际状态决定，关闭操作不需要生成计划。</summary>
internal enum ProviderDeploymentScope { Current, Configuration, Full }

/// <summary>同一份计划贯穿候选构建和发布，避免在卸载后重新推断待复制文件。</summary>
internal sealed record ProviderDeploymentPlan(ProviderDeploymentScope Scope, IReadOnlyList<string> CopyPaths);

/// <summary>包外进程只报告文件差异，是否允许仅发布配置由应用内的纯规则决定。</summary>
internal sealed record DeployedFileInspection(
    bool MetadataValid,
    bool TemplateMatches,
    bool UnexpectedRuntime,
    string[] MetadataChanged,
    string[] ContentChanged);

/// <summary>
/// Provider 平台副作用边界。查询失败必须抛出异常，不能把“无法查询”解释成“未安装”。
/// 协调器负责调用顺序与串行化，实现负责包外发布、AppX 和进程细节。
/// </summary>
internal interface IProviderDeploymentPlatform
{
    Task<bool> IsInstalledAsync(CancellationToken token);
    Task RemoveAsync(CancellationToken token);
    Task StopAsync(CancellationToken token);
    /// <summary>只读预检查，不修改系统设置；异常通过 Unknown 表达，不解释为关闭。</summary>
    DeveloperModeState ReadDeveloperModeState();
    /// <summary>先发布候选再按指定方式注册；普通方式共享进程，提权方式保留独立权限边界。</summary>
    Task<ProviderRegistrationResult> PublishAndRegisterAsync(
        string candidatePath,
        ProviderDeploymentPlan plan,
        ProviderRegistrationMode mode,
        CancellationToken token);
    /// <summary>仅提权注册已发布的版本；用于普通注册失败后的单次兜底，不再次发布文件。</summary>
    Task<ProviderRegistrationResult> RegisterWithTemporaryDeveloperModeAsync(CancellationToken token);
}

/// <summary>
/// 部署文件边界。候选构建完成后记录操作日志；不保存或重新注册旧版本副本。
/// 未完成日志跨进程保留，下次启动先停止不完整注册，再允许重试当前版本。
/// </summary>
internal interface IProviderDeploymentStorage
{
    string ManifestPath { get; }
    /// <summary>构建完成的候选目录；只供平台发布使用，不是用户配置来源。</summary>
    string CandidatePath { get; }
    /// <summary>启动校验或修复已成功，运行时只读取此状态，不扫描工作目录。</summary>
    bool WorkReady { get; }
    bool HasTransaction { get; }
    bool TransactionCommitted { get; }
    DeploymentStage PendingStage { get; }
    /// <summary>启动阶段检查一次工作版本，并保留已有版本清单；重复调用复用结果。</summary>
    Task<bool> InspectWorkAtStartupAsync();
    /// <summary>启动阶段按需修复工作版本，失败向上传递，不在运行时自动重建。</summary>
    Task PrepareWorkAtStartupAsync();
    Task SaveFeedsAsync(List<Feed> feeds);
    Task<ProviderDeploymentPlan> PlanAsync(bool installed);
    Task StageAsync(ProviderDeploymentPlan plan);
    Task BeginAsync(bool installed);
    Task RecordStageAsync(DeploymentStage stage);
    Task CommitAsync();
    Task FinishAsync();
    Task<ImageCleanupResult> CleanImagesAsync(IReadOnlyCollection<string> draftImages, bool installed);
}
