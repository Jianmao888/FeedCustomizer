using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Documents;

/// <summary>
/// 应用随包分发、需要在浏览器或关联程序中打开的文档类型。
/// 新增隐私声明或开源许可时，应在目录描述中增加对应入口，而不是另建专用打开工具。
/// </summary>
public enum ApplicationDocumentKind
{
    Help,
    Privacy,
    OpenSourceLicenses,
}

/// <summary>
/// 已由文档服务验证的打开请求。视图只负责把文件交给 Windows 关联程序，
/// 不得重新拼接或解释其中的路径。
/// </summary>
public sealed class ApplicationDocumentOpenRequestedEventArgs(
    ApplicationDocumentKind kind,
    string filePath) : EventArgs
{
    public ApplicationDocumentKind Kind { get; } = kind;

    public string FilePath { get; } = filePath;
}

/// <summary>文档准备结果的稳定状态，UI 只根据状态决定是否打开或提示。</summary>
public enum ApplicationDocumentStatus
{
    Ready,
    Unsupported,
    SynchronizationFailed,
    MissingAfterSynchronization,
}

/// <summary>
/// 文档准备结果。诊断只供日志和错误展示使用，文件路径仅在成功时有效。
/// </summary>
public sealed record ApplicationDocumentResult(
    ApplicationDocumentStatus Status,
    string FilePath,
    string Diagnostic)
{
    public bool Succeeded => Status == ApplicationDocumentStatus.Ready;

    internal static ApplicationDocumentResult Ready(string filePath) =>
        new(ApplicationDocumentStatus.Ready, filePath, string.Empty);

    internal static ApplicationDocumentResult Failed(
        ApplicationDocumentStatus status,
        string diagnostic) =>
        new(status, string.Empty, diagnostic);
}

/// <summary>
/// 文档目录的持久化状态。清理尝试版本单独记录，保证一次失败不会让普通启动反复创建 PowerShell 进程。
/// </summary>
internal sealed record ApplicationDocumentState(
    string PackageVersion,
    int CatalogSchema,
    string LegacyCleanupAttemptedVersion,
    bool LegacyCleanupCompleted);

/// <summary>
/// 文档文件系统边界。应用服务负责版本决策，本接口只执行确定的读取、同步和遗留清理动作。
/// </summary>
internal interface IApplicationDocumentStorage
{
    string CurrentPackageVersion { get; }

    ApplicationDocumentState? ReadState();

    void WriteState(ApplicationDocumentState state);

    bool LegacyPrivateHelpExists();

    Task ReplaceCatalogAsync(CancellationToken cancellationToken);

    string? ResolveDocumentPath(ApplicationDocumentKind kind, string languageTag);

    Task<LegacyDocumentCleanupResult> RemoveLegacyHelpAsync(CancellationToken cancellationToken);
}

/// <summary>旧版三副本文档的清理结果；私有目录和真实 LocalAppData 分别报告。</summary>
internal sealed record LegacyDocumentCleanupResult(
    bool PrivateCopyRemoved,
    bool RegisteredCopyRemoved,
    IReadOnlyList<string> Diagnostics);
