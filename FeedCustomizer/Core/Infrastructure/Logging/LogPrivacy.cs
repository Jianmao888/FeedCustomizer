using System;
using System.Collections.Generic;
using System.IO;

namespace FeedCustomizer.Core.Infrastructure.Logging;

/// <summary>对即将落盘的异常文本隐藏用户目录，同时保留可用于定位问题的相对路径和堆栈。</summary>
internal static class LogPrivacy
{
    internal const int MaximumDiagnosticLength = 16 * 1024;

    internal static string RedactException(Exception exception)
    {
        return PrepareDiagnostic(exception.ToString());
    }

    internal static string RedactPaths(string value)
    {
        string redacted = value;
        foreach ((string path, string replacement) in GetSensitivePaths())
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                redacted = redacted.Replace(path, replacement, StringComparison.OrdinalIgnoreCase);
            }
        }

        return redacted;
    }

    /// <summary>
    /// 将受控基础设施返回的诊断转换为可落盘文本：隐藏当前用户与机器信息，
    /// 并设置长度上限，防止异常外部输出无限放大日志文件。
    /// </summary>
    internal static string PrepareDiagnostic(string? value)
    {
        try
        {
            return PrepareDiagnosticCore(value);
        }
        catch
        {
            // 脱敏本身失败时宁可放弃原始诊断，也不能让日志改变业务结果或泄露未经处理的内容。
            return "（诊断脱敏失败，原始内容未写入）";
        }
    }

    private static string PrepareDiagnosticCore(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "（无）";
        }

        string redacted = RedactPaths(value.Trim())
            .Replace("\0", "�", StringComparison.Ordinal);
        redacted = ReplaceIfPresent(redacted, Environment.UserName, "%USERNAME%");
        redacted = ReplaceIfPresent(
            redacted,
            Environment.GetEnvironmentVariable("USERDOMAIN"),
            "%USERDOMAIN%");
        redacted = ReplaceIfPresent(
            redacted,
            Environment.GetEnvironmentVariable("USERDNSDOMAIN"),
            "%USERDNSDOMAIN%");
        redacted = ReplaceIfPresent(redacted, Environment.MachineName, "%COMPUTERNAME%");

        if (redacted.Length <= MaximumDiagnosticLength)
        {
            return redacted;
        }

        return redacted[..MaximumDiagnosticLength] +
            $"{Environment.NewLine}……诊断已截断，原始字符数={redacted.Length}";
    }

    private static string ReplaceIfPresent(string value, string? sensitiveValue, string replacement)
    {
        if (string.IsNullOrWhiteSpace(sensitiveValue))
        {
            return value;
        }

        return value.Replace(sensitiveValue, replacement, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string Path, string Replacement)> GetSensitivePaths()
    {
        // 先替换更具体的目录，避免用户目录的短前缀提前吞掉 LocalAppData 与临时目录。
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        yield return (Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "%TEMP%");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
    }
}
