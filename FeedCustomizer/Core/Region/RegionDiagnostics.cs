using System;

namespace FeedCustomizer.Core.Region;

/// <summary>生成区域操作的稳定失败诊断，避免把异常消息中的不可信内容转交日志或反馈。</summary>
internal static class RegionDiagnostics
{
    /// <summary>只保留操作、异常类型和错误码，不包含文件原文或未经审查的异常消息。</summary>
    internal static string FromException(string operation, Exception exception)
    {
        return $"Operation = {operation}; Error = {exception.GetType().Name}; HResult = 0x{exception.HResult:X8}";
    }
}
