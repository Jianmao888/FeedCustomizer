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
    public sealed class DialogService
    {
        private readonly UiThreadRunner _uiThreadRunner;
        private readonly Func<XamlRoot?> _xamlRootProvider;
        private readonly Func<Task> _waitForSplashHidden;
        private readonly SemaphoreSlim _dialogGate;
        private readonly TaskCompletionSource<bool> _firstRunDialogFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _firstRunDialogPending;

        public DialogService(
            DispatcherQueue dispatcherQueue,
            Func<XamlRoot?> xamlRootProvider,
            Func<Task> waitForSplashHidden,
            SemaphoreSlim dialogGate)
        {
            _uiThreadRunner = new UiThreadRunner(dispatcherQueue, waitForSplashHidden);
            _xamlRootProvider = xamlRootProvider;
            _waitForSplashHidden = waitForSplashHidden;
            _dialogGate = dialogGate;
        }

        public void SetFirstRunDialogPending(bool pending)
        {
            _firstRunDialogPending = pending;
            if (!pending)
            {
                _firstRunDialogFinished.TrySetResult(true);
            }
        }

        public Task ShowMessageAsync(string title, string content, string closeButtonText)
        {
            return _uiThreadRunner.RunAsync(async () =>
            {
                var dialog = new MessageDialog();
                dialog.Configure(title, content, closeButtonText);
                await ShowWithGateAsync(dialog);
            });
        }

        public async Task ShowStartupFailureAsync(string title, string details)
        {
            await _waitForSplashHidden();
            await _firstRunDialogFinished.Task;

            await _uiThreadRunner.RunAsync(async () =>
            {
                var dialog = new StartupFailureDialog();
                dialog.Configure(title, details);
                await ShowWithGateAsync(dialog);
            });
        }

        public async Task ShowFirstRunAsync()
        {
            await _waitForSplashHidden();

            if (!_firstRunDialogPending)
            {
                _firstRunDialogFinished.TrySetResult(true);
                return;
            }

            try
            {
                await _uiThreadRunner.RunAsync(async () =>
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
                _firstRunDialogFinished.TrySetResult(true);
            }
        }

        public Task ShowWebIconFetchErrorAsync(string details)
        {
            return _uiThreadRunner.RunAsync(async () =>
            {
                var dialog = new WebIconFetchErrorDialog();
                dialog.Configure(details);
                await ShowWithGateAsync(dialog);
            });
        }

        private async Task ShowWithGateAsync(ContentDialog dialog)
        {
            var xamlRoot = _xamlRootProvider();
            if (xamlRoot is null)
            {
                return;
            }

            dialog.XamlRoot = xamlRoot;
            await _dialogGate.WaitAsync();
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                _dialogGate.Release();
            }
        }
    }
}
