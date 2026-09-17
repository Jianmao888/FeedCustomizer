using FeedCustomizer.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.WidgetData;

/// <summary>
/// 小组件 WebView 数据清理的唯一用例入口。锁覆盖进程终止、等待句柄释放和目录替换，
/// 防止多个设置页实例同时删除同一 Profile。
/// </summary>
internal sealed class WidgetDataResetCoordinator(IWidgetDataResetPlatform platform)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>串行清理 Windows 小组件面板的专用 WebView Profile。</summary>
    internal async Task<WidgetDataClearResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
        {
            return new WidgetDataClearResult(
                WidgetDataClearStatus.Failed,
                "等待已有的小组件数据清理操作超时。");
        }

        try
        {
            return await platform.ClearAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WidgetDataClearResult(
                WidgetDataClearStatus.Failed,
                "小组件数据清理已取消。");
        }
        catch (Exception ex)
        {
            return new WidgetDataClearResult(
                WidgetDataClearStatus.Failed,
                $"小组件数据清理发生未处理异常：{ex}");
        }
        finally
        {
            _gate.Release();
        }
    }
}
