using FeedCustomizer.Core.Donations;
using FeedCustomizer.Core.Infrastructure.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;
using DonationConstants = FeedCustomizer.Core.Constants.Constants;

namespace FeedCustomizer.Core.Infrastructure.Donations;

/// <summary>适配 Microsoft Store；查询在后台运行，购买保留调用方的窗口与 UI 线程上下文。</summary>
internal sealed class WindowsDonationStore : IDonationStore
{
    private static readonly IAppLog Log = AppLog.For<WindowsDonationStore>();

    /// <summary>查询加载项许可证，明确区分尚未购买、平台查询失败和调用方取消。</summary>
    public async Task<DonationLicenseResult> GetLicenseAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // 保留原有后台查询策略，StoreContext 创建与许可证读取都不阻塞设置页面的 UI。
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                StoreContext context = CreateContext(windowHandle);
                StoreAppLicense license = await context.GetAppLicenseAsync().AsTask(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                bool purchased = license.AddOnLicenses.TryGetValue(
                    DonationConstants.DonationAddOnStoreId,
                    out StoreLicense? addOnLicense) && addOnLicense.IsActive;
                return new DonationLicenseResult(
                    purchased ? DonationLicenseStatus.Purchased : DonationLicenseStatus.NotPurchased);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log.Warning(ex, "检查捐赠者版许可证失败，加载项={AddOnStoreId}", DonationConstants.DonationAddOnStoreId);
            return new DonationLicenseResult(DonationLicenseStatus.Failed, DonationDiagnostics.FromException(ex));
        }
    }

    /// <summary>使用当前 UI 上下文发起一次购买，并将 Store 状态转换为应用结果。</summary>
    public async Task<DonationPurchaseResult> PurchaseAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // 购买会显示 Store 界面，不能像许可证查询一样移到 Task.Run，也不自动重试。
            StoreContext context = CreateContext(windowHandle);
            StorePurchaseResult result = await context.RequestPurchaseAsync(
                DonationConstants.DonationAddOnStoreId).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            DonationPurchaseStatus status = result.Status switch
            {
                StorePurchaseStatus.Succeeded => DonationPurchaseStatus.Purchased,
                StorePurchaseStatus.AlreadyPurchased => DonationPurchaseStatus.AlreadyPurchased,
                StorePurchaseStatus.NotPurchased => DonationPurchaseStatus.Cancelled,
                _ => DonationPurchaseStatus.Failed
            };
            if (status != DonationPurchaseStatus.Failed)
            {
                return new DonationPurchaseResult(status);
            }

            string diagnostic = $"StorePurchaseStatus={result.Status}; " +
                $"HRESULT={(result.ExtendedError is null ? "none" : $"0x{result.ExtendedError.HResult:X8}")}";
            Log.Warning(
                "Microsoft Store 未能完成捐赠购买，加载项={AddOnStoreId}，状态={StoreStatus}，诊断={Diagnostic}",
                DonationConstants.DonationAddOnStoreId,
                result.Status,
                diagnostic);
            return new DonationPurchaseResult(DonationPurchaseStatus.Failed, diagnostic);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log.Error(ex, "购买捐赠者版时发生异常，加载项={AddOnStoreId}", DonationConstants.DonationAddOnStoreId);
            return new DonationPurchaseResult(DonationPurchaseStatus.Failed, DonationDiagnostics.FromException(ex));
        }
    }

    private static StoreContext CreateContext(IntPtr windowHandle)
    {
        StoreContext context = StoreContext.GetDefault();
        if (windowHandle != IntPtr.Zero)
        {
            // 桌面 Store 操作必须绑定窗口所有者，避免购买界面失去正确的窗口上下文。
            WinRT.Interop.InitializeWithWindow.Initialize(context, windowHandle);
        }

        return context;
    }

}
