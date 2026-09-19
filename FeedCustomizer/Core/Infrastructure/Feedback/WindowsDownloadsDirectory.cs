using System;
using System.Runtime.InteropServices;

namespace FeedCustomizer.Core.Infrastructure.Feedback;

/// <summary>
/// 读取 Windows 为当前用户配置的真实 Downloads 目录。
/// 不使用 DownloadsFolder，是因为其面向沙盒应用的 API 会自动在 Downloads 下创建 AUMID 命名目录。
/// </summary>
internal static partial class WindowsDownloadsDirectory
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>返回 Windows 已知文件夹配置的 Downloads 物理路径，支持用户迁移 Downloads 位置。</summary>
    internal static string GetPath()
    {
        IntPtr nativePath = IntPtr.Zero;
        int hresult = SHGetKnownFolderPath(in DownloadsFolderId, 0, IntPtr.Zero, out nativePath);
        try
        {
            if (hresult < 0)
            {
                Marshal.ThrowExceptionForHR(hresult);
            }

            string? downloadsPath = Marshal.PtrToStringUni(nativePath);
            if (string.IsNullOrWhiteSpace(downloadsPath))
            {
                throw new InvalidOperationException("Windows 未返回有效的下载目录路径。");
            }

            return downloadsPath;
        }
        finally
        {
            if (nativePath != IntPtr.Zero)
            {
                CoTaskMemFree(nativePath);
            }
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHGetKnownFolderPath")]
    private static partial int SHGetKnownFolderPath(
        in Guid knownFolderId,
        uint flags,
        IntPtr token,
        out IntPtr path);

    [LibraryImport("ole32.dll", EntryPoint = "CoTaskMemFree")]
    private static partial void CoTaskMemFree(IntPtr memory);
}
