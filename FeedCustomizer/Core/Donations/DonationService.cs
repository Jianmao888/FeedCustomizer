using FeedCustomizer.Core.Infrastructure.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Donations;

/// <summary>协调捐赠者版许可证与购买操作；许可证限时查询，购买只遵守调用方取消。</summary>
internal sealed class DonationService
{
    private static readonly IAppLog Log = AppLog.For<DonationService>();
    private readonly IDonationStore _store;
    private readonly TimeSpan _licenseTimeout;

    /// <summary>注入 Store 适配器与有限查询时间，允许纯逻辑测试模拟平台结果。</summary>
    internal DonationService(IDonationStore store, TimeSpan? licenseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _licenseTimeout = licenseTimeout ?? TimeSpan.FromSeconds(5);
        if (_licenseTimeout <= TimeSpan.Zero || _licenseTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(licenseTimeout));
        }
    }

    /// <summary>限时查询许可证；超时取消平台查询，调用方取消保持取消异常。</summary>
    internal async Task<DonationLicenseResult> GetLicenseAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutSource = new CancellationTokenSource(_licenseTimeout);
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        Task<DonationLicenseResult>? query = null;
        try
        {
            query = _store.GetLicenseAsync(windowHandle, operationSource.Token);
            // 等待本身也遵守取消边界，适配器未及时完成取消时不继续阻塞设置页。
            DonationLicenseResult result = await query.WaitAsync(operationSource.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveUnfinishedQuery(query);
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            ObserveUnfinishedQuery(query);
            return new DonationLicenseResult(
                DonationLicenseStatus.TimedOut,
                "DonationLicenseQueryTimedOut");
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log.Warning(ex, "捐赠者版许可证用例查询失败");
            return new DonationLicenseResult(DonationLicenseStatus.Failed, DonationDiagnostics.FromException(ex));
        }
    }

    /// <summary>发起一次购买并保留调用线程上下文，不以许可证查询的短超时限制购买界面。</summary>
    internal async Task<DonationPurchaseResult> PurchaseAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            DonationPurchaseResult result = await _store.PurchaseAsync(windowHandle, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log.Error(ex, "捐赠者版购买用例失败");
            return new DonationPurchaseResult(DonationPurchaseStatus.Failed, DonationDiagnostics.FromException(ex));
        }
    }

    private static void ObserveUnfinishedQuery(Task<DonationLicenseResult>? query)
    {
        if (query is null)
        {
            return;
        }

        // 限时等待结束后，平台任务仍可能完成并失败；继续观察其异常，避免遗留不可见故障。
        _ = query.ContinueWith(
            task => Log.Warning(task.Exception!.GetBaseException(), "已取消的捐赠许可证查询随后失败"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
