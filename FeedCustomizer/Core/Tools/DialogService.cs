using FeedCustomizer.Dialogs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Tools
{
    /// <summary>
    /// 负责在主窗口上以串行方式展示各类 ContentDialog。
    /// </summary>
    public static class DialogService
    {
        private static UiThreadRunner? _uiThreadRunner;
        private static Func<XamlRoot?>? _xamlRootProvider;
        private static Func<Task>? _waitForSplashHidden;
        private static SemaphoreSlim? _dialogGate;
        private static TaskCompletionSource<bool>? _firstRunDialogFinished;
        private static bool _firstRunDialogPending;

        public static void Initialize(
            DispatcherQueue dispatcherQueue,
            Func<XamlRoot?> xamlRootProvider,
            Func<Task> waitForSplashHidden,
            SemaphoreSlim dialogGate)
        {
            _uiThreadRunner = new UiThreadRunner(dispatcherQueue, waitForSplashHidden);
            _xamlRootProvider = xamlRootProvider;
            _waitForSplashHidden = waitForSplashHidden;
            _dialogGate = dialogGate;
            _firstRunDialogFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void EnsureInitialized()
        {
            if (_uiThreadRunner is null || _xamlRootProvider is null || _waitForSplashHidden is null || _dialogGate is null || _firstRunDialogFinished is null)
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

            await _uiThreadRunner!.RunAsync(async () =>
            {
                var dialog = new StartupFailureDialog();
                dialog.Configure(title, details);
                await ShowWithGateAsync(dialog);
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
            EnsureInitialized();
            return _uiThreadRunner!.RunAsync(async () =>
            {
                var dialog = new WebIconFetchErrorDialog();
                dialog.Configure(details);
                await ShowWithGateAsync(dialog);
            });
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
