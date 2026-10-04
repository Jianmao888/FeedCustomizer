using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Donations;

/// <summary>区分真实许可证状态与查询失败，避免将无法查询误判为尚未购买。</summary>
public enum DonationLicenseStatus
{
    /// <summary>捐赠者版加载项许可证有效。</summary>
    Purchased,

    /// <summary>已完成查询，但未找到有效的捐赠者版许可证。</summary>
    NotPurchased,

    /// <summary>查询失败，当前购买状态未知。</summary>
    Failed,

    /// <summary>查询超过应用允许的等待时间，当前购买状态未知。</summary>
    TimedOut
}

/// <summary>描述 Microsoft Store 购买流程结果，不将用户取消视为购买故障。</summary>
public enum DonationPurchaseStatus
{
    /// <summary>本次购买成功。</summary>
    Purchased,

    /// <summary>Microsoft Store 确认已购买该加载项。</summary>
    AlreadyPurchased,

    /// <summary>Microsoft Store 返回未购买，通常表示用户关闭购买界面。</summary>
    Cancelled,

    /// <summary>购买失败，调用方可根据诊断决定是否提示用户。</summary>
    Failed
}

/// <summary>许可证查询结果；诊断供日志使用，面向用户的提示由调用方选择资源文案。</summary>
/// <param name="Status">许可证状态或明确的查询失败状态。</param>
/// <param name="Diagnostic">平台错误或查询超时的技术诊断。</param>
public sealed record DonationLicenseResult(DonationLicenseStatus Status, string Diagnostic = "");

/// <summary>购买操作结果；调用方取消通过异常传播，不与 Store 界面取消混淆。</summary>
/// <param name="Status">Microsoft Store 购买流程的稳定状态。</param>
/// <param name="Diagnostic">失败时可用于日志的技术诊断。</param>
public sealed record DonationPurchaseResult(DonationPurchaseStatus Status, string Diagnostic = "");

/// <summary>隔离 Microsoft Store 许可证与购买 API，不让应用用例依赖 Windows 类型。</summary>
internal interface IDonationStore
{
    /// <summary>查询捐赠者版许可证；支持取消，平台错误返回 Failed 而非 NotPurchased。</summary>
    Task<DonationLicenseResult> GetLicenseAsync(IntPtr windowHandle, CancellationToken cancellationToken);

    /// <summary>从调用方窗口上下文发起一次购买；不自动重试，调用方取消向上传播。</summary>
    Task<DonationPurchaseResult> PurchaseAsync(IntPtr windowHandle, CancellationToken cancellationToken);
}

/// <summary>只提取稳定异常类型与错误码，避免将包含账户或路径的异常文本转为界面诊断。</summary>
internal static class DonationDiagnostics
{
    internal static string FromException(Exception exception)
    {
        return $"{exception.GetType().Name}: 0x{exception.HResult:X8}";
    }
}
