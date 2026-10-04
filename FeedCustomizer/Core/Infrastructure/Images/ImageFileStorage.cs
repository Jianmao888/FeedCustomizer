using FeedCustomizer.Core.Infrastructure.Deployment;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Infrastructure.Storage;
using System;
using System.IO;
using System.Threading.Tasks;
using StorageFile = Windows.Storage.StorageFile;

namespace FeedCustomizer.Core.Infrastructure.Images;

/// <summary>集中处理图片文件复制、读取和删除，写入与删除限定在应用私有图片目录。</summary>
internal static class ImageFileStorage
{
    private static readonly IAppLog Log = AppLog.For(nameof(ImageFileStorage));

    /// <summary>按既有 GUID 与原扩展名保存所选文件；复制异常由选择器调用方处理。</summary>
    internal static async Task<string> SavePickedImageAsync(StorageFile file)
    {
        string fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.Name)}";
        string imageFolder = GetValidatedImageFolderPath();
        if (!Directory.Exists(imageFolder))
        {
            Directory.CreateDirectory(imageFolder);
        }

        // 复用部署文件边界，不允许图片写入经过重解析点或离开应用专用目录。
        string imagePath = DeploymentFiles.Under(imageFolder, fileName);
        using (var sourceStream = await file.OpenReadAsync())
        using (var destinationStream = File.OpenWrite(imagePath))
        {
            await sourceStream.AsStream().CopyToAsync(destinationStream);
        }

        return fileName;
    }

    /// <summary>按既有读取范围打开图片；空路径、文件缺失或打开失败时返回空值，流由调用方释放。</summary>
    internal static Stream? OpenRead(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            return File.OpenRead(imagePath);
        }
        catch (Exception ex)
        {
            // 保持打开失败返回空值，不将完整外部路径或不可信异常文本写入日志。
            Log.Warning(
                "打开图片文件失败，异常类型={ExceptionType}，HRESULT={ErrorCode}",
                ex.GetType().FullName,
                $"0x{ex.HResult:X8}");
            return null;
        }
    }

    /// <summary>删除应用图片目录中的文件；保留既有默认图片保护与失败降级语义。</summary>
    internal static void DeleteImage(string imagePath)
    {
        string fileName = Path.GetFileName(imagePath);
        if (fileName == Constants.Constants.DefaultImageName)
        {
            // 保持原有大小写比较语义，迁移不改变默认图片识别规则。
            Log.Debug("尝试删除默认图片，操作已忽略");
            return;
        }

        if (string.IsNullOrEmpty(imagePath))
        {
            return;
        }

        try
        {
            string imageFolder = GetValidatedImageFolderPath();
            string relativePath = Path.GetRelativePath(imageFolder, Path.GetFullPath(imagePath));
            string validatedPath = DeploymentFiles.Under(imageFolder, relativePath);
            if (File.Exists(validatedPath))
            {
                File.Delete(validatedPath);
            }
        }
        catch (Exception ex)
        {
            // 失败清理不能遮蔽原始写入异常；越界与占用等失败仍需留下可公开的诊断。
            Log.Warning(
                "删除应用图片失败，目标目录={ImageDirectory}，异常类型={ExceptionType}，HRESULT={ErrorCode}",
                "Images",
                ex.GetType().FullName,
                $"0x{ex.HResult:X8}");
        }
    }

    /// <summary>返回既有私有图片目录，不改变路径布局或目录创建时机。</summary>
    internal static string GetImageFolderPath()
    {
        return AppDataPaths.ImagesFolder;
    }

    /// <summary>按既有 Provider 私有目录边界解析 XML 相对路径，不反向读取注册部署副本。</summary>
    internal static string GetImageFullPathFromXmlRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        return DeploymentFiles.Under(AppDataPaths.PackageLocalFeedProviderFolder, relativePath);
    }

    private static string GetValidatedImageFolderPath()
    {
        return DeploymentFiles.Under(AppDataPaths.PackageLocalFeedProviderFolder, "Images");
    }
}
