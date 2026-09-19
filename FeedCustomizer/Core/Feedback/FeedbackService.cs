using FeedCustomizer.Core.Infrastructure.Feedback;
using FeedCustomizer.Core.Infrastructure.Logging;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Feedback;

/// <summary>
/// 反馈用例的唯一编排入口：串行导出日志、构建本地化邮件并选择邮件通道。
/// UI 只消费结构化结果，不直接读写日志或调用 Win32 邮件 API。
/// </summary>
internal sealed class FeedbackService
{
    private static readonly IAppLog Log = AppLog.For<FeedbackService>();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly LogArchiveService _archiveService;
    private readonly FeedbackMessageBuilder _messageBuilder;
    private readonly FeedbackMailDispatcher _mailDispatcher;
    private readonly ResourceLoader _resourceLoader = new();

    internal FeedbackService(
        LogArchiveService archiveService,
        FeedbackMessageBuilder messageBuilder,
        FeedbackMailDispatcher mailDispatcher)
    {
        _archiveService = archiveService;
        _messageBuilder = messageBuilder;
        _mailDispatcher = mailDispatcher;
    }

    /// <summary>创建 Windows 默认实现，邮件通道依次为 Simple MAPI 和 mailto。</summary>
    internal static FeedbackService CreateDefault()
    {
        IFeedbackMailTransport[] transports =
        [
            new SimpleMapiFeedbackMailTransport(),
            new MailtoFeedbackMailTransport()
        ];

        return new FeedbackService(
            new LogArchiveService(),
            new FeedbackMessageBuilder(),
            new FeedbackMailDispatcher(transports));
    }

    /// <summary>仅导出日志，供设置页“提取日志”命令使用。</summary>
    internal async Task<LogArchiveResult> ExportLogsAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await ExportLogsCoreAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// 先生成日志附件与邮件内容，但暂不打开外部客户端。错误弹窗借此在归档失败时保持当前弹窗，
    /// 成功后再释放 ContentDialog 的串行锁并打开邮件客户端。
    /// </summary>
    internal async Task<FeedbackPreparationResult> PrepareAsync(
        FeedbackSource source,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            Log.Information("开始准备反馈，来源={FeedbackSource}", source);
            LogArchiveResult archive = await ExportLogsCoreAsync(cancellationToken);
            if (!archive.Succeeded)
            {
                return FeedbackPreparationResult.Failure(archive.Error);
            }

            FeedbackMailMessage message = _messageBuilder.Build(source, archive);
            return FeedbackPreparationResult.Success(new PreparedFeedback(source, archive, message));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "准备反馈内容失败，来源={FeedbackSource}", source);
            return FeedbackPreparationResult.Failure(LogPrivacy.RedactException(ex));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>将已经准备好的反馈交给邮件客户端；接管后不追踪邮件是否发送。</summary>
    internal async Task<FeedbackOperationResult> LaunchPreparedAsync(
        PreparedFeedback preparedFeedback,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                FeedbackMailTransportResult result = await _mailDispatcher.LaunchAsync(
                    preparedFeedback.Message,
                    ownerWindowHandle,
                    cancellationToken);
                if (!result.WasHandled)
                {
                    return new FeedbackOperationResult(
                        FeedbackOperationStatus.MailClientUnavailable,
                        preparedFeedback.Archive,
                        result.Diagnostic);
                }

                FeedbackOperationStatus status = result.AttachmentRequested
                    ? FeedbackOperationStatus.LaunchedWithAttachment
                    : FeedbackOperationStatus.LaunchedWithoutGuaranteedAttachment;
                return new FeedbackOperationResult(status, preparedFeedback.Archive, result.Diagnostic);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开反馈邮件客户端失败，来源={FeedbackSource}", preparedFeedback.Source);
                return new FeedbackOperationResult(
                    FeedbackOperationStatus.MailClientUnavailable,
                    preparedFeedback.Archive,
                    LogPrivacy.RedactException(ex));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>设置页的一步式反馈操作。</summary>
    internal async Task<FeedbackOperationResult> SendAsync(
        FeedbackSource source,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken = default)
    {
        FeedbackPreparationResult preparation = await PrepareAsync(source, cancellationToken);
        if (!preparation.Succeeded || preparation.PreparedFeedback is null)
        {
            return new FeedbackOperationResult(
                FeedbackOperationStatus.ArchiveFailed,
                LogArchiveResult.Failure(preparation.Error),
                preparation.Error);
        }

        return await LaunchPreparedAsync(
            preparation.PreparedFeedback,
            ownerWindowHandle,
            cancellationToken);
    }

    private Task<LogArchiveResult> ExportLogsCoreAsync(CancellationToken cancellationToken)
    {
        string emptyArchiveInformation = string.Format(
            _resourceLoader.GetString("FeedbackEmptyArchiveInformationFormat"),
            DateTimeOffset.Now,
            AppLog.SessionId);
        return _archiveService.ExportToDownloadsAsync(emptyArchiveInformation, cancellationToken);
    }
}
