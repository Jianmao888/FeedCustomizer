using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Feedback;

/// <summary>标识反馈入口，邮件正文只记录稳定类别，不复制可能包含隐私的错误详情。</summary>
internal enum FeedbackSource
{
    Settings,
    ProviderRegistration,
    Startup,
    RegionPolicy,
    WidgetData,
    WebIcon,
    Donation
}

/// <summary>日志归档结果。成功时提供 Windows 返回的实际完整路径，失败时保留安全诊断。</summary>
internal sealed record LogArchiveResult(
    bool Succeeded,
    string FileName,
    string FullPath,
    int LogFileCount,
    string Error)
{
    internal static LogArchiveResult Success(string fileName, string fullPath, int logFileCount) =>
        new(true, fileName, fullPath, logFileCount, string.Empty);

    internal static LogArchiveResult Failure(string error) =>
        new(false, string.Empty, string.Empty, 0, error);
}

/// <summary>传递给邮件基础设施的不可变消息，不包含邮件客户端或 UI 类型。</summary>
internal sealed record FeedbackMailMessage(
    string Recipient,
    string Subject,
    string Body,
    string AttachmentPath,
    string AttachmentName);

/// <summary>单个邮件通道的结果。客户端接管后不再追踪用户是否编辑、关闭或发送邮件。</summary>
internal enum FeedbackMailTransportStatus
{
    Launched,
    ClientHandled,
    Unsupported,
    Failed
}

/// <summary>单个邮件通道返回的稳定结果，诊断仅用于日志与最终失败提示。</summary>
internal sealed record FeedbackMailTransportResult(
    FeedbackMailTransportStatus Status,
    string TransportName,
    bool AttachmentRequested,
    string Diagnostic)
{
    internal bool WasHandled =>
        Status is FeedbackMailTransportStatus.Launched or FeedbackMailTransportStatus.ClientHandled;
}

/// <summary>
/// 一种打开邮件编辑器的基础设施通道。当前由 Simple MAPI 与 mailto 实现，
/// 后续可加入针对特定客户端的实现而不改变反馈业务流程。
/// </summary>
internal interface IFeedbackMailTransport
{
    string Name { get; }

    Task<FeedbackMailTransportResult> TryLaunchAsync(
        FeedbackMailMessage message,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken = default);
}

/// <summary>已生成日志与邮件内容、等待交给邮件客户端的反馈。</summary>
internal sealed record PreparedFeedback(
    FeedbackSource Source,
    LogArchiveResult Archive,
    FeedbackMailMessage Message);

/// <summary>反馈准备结果。日志归档失败时不会继续打开邮件客户端。</summary>
internal sealed record FeedbackPreparationResult(
    bool Succeeded,
    PreparedFeedback? PreparedFeedback,
    string Error)
{
    internal static FeedbackPreparationResult Success(PreparedFeedback preparedFeedback) =>
        new(true, preparedFeedback, string.Empty);

    internal static FeedbackPreparationResult Failure(string error) =>
        new(false, null, error);
}

/// <summary>完整反馈操作状态；名称只描述客户端是否接管，不宣称邮件已经发送。</summary>
internal enum FeedbackOperationStatus
{
    LaunchedWithAttachment,
    LaunchedWithoutGuaranteedAttachment,
    ArchiveFailed,
    MailClientUnavailable
}

/// <summary>设置页或错误 UI 用于决定是否提示用户的反馈操作结果。</summary>
internal sealed record FeedbackOperationResult(
    FeedbackOperationStatus Status,
    LogArchiveResult Archive,
    string Diagnostic)
{
    internal bool MailClientLaunched =>
        Status is FeedbackOperationStatus.LaunchedWithAttachment or
            FeedbackOperationStatus.LaunchedWithoutGuaranteedAttachment;
}
