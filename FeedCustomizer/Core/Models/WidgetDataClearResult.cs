namespace FeedCustomizer.Core.Models;

/// <summary>小组件 WebView 本地数据清理的稳定结果分类。</summary>
internal enum WidgetDataClearStatus
{
    Succeeded,
    Locked,
    Failed
}

/// <summary>
/// 小组件 WebView 本地数据清理结果。诊断仅供日志和排查使用，
/// 不将 PowerShell 的原始输出直接暴露给最终用户。
/// </summary>
internal sealed record WidgetDataClearResult(
    WidgetDataClearStatus Status,
    string Diagnostics);
