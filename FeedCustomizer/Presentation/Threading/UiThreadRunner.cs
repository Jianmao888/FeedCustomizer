using Microsoft.UI.Dispatching;
using System;
using System.Threading;
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

        /// <summary>
        /// 等待启动界面结束后在 UI 线程执行；取消阻止尚未开始的动作。
        /// 已开始的动作由调用方协作取消并等待结束，保证动作异常始终由当前调用观察。
        /// </summary>
        public async Task RunAsync(Func<Task> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _waitForSplashHidden().WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (_dispatcherQueue.HasThreadAccess)
            {
                await action();
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int operationState = 0;
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                // 只取消仍在队列中的请求；动作一旦开始，完成信号必须等待其真实结果，不能留下失去观察的任务。
                if (Interlocked.CompareExchange(ref operationState, 2, 0) == 0)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
            });

            if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                if (Interlocked.CompareExchange(ref operationState, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await action();
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
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
