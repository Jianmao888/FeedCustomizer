using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Services.Store;
using DonationConstants = FeedCustomizer.Core.Constants.Constants;

namespace FeedCustomizer.Core.Tools
{
    /// <summary>
    /// 负责通过 Microsoft Store 应用内购买 API 检查并购买“捐赠者版”加载项。
    /// </summary>
    public enum DonationPurchaseResult
    {
        Purchased,
        AlreadyPurchased,
        Cancelled,
        Failed
    }

    public static class DonationService
    {
        public static async Task<bool> IsDonorEditionPurchasedAsync(IntPtr windowHandle)
        {
            try
            {
                // Store 许可证查询可能耗时，放到后台线程执行，避免阻塞设置页面的打开
                return await Task.Run(async () =>
                {
                    var context = CreateContext(windowHandle);
                    var license = await context.GetAppLicenseAsync();
                    if (license.AddOnLicenses.TryGetValue(DonationConstants.DonationAddOnStoreId, out var addOnLicense))
                    {
                        return addOnLicense.IsActive;
                    }

                    return false;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查捐赠者版许可证失败: {ex.Message}");
            }

            return false;
        }

        public static async Task<DonationPurchaseResult> PurchaseDonorEditionAsync(IntPtr windowHandle)
        {
            try
            {
                var context = CreateContext(windowHandle);
                var result = await context.RequestPurchaseAsync(DonationConstants.DonationAddOnStoreId);
                return result.Status switch
                {
                    StorePurchaseStatus.Succeeded => DonationPurchaseResult.Purchased,
                    StorePurchaseStatus.AlreadyPurchased => DonationPurchaseResult.AlreadyPurchased,
                    StorePurchaseStatus.NotPurchased => DonationPurchaseResult.Cancelled,
                    _ => DonationPurchaseResult.Failed
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"购买捐赠者版异常: {ex.Message}");
                return DonationPurchaseResult.Failed;
            }
        }

        private static StoreContext CreateContext(IntPtr windowHandle)
        {
            var context = StoreContext.GetDefault();
            if (windowHandle != IntPtr.Zero)
            {
                // 桌面应用必须为 StoreContext 指定所有者窗口，购买弹窗才能正常显示
                WinRT.Interop.InitializeWithWindow.Initialize(context, windowHandle);
            }

            return context;
        }
    }
}
