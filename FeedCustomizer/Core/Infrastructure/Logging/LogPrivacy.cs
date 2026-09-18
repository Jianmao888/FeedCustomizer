using System;
using System.Collections.Generic;
using System.IO;

namespace FeedCustomizer.Core.Infrastructure.Logging;

/// <summary>对即将落盘的异常文本隐藏用户目录，同时保留可用于定位问题的相对路径和堆栈。</summary>
internal static class LogPrivacy
{
    internal static string RedactException(Exception exception)
    {
        return RedactPaths(exception.ToString());
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

    private static IEnumerable<(string Path, string Replacement)> GetSensitivePaths()
    {
        // 先替换更具体的目录，避免用户目录的短前缀提前吞掉 LocalAppData 与临时目录。
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        yield return (Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "%TEMP%");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
    }
}
