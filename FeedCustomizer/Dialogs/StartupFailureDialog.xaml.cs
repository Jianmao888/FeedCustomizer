using FeedCustomizer.Core.Feedback;
using FeedCustomizer.Core.Infrastructure.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace FeedCustomizer.Dialogs
{
    public sealed partial class StartupFailureDialog : ContentDialog
    {
        private static readonly IAppLog Log = AppLog.For<StartupFailureDialog>();
        private Func<Task<FeedbackPreparationResult>>? _prepareFeedback;
        private string _preparingText = string.Empty;
        private string _preparationFailedText = string.Empty;

        /// <summary>主按钮完成日志归档后保存的反馈，由 DialogService 在释放弹窗锁后启动邮件客户端。</summary>
        internal PreparedFeedback? PreparedFeedback { get; private set; }

        public StartupFailureDialog()
        {
            InitializeComponent();
            PrimaryButtonClick += OnSendFeedbackButtonClick;
            SecondaryButtonClick += OnCopyButtonClick;
        }

        /// <summary>配置可反馈错误。反馈准备失败时保持当前弹窗，避免再叠加一个错误框。</summary>
        internal void Configure(
            string title,
            string details,
            string sendFeedbackButtonText,
            string preparingText,
            string preparationFailedText,
            Func<Task<FeedbackPreparationResult>> prepareFeedback)
        {
            Title = title;
            ErrorTextBox.Text = details;
            PrimaryButtonText = sendFeedbackButtonText;
            _preparingText = preparingText;
            _preparationFailedText = preparationFailedText;
            _prepareFeedback = prepareFeedback;
        }

        private async void OnSendFeedbackButtonClick(
            ContentDialog sender,
            ContentDialogButtonClickEventArgs args)
        {
            _ = sender;
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            IsPrimaryButtonEnabled = false;
            IsSecondaryButtonEnabled = false;
            FeedbackStatusPanel.Visibility = Visibility.Visible;
            FeedbackProgressRing.IsActive = true;
            FeedbackStatusText.Text = _preparingText;

            try
            {
                if (_prepareFeedback is null)
                {
                    throw new InvalidOperationException("反馈准备回调尚未配置。");
                }

                FeedbackPreparationResult result = await _prepareFeedback();
                if (!result.Succeeded || result.PreparedFeedback is null)
                {
                    // 保持当前错误弹窗，不再追加第二个“日志导出失败”弹窗。
                    args.Cancel = true;
                    FeedbackStatusText.Text = _preparationFailedText;
                    return;
                }

                PreparedFeedback = result.PreparedFeedback;
            }
            catch (Exception ex)
            {
                // UI 只显示本地化的稳定提示；详细异常由反馈服务写入调试/文件日志。
                Log.Error(ex, "错误弹窗准备反馈失败");
                args.Cancel = true;
                FeedbackStatusText.Text = _preparationFailedText;
            }
            finally
            {
                FeedbackProgressRing.IsActive = false;
                IsPrimaryButtonEnabled = true;
                IsSecondaryButtonEnabled = true;
                deferral.Complete();
            }
        }

        private void OnCopyButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var package = new DataPackage();
            package.SetText(ErrorTextBox.Text);
            Clipboard.SetContent(package);
            args.Cancel = true;
        }
    }
}
