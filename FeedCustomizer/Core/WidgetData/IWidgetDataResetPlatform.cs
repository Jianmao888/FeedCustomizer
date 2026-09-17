using FeedCustomizer.Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.WidgetData;

/// <summary>小组件数据清理的平台副作用边界。</summary>
internal interface IWidgetDataResetPlatform
{
    /// <summary>终止目标 WebView 进程组并清理经过固定路径验证的本地 Profile。</summary>
    Task<WidgetDataClearResult> ClearAsync(CancellationToken cancellationToken = default);
}
