using FeedCustomizer.Core.Feedback;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;

namespace FeedCustomizer.Core.Infrastructure.Feedback;

/// <summary>
/// 使用系统 mailto 关联打开默认邮件客户端。该标准不支持可靠附件，
/// 因此只作为 MAPI 失败后的最后降级，正文会提示用户从完整路径手动附加 ZIP。
/// </summary>
internal sealed class MailtoFeedbackMailTransport : IFeedbackMailTransport
{
    public string Name => "mailto";

    public async Task<FeedbackMailTransportResult> TryLaunchAsync(
        FeedbackMailMessage message,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken = default)
    {
        _ = ownerWindowHandle;
        cancellationToken.ThrowIfCancellationRequested();

        string uriText =
            $"mailto:{Uri.EscapeDataString(message.Recipient)}" +
            $"?subject={Uri.EscapeDataString(message.Subject)}" +
            $"&body={Uri.EscapeDataString(message.Body)}";
        bool launched = await Launcher.LaunchUriAsync(new Uri(uriText));
        return launched
            ? new FeedbackMailTransportResult(
                FeedbackMailTransportStatus.Launched,
                Name,
                false,
                "系统已接受 mailto 打开请求，附件需要用户确认。")
            : new FeedbackMailTransportResult(
                FeedbackMailTransportStatus.Unsupported,
                Name,
                false,
                "系统没有接受 mailto 打开请求。");
    }
}
