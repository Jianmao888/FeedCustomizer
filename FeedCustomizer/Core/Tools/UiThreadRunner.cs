using Microsoft.UI.Dispatching;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Tools
{
    /// <summary>
    /// 确保操作在窗口 UI 线程上执行，并在执行前等待启动画面隐藏。
    /// </summary>
    public sealed class UiThreadRunner
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Func<Task> _waitForSplashHidden;

        public UiThreadRunner(DispatcherQueue dispatcherQueue, Func<Task> waitForSplashHidden)
        {
            _dispatcherQueue = dispatcherQueue;
            _waitForSplashHidden = waitForSplashHidden;
        }

        public async Task RunAsync(Func<Task> action)
        {
            await _waitForSplashHidden();

            if (_dispatcherQueue.HasThreadAccess)
            {
                await action();
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
            {
                completion.TrySetException(new InvalidOperationException("无法切换到窗口 UI 线程。"));
            }

            await completion.Task;
        }
    }
}
