using System;
using System.IO;

namespace FeedCustomizer.Core.Feedback;

/// <summary>将系统返回的日志物理路径转换为与资源管理器显示一致的用户可读路径。</summary>
internal static class LogArchivePathFormatter
{
    /// <summary>
    /// 仅替换路径中与当前 AUMID 完全相同的目录段。附件仍使用原始物理路径，
    /// 此结果只用于界面和邮件正文，不能用于文件读写。
    /// </summary>
    internal static string CreateDisplayPath(
        string physicalPath,
        string appUserModelId,
        string displayFolderName)
    {
        if (string.IsNullOrWhiteSpace(physicalPath) ||
            string.IsNullOrWhiteSpace(appUserModelId) ||
            string.IsNullOrWhiteSpace(displayFolderName))
        {
            return physicalPath;
        }

        string fullPath = Path.GetFullPath(physicalPath);
        string? physicalDirectory = Path.GetDirectoryName(fullPath);
        if (physicalDirectory is null ||
            !Path.GetFileName(physicalDirectory).Equals(
                appUserModelId,
                StringComparison.OrdinalIgnoreCase))
        {
            return physicalPath;
        }

        string? downloadsDirectory = Path.GetDirectoryName(physicalDirectory);
        if (downloadsDirectory is null)
        {
            return physicalPath;
        }

        return Path.Combine(
            downloadsDirectory,
            displayFolderName,
            Path.GetFileName(fullPath));
    }
}
