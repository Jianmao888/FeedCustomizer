using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace FeedCustomizer.Core.Tools
{
    /// <summary>
    /// 负责确认后打开外部链接或文件。
    /// </summary>
    public sealed class ExternalLaunchService
    {
        private static readonly IAppLog Log = AppLog.For<ExternalLaunchService>();
        private readonly UiThreadRunner _uiThreadRunner;
        private readonly Func<XamlRoot?> _xamlRootProvider;
        private readonly SemaphoreSlim _dialogGate;

        public ExternalLaunchService(
            Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue,
            Func<XamlRoot?> xamlRootProvider,
            Func<Task> waitForSplashHidden,
            SemaphoreSlim dialogGate)
        {
            _uiThreadRunner = new UiThreadRunner(dispatcherQueue, waitForSplashHidden);
            _xamlRootProvider = xamlRootProvider;
            _dialogGate = dialogGate;
        }

        public async Task<bool> OpenLinkAsync(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var result = false;
            await _uiThreadRunner.RunAsync(async () =>
            {
                result = await ConfirmAndLaunchAsync(
                    async () => await Launcher.LaunchUriAsync(uri),
                    "external link");
            });
            return result;
        }

        public async Task<bool> OpenFileAsync(StorageFile file)
        {
            var result = false;
            await _uiThreadRunner.RunAsync(async () =>
            {
                result = await ConfirmAndLaunchAsync(
                    async () => await Launcher.LaunchFileAsync(file),
                    "external file");
            });
            return result;
        }

        private async Task<bool> ConfirmAndLaunchAsync(Func<Task<bool>> launch, string context)
        {
            var xamlRoot = _xamlRootProvider();
            if (xamlRoot is null)
            {
                return false;
            }

            await _dialogGate.WaitAsync();
            try
            {
                var dialog = new ExternalOpenDialog
                {
                    XamlRoot = xamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return false;
                }

                return await launch();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "打开外部内容失败，类型={LaunchContext}", context);
                return false;
            }
            finally
            {
                _dialogGate.Release();
            }
        }
    }
}
