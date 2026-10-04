using FeedCustomizer.Core.Interface;
using Microsoft.Windows.ApplicationModel.Resources;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Presentation.Dialogs;

/// <summary>将设置用例的确认意图转换为本地化对话框，复用已有窗口级显示顺序。</summary>
internal sealed class SettingsInteraction : ISettingsInteraction
{
    private readonly ResourceLoader _resourceLoader = new();

    /// <summary>展示捐赠购买说明，关闭或取消时不继续购买。</summary>
    public Task<bool> ConfirmDonationAsync(CancellationToken cancellationToken)
    {
        return ConfirmAsync(
            "DonationConfirmTitle",
            "DonationConfirmMessage",
            "DonationConfirmPrimaryButtonText",
            "DonationConfirmCloseButtonText",
            cancellationToken);
    }

    /// <summary>展示系统地区策略修改说明，保持现有资源与按钮文案。</summary>
    public Task<bool> ConfirmRegionUnlockAsync(CancellationToken cancellationToken)
    {
        return ConfirmAsync(
            "RegionPolicyWarningTitle",
            "RegionPolicyWarningMessage",
            "RegionPolicyWarningPrimaryButtonText",
            "DonationConfirmCloseButtonText",
            cancellationToken);
    }

    private async Task<bool> ConfirmAsync(
        string titleKey,
        string messageKey,
        string primaryButtonKey,
        string closeButtonKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 取消传入实际显示流程，避免只截断等待后仍在其他页面弹出已过期的确认框。
        bool confirmed = await DialogService.ShowConfirmAsync(
            _resourceLoader.GetString(titleKey),
            _resourceLoader.GetString(messageKey),
            _resourceLoader.GetString(primaryButtonKey),
            _resourceLoader.GetString(closeButtonKey),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return confirmed;
    }
}
