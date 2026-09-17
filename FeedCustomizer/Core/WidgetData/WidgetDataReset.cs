using FeedCustomizer.Core.Infrastructure.PowerShell;

namespace FeedCustomizer.Core.WidgetData;

/// <summary>
/// 进程级小组件数据清理服务组合根。唯一协调器拥有串行锁，
/// 避免设置页导航创建多个 ViewModel 后出现并发删除。
/// </summary>
internal static class WidgetDataReset
{
    internal static WidgetDataResetCoordinator Current { get; } = new(PowerShellInfrastructure.WidgetData);
}
