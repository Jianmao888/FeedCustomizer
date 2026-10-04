using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Interface;

/// <summary>设置用例的确认请求；资源与对话框由呈现层处理，不向 ViewModel 暴露控件。</summary>
internal interface ISettingsInteraction
{
    /// <summary>在购买之前取得用户确认；调用方取消时不继续发起 Store 操作。</summary>
    Task<bool> ConfirmDonationAsync(CancellationToken cancellationToken);

    /// <summary>在修改系统地区策略之前取得用户确认；调用方取消向上传播。</summary>
    Task<bool> ConfirmRegionUnlockAsync(CancellationToken cancellationToken);
}
