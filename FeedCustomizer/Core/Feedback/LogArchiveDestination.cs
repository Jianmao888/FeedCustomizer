using System;
using System.IO;

namespace FeedCustomizer.Core.Feedback;

/// <summary>生成并验证日志导出的应用专用目录，不负责获取 Windows 已知文件夹。</summary>
internal static class LogArchiveDestination
{
    /// <summary>
    /// 返回 Downloads 下的应用专用导出目录。名称必须是直接子目录，
    /// 防止未来调用方误把路径片段或上级目录传入文件操作。
    /// </summary>
    internal static string CreateDirectoryPath(string downloadsDirectory, string exportFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportFolderName);

        string fullDownloadsDirectory = NormalizeDirectoryPath(downloadsDirectory);
        string exportDirectory = Path.GetFullPath(
            Path.Combine(fullDownloadsDirectory, exportFolderName));
        EnsureChildPath(fullDownloadsDirectory, exportDirectory);
        return exportDirectory;
    }

    internal static void EnsureChildPath(string parentPath, string childPath)
    {
        string normalizedParent = NormalizeDirectoryPath(parentPath);
        string fullParent = normalizedParent.EndsWith(Path.DirectorySeparatorChar) ||
            normalizedParent.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedParent
            : normalizedParent + Path.DirectorySeparatorChar;
        string fullChild = Path.GetFullPath(childPath);
        if (!fullChild.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("日志归档路径超出已验证目录。");
        }
    }

    private static string NormalizeDirectoryPath(string directoryPath)
    {
        string fullPath = Path.GetFullPath(directoryPath);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || fullPath.Length <= root.Length)
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
