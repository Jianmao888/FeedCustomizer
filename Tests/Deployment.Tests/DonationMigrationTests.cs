using FeedCustomizer.Core.Donations;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>使用 Store 替身验证捐赠状态与取消边界，不触发真实购买或 Windows UI。</summary>
internal static class DonationMigrationTests
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> Cases { get; } =
    [
        ("DonationLicenseStates", LicenseStatesAsync),
        ("DonationLicenseFailure", LicenseFailureAsync),
        ("DonationLicenseTimeout", LicenseTimeoutAsync),
        ("DonationLicenseCancellation", LicenseCancellationAsync),
        ("DonationLicensePreCancellation", LicensePreCancellationAsync),
        ("DonationPurchaseStates", PurchaseStatesAsync),
        ("DonationPurchaseFailure", PurchaseFailureAsync),
        ("DonationPurchaseCancellation", PurchaseCancellationAsync),
        ("DonationPurchasePreCancellation", PurchasePreCancellationAsync)
    ];

    private static async Task LicenseStatesAsync()
    {
        var windowHandle = new IntPtr(73);
        foreach (DonationLicenseStatus status in new[]
        {
            DonationLicenseStatus.Purchased,
            DonationLicenseStatus.NotPurchased,
            DonationLicenseStatus.Failed
        })
        {
            var expected = new DonationLicenseResult(status, "store diagnostic");
            var store = new FakeDonationStore
            {
                GetLicense = (_, _) => Task.FromResult(expected)
            };
            var service = new DonationService(store);
            DonationLicenseResult result = await service.GetLicenseAsync(windowHandle);
            Check(result == expected, "许可证查询必须保留真实状态与诊断。");
            Check(store.LicenseCalls == 1 && store.LastWindowHandle == windowHandle,
                "查询必须只调用一次，并转交窗口所有者。");
        }
    }

    private static async Task LicenseFailureAsync()
    {
        var store = new FakeDonationStore
        {
            GetLicense = (_, _) => Task.FromException<DonationLicenseResult>(new InvalidOperationException())
        };
        DonationLicenseResult result = await new DonationService(store).GetLicenseAsync(IntPtr.Zero);
        Check(result.Status == DonationLicenseStatus.Failed && !string.IsNullOrWhiteSpace(result.Diagnostic),
            "基础设施异常必须成为有诊断的 Failed，不能成为 NotPurchased。");
        Check(store.LicenseCalls == 1, "许可证查询失败不能隐式重试。");
    }

    private static async Task LicenseTimeoutAsync()
    {
        var pending = new TaskCompletionSource<DonationLicenseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeDonationStore
        {
            // 故意不完成平台任务，验证应用等待能自行结束并向平台传递取消。
            GetLicense = (_, _) => pending.Task
        };
        var service = new DonationService(store, TimeSpan.FromMilliseconds(30));
        DonationLicenseResult result = await service.GetLicenseAsync(IntPtr.Zero).WaitAsync(TimeSpan.FromSeconds(3));
        Check(result.Status == DonationLicenseStatus.TimedOut && !string.IsNullOrWhiteSpace(result.Diagnostic),
            "许可证查询超时必须独立于失败和未购买。");
        Check(store.LastLicenseToken.IsCancellationRequested && store.LicenseCalls == 1,
            "查询超时必须取消平台查询，并且不得重试。");
        pending.SetResult(new DonationLicenseResult(DonationLicenseStatus.NotPurchased));
    }

    private static async Task LicenseCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<DonationLicenseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeDonationStore
        {
            GetLicense = (_, _) => pending.Task
        };
        Task<DonationLicenseResult> query = new DonationService(store).GetLicenseAsync(IntPtr.Zero, cancellation.Token);
        cancellation.Cancel();
        await ExpectCancellationAsync(query);
        Check(store.LastLicenseToken.IsCancellationRequested && store.LicenseCalls == 1,
            "调用方取消必须传递到查询平台，并且不得重试。");
        pending.SetResult(new DonationLicenseResult(DonationLicenseStatus.Purchased));
    }

    private static async Task LicensePreCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new FakeDonationStore();
        await ExpectCancellationAsync(new DonationService(store).GetLicenseAsync(IntPtr.Zero, cancellation.Token));
        Check(store.LicenseCalls == 0, "已取消的调用不能启动 Store 查询。");
    }

    private static async Task PurchaseStatesAsync()
    {
        var windowHandle = new IntPtr(91);
        using var cancellation = new CancellationTokenSource();
        foreach (DonationPurchaseStatus status in Enum.GetValues<DonationPurchaseStatus>())
        {
            var expected = new DonationPurchaseResult(status, "purchase diagnostic");
            var store = new FakeDonationStore
            {
                Purchase = (_, _) => Task.FromResult(expected)
            };
            // 查询超时不应创建购买用的截止时间，也不能替换调用方的取消令牌。
            var service = new DonationService(store, TimeSpan.FromMilliseconds(1));
            DonationPurchaseResult result = await service.PurchaseAsync(windowHandle, cancellation.Token);
            Check(result == expected && store.LastPurchaseToken == cancellation.Token,
                "购买必须保留状态、诊断与调用方取消令牌。");
            Check(store.PurchaseCalls == 1 && store.LastWindowHandle == windowHandle,
                "购买必须只调用一次，并转交窗口所有者。");
        }
    }

    private static async Task PurchaseFailureAsync()
    {
        var store = new FakeDonationStore
        {
            Purchase = (_, _) => Task.FromException<DonationPurchaseResult>(new InvalidOperationException())
        };
        DonationPurchaseResult result = await new DonationService(store).PurchaseAsync(IntPtr.Zero);
        Check(result.Status == DonationPurchaseStatus.Failed && !string.IsNullOrWhiteSpace(result.Diagnostic),
            "购买异常必须成为有诊断的 Failed。");
        Check(store.PurchaseCalls == 1, "购买失败不能隐式重试。");
    }

    private static async Task PurchaseCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeDonationStore
        {
            Purchase = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("取消后的购买不应继续执行。");
            }
        };
        Task<DonationPurchaseResult> purchase = new DonationService(store).PurchaseAsync(IntPtr.Zero, cancellation.Token);
        cancellation.Cancel();
        await ExpectCancellationAsync(purchase);
        Check(store.LastPurchaseToken == cancellation.Token && store.PurchaseCalls == 1,
            "调用方取消不能变成 Store 界面 Cancelled，也不能触发重新购买。");
    }

    private static async Task PurchasePreCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new FakeDonationStore();
        await ExpectCancellationAsync(new DonationService(store).PurchaseAsync(IntPtr.Zero, cancellation.Token));
        Check(store.PurchaseCalls == 0, "已取消的调用不能启动购买界面。");
    }

    private static async Task ExpectCancellationAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("预期调用方取消向上传播。");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeDonationStore : IDonationStore
    {
        internal Func<IntPtr, CancellationToken, Task<DonationLicenseResult>> GetLicense { get; init; } =
            (_, _) => Task.FromResult(new DonationLicenseResult(DonationLicenseStatus.NotPurchased));

        internal Func<IntPtr, CancellationToken, Task<DonationPurchaseResult>> Purchase { get; init; } =
            (_, _) => Task.FromResult(new DonationPurchaseResult(DonationPurchaseStatus.Cancelled));

        internal int LicenseCalls { get; private set; }

        internal int PurchaseCalls { get; private set; }

        internal IntPtr LastWindowHandle { get; private set; }

        internal CancellationToken LastLicenseToken { get; private set; }

        internal CancellationToken LastPurchaseToken { get; private set; }

        public Task<DonationLicenseResult> GetLicenseAsync(IntPtr windowHandle, CancellationToken cancellationToken)
        {
            LicenseCalls++;
            LastWindowHandle = windowHandle;
            LastLicenseToken = cancellationToken;
            return GetLicense(windowHandle, cancellationToken);
        }

        public Task<DonationPurchaseResult> PurchaseAsync(IntPtr windowHandle, CancellationToken cancellationToken)
        {
            PurchaseCalls++;
            LastWindowHandle = windowHandle;
            LastPurchaseToken = cancellationToken;
            return Purchase(windowHandle, cancellationToken);
        }
    }
}
