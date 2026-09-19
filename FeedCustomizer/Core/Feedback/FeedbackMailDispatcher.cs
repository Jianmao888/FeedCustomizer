using FeedCustomizer.Core.Infrastructure.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Feedback;

/// <summary>按固定优先级尝试邮件通道；任一客户端接管后立即停止，不跟踪用户后续行为。</summary>
internal sealed class FeedbackMailDispatcher
{
    private static readonly IAppLog Log = AppLog.For<FeedbackMailDispatcher>();
    private readonly IReadOnlyList<IFeedbackMailTransport> _transports;

    internal FeedbackMailDispatcher(IReadOnlyList<IFeedbackMailTransport> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        if (transports.Count == 0)
        {
            throw new ArgumentException("至少需要一个反馈邮件通道。", nameof(transports));
        }

        _transports = transports;
    }

    internal async Task<FeedbackMailTransportResult> LaunchAsync(
        FeedbackMailMessage message,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        foreach (IFeedbackMailTransport transport in _transports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FeedbackMailTransportResult result;
            try
            {
                result = await transport.TryLaunchAsync(message, ownerWindowHandle, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个外部客户端适配失败允许降级，但必须留下足够诊断，不能伪装成“未安装”。
                string diagnostic = LogPrivacy.RedactException(ex);
                diagnostics.Add($"{transport.Name}: {diagnostic}");
                Log.Warning(ex, "反馈邮件通道发生未处理异常，通道={TransportName}", transport.Name);
                continue;
            }

            if (result.WasHandled)
            {
                Log.Information(
                    "反馈邮件客户端已接管，通道={TransportName}，请求附件={AttachmentRequested}",
                    result.TransportName,
                    result.AttachmentRequested);
                return result;
            }

            diagnostics.Add($"{result.TransportName}: {result.Diagnostic}");
            Log.Warning(
                "反馈邮件通道未能启动客户端，通道={TransportName}，状态={Status}，诊断={Diagnostic}",
                result.TransportName,
                result.Status,
                LogPrivacy.PrepareDiagnostic(result.Diagnostic));
        }

        return new FeedbackMailTransportResult(
            FeedbackMailTransportStatus.Failed,
            "All",
            false,
            string.Join(Environment.NewLine, diagnostics));
    }
}
