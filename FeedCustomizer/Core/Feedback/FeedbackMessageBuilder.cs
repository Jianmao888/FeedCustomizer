using AppConstants = FeedCustomizer.Core.Constants.Constants;
using FeedCustomizer.Core.Infrastructure.Logging;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using Windows.ApplicationModel;

namespace FeedCustomizer.Core.Feedback;

/// <summary>使用当前应用显示语言生成邮件主题和正文，基础设施通道只负责打开客户端。</summary>
internal sealed class FeedbackMessageBuilder
{
    private static readonly IAppLog Log = AppLog.For<FeedbackMessageBuilder>();
    private readonly ResourceLoader _resourceLoader = new();

    internal FeedbackMailMessage Build(FeedbackSource source, LogArchiveResult archive)
    {
        string sourceText = _resourceLoader.GetString($"FeedbackSource_{source}");
        string body = string.Format(
            _resourceLoader.GetString("FeedbackMailBodyFormat"),
            sourceText,
            GetAppVersion(),
            archive.FileName,
            archive.FullPath,
            AppConstants.FeedbackIdentifier);

        return new FeedbackMailMessage(
            AppConstants.FeedbackEmailAddress,
            _resourceLoader.GetString("FeedbackMailSubject"),
            body,
            archive.FullPath,
            archive.FileName);
    }

    private static string GetAppVersion()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (Exception ex)
        {
            // 邮件正文缺少版本不应阻止用户提交反馈；系统信息日志仍会记录可取得的环境字段。
            Log.Warning(ex, "生成反馈邮件时读取应用版本失败");
            return "unknown";
        }
    }
}
