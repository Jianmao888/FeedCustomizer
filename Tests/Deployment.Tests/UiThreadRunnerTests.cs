using FeedCustomizer.Presentation.Threading;
using Microsoft.UI.Dispatching;

/// <summary>使用可控调度器验证启动等待、排队取消和已执行动作的异常观察。</summary>
internal static class UiThreadRunnerTests
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> Cases { get; } =
    [
        ("UI动作等待Splash时可取消且不入队", SplashCancellationAsync),
        ("UI动作排队后取消不再执行", QueuedCancellationAsync),
        ("UI动作开始后取消仍完整观察动作异常", RunningFailureAfterCancellationAsync),
        ("UI动作开始后取消等待真实结束再返回取消", RunningSuccessAfterCancellationAsync),
        ("UI动作调度失败向调用方传播", DispatchFailureAsync),
        ("UI线程直接执行时原样传播动作异常", DirectActionFailureAsync),
    ];

    private static async Task SplashCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var splash = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DispatcherQueue();
        var runner = new UiThreadRunner(dispatcher, () => splash.Task);
        int actionCalls = 0;
        Task operation = runner.RunAsync(() =>
        {
            actionCalls++;
            return Task.CompletedTask;
        }, cancellation.Token);

        cancellation.Cancel();
        await RequireCancellationAsync(operation);
        splash.SetResult(true);
        Require(dispatcher.PendingCount == 0 && actionCalls == 0);
    }

    private static async Task QueuedCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new DispatcherQueue();
        var runner = new UiThreadRunner(dispatcher, () => Task.CompletedTask);
        int actionCalls = 0;
        Task operation = runner.RunAsync(() =>
        {
            actionCalls++;
            return Task.CompletedTask;
        }, cancellation.Token);
        Require(dispatcher.PendingCount == 1);

        cancellation.Cancel();
        await RequireCancellationAsync(operation);
        dispatcher.RunNext();
        Require(dispatcher.PendingCount == 0 && actionCalls == 0);
    }

    private static async Task RunningFailureAfterCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var pendingAction = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("正在执行的 UI 动作测试失败。");
        var dispatcher = new DispatcherQueue();
        var runner = new UiThreadRunner(dispatcher, () => Task.CompletedTask);
        Task operation = runner.RunAsync(() => pendingAction.Task, cancellation.Token);
        dispatcher.RunNext();

        cancellation.Cancel();
        Require(!operation.IsCompleted);
        pendingAction.SetException(failure);
        await RequireSameFailureAsync(operation, failure);
    }

    private static async Task RunningSuccessAfterCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var pendingAction = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DispatcherQueue();
        var runner = new UiThreadRunner(dispatcher, () => Task.CompletedTask);
        Task operation = runner.RunAsync(() => pendingAction.Task, cancellation.Token);
        dispatcher.RunNext();

        cancellation.Cancel();
        Require(!operation.IsCompleted);
        pendingAction.SetResult(true);
        await RequireCancellationAsync(operation);
    }

    private static async Task DispatchFailureAsync()
    {
        var dispatcher = new DispatcherQueue { AcceptEnqueue = false };
        var runner = new UiThreadRunner(dispatcher, () => Task.CompletedTask);
        int actionCalls = 0;
        Task operation = runner.RunAsync(() =>
        {
            actionCalls++;
            return Task.CompletedTask;
        });

        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (InvalidOperationException)
        {
            Require(actionCalls == 0 && dispatcher.PendingCount == 0);
            return;
        }

        throw new InvalidOperationException("UI 调度失败未传播给调用方。");
    }

    private static async Task DirectActionFailureAsync()
    {
        var failure = new IOException("UI 线程直接动作测试失败。");
        var dispatcher = new DispatcherQueue { HasThreadAccess = true };
        var runner = new UiThreadRunner(dispatcher, () => Task.CompletedTask);
        await RequireSameFailureAsync(runner.RunAsync(() => Task.FromException(failure)), failure);
        Require(dispatcher.PendingCount == 0);
    }

    private static async Task RequireCancellationAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("UI 动作取消未传播给调用方。");
    }

    private static async Task RequireSameFailureAsync(Task operation, Exception expected)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception actual) when (ReferenceEquals(actual, expected))
        {
            return;
        }

        throw new InvalidOperationException("UI 动作异常未原样传播给调用方。");
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("UI 调度时序验证失败。");
        }
    }
}
