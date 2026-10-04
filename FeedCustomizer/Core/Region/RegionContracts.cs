using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Region;

/// <summary>设备区域的读取结果；代码为空表示读取失败，诊断属于本次读取。</summary>
internal sealed record DeviceRegionResult(string? IsoCountryCode, string Diagnostic);

/// <summary>区域策略的读取结果；未知状态不能解释为系统已解除限制。</summary>
internal sealed record RegionPolicyStateResult(bool? IsEnabled, string Diagnostic);

/// <summary>区域策略修改的稳定状态，不依赖 PowerShell 的本地化错误文本。</summary>
internal enum RegionPolicyOperationStatus
{
    Success,
    Cancelled,
    PolicyNotFound,
    Failed
}

/// <summary>单次策略修改的状态和诊断，避免上层读取其他操作留下的全局诊断。</summary>
internal sealed record RegionPolicyOperationResult(RegionPolicyOperationStatus Status, string Diagnostic);

/// <summary>设备区域与策略的快照；未知设备区域不误报警告，未知策略不误判为已解限。</summary>
internal sealed record RegionState(bool? IsNonEuropeanUnion, bool? IsPolicyEnabled, string Diagnostic)
{
    /// <summary>保持保守的提示规则，只有已识别为非欧盟地区时才显示限制提示。</summary>
    internal bool ShouldShowWarning => IsNonEuropeanUnion == true && IsPolicyEnabled != true;
}

/// <summary>区域查询与策略修改的平台边界；查询取消向上传递，已开始的修改必须完整观察恢复。</summary>
internal interface IRegionPlatform
{
    /// <summary>读取设备设置区域的 ISO 国家代码，不在平台层决定欧盟成员身份。</summary>
    Task<DeviceRegionResult> ReadDeviceRegionAsync(CancellationToken cancellationToken = default);

    /// <summary>只读查询第三方 Widgets 源策略，缺失或无法确定目标时返回未知及诊断。</summary>
    Task<RegionPolicyStateResult> ReadPolicyStateAsync(CancellationToken cancellationToken = default);

    /// <summary>通过既有提权适配器启用策略；取消只在修改开始前生效，系统失败转换为稳定结果。</summary>
    Task<RegionPolicyOperationResult> EnablePolicyAsync(CancellationToken cancellationToken = default);
}
