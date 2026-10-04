using AppConstants = FeedCustomizer.Core.Constants.Constants;
using FeedCustomizer.Core.Feedback;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Dialogs;
using FeedCustomizer.Presentation.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.ApplicationModel.Resources;

namespace FeedCustomizer.Presentation.Dialogs
{
    /// <summary>
    /// 主窗口的统一对话框呈现入口。复用窗口初始化时提供的调度器、共享显示锁和启动信号，
    /// 使不同页面请求遵守既有显示顺序。
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

        /// <summary>
        /// 在主窗口导航页面前绑定 UI 环境、共享显示锁与反馈服务，
        /// 后续页面请求继续使用这里保存的窗口状态，不各自创建独立显示队列。
        /// </summary>
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

        /// <summary>标记首次运行说明的待显示状态；无需说明时立即解除后续启动弹窗的等待。</summary>
        public static void SetFirstRunDialogPending(bool pending)
        {
            EnsureInitialized();
            _firstRunDialogPending = pending;
            if (!pending)
            {
                _firstRunDialogFinished!.TrySetResult(true);
            }
        }

        /// <summary>启动视觉结束后排队展示普通消息，调度与显示异常向调用方传播。</summary>
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

        /// <summary>串行显示确认框；调用方取消时停止排队或关闭已显示的确认框，并观察显示操作结束。</summary>
        public static async Task<bool> ShowConfirmAsync(
            string title,
            string content,
            string primaryButtonText,
            string closeButtonText,
            CancellationToken cancellationToken = default)
        {
            EnsureInitialized();
            bool confirmed = false;
            await _uiThreadRunner!.RunAsync(async () =>
            {
                var dialog = new ConfirmDialog();
                dialog.Configure(title, content, primaryButtonText, closeButtonText);

                var result = await ShowWithGateAsync(dialog, cancellationToken);
                confirmed = result == ContentDialogResult.Primary;
            }, cancellationToken);

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

        /// <summary>等待启动视觉和首次运行说明结束后展示可发送反馈的启动错误。</summary>
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

        /// <summary>等待启动视觉结束后展示首次运行说明；显示尝试结束时解除后续启动提示的等待。</summary>
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

        /// <summary>使用网页图标错误的本地化标题展示统一反馈弹窗，避免另建专用错误流程。</summary>
        public static Task ShowWebIconFetchErrorAsync(string details)
        {
            var resources = new ResourceLoader();
            return ShowErrorAsync(
                resources.GetString("WebIconFetchErrorDialog/Title"),
                details,
                FeedbackSource.WebIcon);
        }

        /// <summary>
        /// 显示统一的应用文档打开失败提示。生成与启动阶段的诊断只写入日志，
        /// 避免向用户暴露无助于恢复操作的内部细节。
        /// </summary>
        public static async Task ShowDocumentOpenFailureAsync()
        {
            try
            {
                var resources = new ResourceLoader();
                await ShowMessageAsync(
                    resources.GetString("DocumentOpenFailureTitle"),
                    resources.GetString("DocumentOpenFailureMessage"),
                    resources.GetString("DialogOK"));
            }
            catch (Exception ex)
            {
                // 错误提示自身失败时只能记录，不能让页面的 async void 事件产生未处理异常。
                Log.Error(ex, "显示应用文档打开失败对话框失败");
            }
        }

        private static async Task ShowMessageCoreAsync(string title, string content, string closeButtonText)
        {
            var dialog = new MessageDialog();
            dialog.Configure(title, content, closeButtonText);
            await ShowWithGateAsync(dialog);
        }

        private static async Task<ContentDialogResult> ShowWithGateAsync(
            ContentDialog dialog,
            CancellationToken cancellationToken = default)
        {
            EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();
            var xamlRoot = _xamlRootProvider!();
            if (xamlRoot is null)
            {
                return ContentDialogResult.None;
            }

            dialog.XamlRoot = xamlRoot;
            await _dialogGate!.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                DispatcherQueue dispatcherQueue = dialog.DispatcherQueue;
                bool displayFinished = false;

                void HideDialog()
                {
                    if (displayFinished)
                    {
                        return;
                    }

                    try
                    {
                        dialog.Hide();
                    }
                    catch (Exception ex)
                    {
                        // 关闭失败不能从取消回调逃逸，显示操作的最终结果仍由下方 await 观察。
                        Log.Warning(ex, "取消确认框时关闭控件失败");
                    }
                }

                void RequestHide()
                {
                    try
                    {
                        if (dispatcherQueue.HasThreadAccess)
                        {
                            HideDialog();
                            return;
                        }

                        if (!dispatcherQueue.TryEnqueue(HideDialog))
                        {
                            Log.Warning("窗口调度器已不可用，无法提交确认框取消请求");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "提交确认框取消请求失败");
                    }
                }

                // 先启动显示再注册关闭回调，保证已经取消的令牌不会在 ShowAsync 前无效调用 Hide。
                var displayOperation = dialog.ShowAsync();
                using CancellationTokenRegistration registration = cancellationToken.Register(RequestHide);
                try
                {
                    ContentDialogResult result = await displayOperation;
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }
                finally
                {
                    // 已提交但尚未执行的 Hide 回调只读取此 UI 线程标志，不再触碰已结束的弹窗。
                    displayFinished = true;
                }
            }
            finally
            {
                _dialogGate.Release();
            }
        }
    }
}
