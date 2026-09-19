using AppConstants = FeedCustomizer.Core.Constants.Constants;
using FeedCustomizer.Core.Feedback;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Dialogs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.ApplicationModel.Resources;

namespace FeedCustomizer.Core.Tools
{
    /// <summary>
    /// 负责在主窗口上以串行方式展示各类 ContentDialog。
    /// </summary>
    public static class DialogService
    {
        private static readonly IAppLog Log = AppLog.For(nameof(DialogService));
        private static UiThreadRunner? _uiThreadRunner;
        private static Func<XamlRoot?>? _xamlRootProvider;
        private static Func<Task>? _waitForSplashHidden;
        private static SemaphoreSlim? _dialogGate;
        private static TaskCompletionSource<bool>? _firstRunDialogFinished;
        private static bool _firstRunDialogPending;
        private static FeedbackService? _feedbackService;
        private static Func<IntPtr>? _windowHandleProvider;

        internal static void Initialize(
            DispatcherQueue dispatcherQueue,
            Func<XamlRoot?> xamlRootProvider,
            Func<Task> waitForSplashHidden,
            SemaphoreSlim dialogGate,
            FeedbackService feedbackService,
            Func<IntPtr> windowHandleProvider)
        {
            _uiThreadRunner = new UiThreadRunner(dispatcherQueue, waitForSplashHidden);
            _xamlRootProvider = xamlRootProvider;
            _waitForSplashHidden = waitForSplashHidden;
            _dialogGate = dialogGate;
            _feedbackService = feedbackService;
            _windowHandleProvider = windowHandleProvider;
            _firstRunDialogFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void EnsureInitialized()
        {
            if (_uiThreadRunner is null ||
                _xamlRootProvider is null ||
                _waitForSplashHidden is null ||
                _dialogGate is null ||
                _firstRunDialogFinished is null ||
                _feedbackService is null ||
                _windowHandleProvider is null)
            {
                throw new InvalidOperationException("DialogService is not initialized. Call DialogService.Initialize(...) from MainWindow during startup.");
            }
        }

        public static void SetFirstRunDialogPending(bool pending)
        {
            EnsureInitialized();
            _firstRunDialogPending = pending;
            if (!pending)
            {
                _firstRunDialogFinished!.TrySetResult(true);
            }
        }

        public static Task ShowMessageAsync(string title, string content, string closeButtonText)
        {
            EnsureInitialized();
            return _uiThreadRunner!.RunAsync(async () =>
            {
                var dialog = new MessageDialog();
                dialog.Configure(title, content, closeButtonText);
                await ShowWithGateAsync(dialog);
            });
        }

        public static async Task<bool> ShowConfirmAsync(string title, string content, string primaryButtonText, string closeButtonText)
        {
            EnsureInitialized();
            bool confirmed = false;
            await _uiThreadRunner!.RunAsync(async () =>
            {
                var dialog = new ConfirmDialog();
                dialog.Configure(title, content, primaryButtonText, closeButtonText);

                var result = await ShowWithGateAsync(dialog);
                confirmed = result == ContentDialogResult.Primary;
            });

            return confirmed;
        }

        /// <summary>
        /// 在启动阶段安全地显示确认框。它先等待启动视觉状态和首次运行说明结束，
        /// 防止后台初始化请求的对话框遮住 Splash 或与首次运行对话框竞争顺序。
        /// </summary>
        public static async Task<bool> ShowAfterStartupConfirmAsync(
            string title,
            string content,
            string primaryButtonText,
            string closeButtonText)
        {
            EnsureInitialized();
            await _waitForSplashHidden!();
            await _firstRunDialogFinished!.Task;
            return await ShowConfirmAsync(title, content, primaryButtonText, closeButtonText);
        }

        public static async Task ShowStartupFailureAsync(string title, string details)
        {
            EnsureInitialized();
            await _waitForSplashHidden!();
            await _firstRunDialogFinished!.Task;

            await ShowErrorAsync(title, details, FeedbackSource.Startup);
        }

        /// <summary>
        /// 显示带“发送反馈”的统一错误弹窗。日志先在当前弹窗内准备，随后释放弹窗串行锁，
        /// 再打开外部邮件客户端，避免 ContentDialog 与外部窗口相互阻塞。
        /// </summary>
        internal static Task ShowErrorAsync(string title, string details, FeedbackSource source)
        {
            EnsureInitialized();
            return _uiThreadRunner!.RunAsync(async () =>
            {
                var resources = new ResourceLoader();
                var dialog = new StartupFailureDialog();
                dialog.Configure(
                    title,
                    details,
                    resources.GetString("FeedbackSendButtonText"),
                    resources.GetString("FeedbackPreparingMessage"),
                    resources.GetString("FeedbackArchiveFailureInlineMessage"),
                    () => _feedbackService!.PrepareAsync(source));

                ContentDialogResult result = await ShowWithGateAsync(dialog);
                if (result != ContentDialogResult.Primary || dialog.PreparedFeedback is null)
                {
                    return;
                }

                FeedbackOperationResult launchResult = await _feedbackService!.LaunchPreparedAsync(
                    dialog.PreparedFeedback,
                    _windowHandleProvider!());
                if (!launchResult.MailClientLaunched)
                {
                    Log.Warning(
                        "所有反馈邮件通道均未能打开客户端，诊断={Diagnostic}",
                        LogPrivacy.PrepareDiagnostic(launchResult.Diagnostic));
                    string message = string.Format(
                        resources.GetString("FeedbackMailClientFailureMessageFormat"),
                        launchResult.Archive.FullPath,
                        AppConstants.FeedbackEmailAddress);
                    await ShowMessageCoreAsync(
                        resources.GetString("FeedbackMailClientFailureTitle"),
                        message,
                        resources.GetString("DialogOK"));
                }
            });
        }

        public static async Task ShowFirstRunAsync()
        {
            EnsureInitialized();
            await _waitForSplashHidden!();

            if (!_firstRunDialogPending)
            {
                _firstRunDialogFinished!.TrySetResult(true);
                return;
            }

            try
            {
                await _uiThreadRunner!.RunAsync(async () =>
                {
                    if (!_firstRunDialogPending)
                    {
                        return;
                    }

                    var dialog = new FirstRunDialog();
                    await ShowWithGateAsync(dialog);
                });
            }
            finally
            {
                _firstRunDialogPending = false;
                _firstRunDialogFinished!.TrySetResult(true);
            }
        }

        public static Task ShowWebIconFetchErrorAsync(string details)
        {
            var resources = new ResourceLoader();
            return ShowErrorAsync(
                resources.GetString("WebIconFetchErrorDialog/Title"),
                details,
                FeedbackSource.WebIcon);
        }

        private static async Task ShowMessageCoreAsync(string title, string content, string closeButtonText)
        {
            var dialog = new MessageDialog();
            dialog.Configure(title, content, closeButtonText);
            await ShowWithGateAsync(dialog);
        }

        private static async Task<ContentDialogResult> ShowWithGateAsync(ContentDialog dialog)
        {
            EnsureInitialized();
            var xamlRoot = _xamlRootProvider!();
            if (xamlRoot is null)
            {
                return ContentDialogResult.None;
            }

            dialog.XamlRoot = xamlRoot;
            await _dialogGate!.WaitAsync();
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                _dialogGate.Release();
            }
        }
    }
}
