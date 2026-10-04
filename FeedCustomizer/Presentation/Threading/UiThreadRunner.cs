using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;

namespace FeedCustomizer.Presentation.Threading
{
    /// <summary>
    /// 确保操作在窗口 UI 线程上执行，并在执行前等待启动画面隐藏。
    /// </summary>
    public sealed class UiThreadRunner
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Func<Task> _waitForSplashHidden;

        /// <summary>绑定窗口调度器与启动界面结束信号，使界面动作遵守同一显示时序。</summary>
        public UiThreadRunner(DispatcherQueue dispatcherQueue, Func<Task> waitForSplashHidden)
        {
            _dispatcherQueue = dispatcherQueue;
            _waitForSplashHidden = waitForSplashHidden;
        }

        /// <summary>等待启动界面结束后在 UI 线程执行；调度失败和动作异常均传回调用方。</summary>
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
