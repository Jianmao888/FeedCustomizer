using Microsoft.UI.Xaml.Media.Imaging;
using FeedCustomizer.Core.Infrastructure.Logging;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace FeedCustomizer.Core.Tools
{
    public static class ImageHelper
    {
        private static readonly IAppLog Log = AppLog.For(nameof(ImageHelper));

        /// <summary>
        /// 选择图片并保存到指定目录
        /// </summary>
        /// <returns>保存后的文件名；用户取消时返回空字符串</returns>
        public static async Task<string> PickAndSaveImageAsync()
        {
            try
            {
                // 创建文件选择器
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.Thumbnail,
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary
                };

                // 设置图片文件类型
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".gif");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".ico");

                // 获取窗口句柄（WinUI 3 需要）
                var mainWindow = App.MainWindow; // 需要在 App.xaml.cs 中公开
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);

                // 选择文件
                var file = await picker.PickSingleFileAsync();
                if (file == null) return string.Empty;

                // 生成GUID文件名
                string fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.Name)}";

                // 构建保存路径
                string imageFolder = AppDataPaths.ImagesFolder;

                // 创建目录
                if (!Directory.Exists(imageFolder))
                    Directory.CreateDirectory(imageFolder);

                // 完整文件路径
                string imagePath = Path.Combine(imageFolder, fileName);

                // 复制文件
                using (var sourceStream = await file.OpenReadAsync())
                using (var destinationStream = File.OpenWrite(imagePath))
                {
                    await sourceStream.AsStream().CopyToAsync(destinationStream);
                }

                return fileName;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "选择并保存图片失败");
                return string.Empty;
                //throw new Exception($"选择图片失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从文件路径加载图片
        /// </summary>
        public static async Task<BitmapImage?> LoadImageFromPathAsync(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return null;

            try
            {
                var bitmapImage = new BitmapImage();
                using (var stream = File.OpenRead(imagePath).AsRandomAccessStream())
                {
                    await bitmapImage.SetSourceAsync(stream);
                }
                return bitmapImage;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 删除图片文件
        /// </summary>
        public static void DeleteImage(string imagePath)
        {
            string fileName = Path.GetFileName(imagePath);
            if (fileName == Constants.Constants.DefaultImageName)
            {
                // 不删除默认图片
                Log.Debug("尝试删除默认图片，操作已忽略");
                return;
            }
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    File.Delete(imagePath);
                }
                catch
                {
                    // 删除失败时忽略
                }
            }
        }

        /// <summary>
        /// 获取保存图片的目录路径
        /// </summary>
        public static string GetImageFolderPath()
        {
            return AppDataPaths.ImagesFolder;
        }

        /// <summary>
        /// 根据 XML 中的相对路径获取图片的完整路径
        /// </summary>
        /// <param name="RelativePath">相对路径</param>
        /// <returns>绝对路径</returns>
        public static string GetImageFullPathFromXmlRelativePath(string RelativePath)
        {
            if (string.IsNullOrWhiteSpace(RelativePath)) return string.Empty;
            // 编辑与预览只使用私有副本，与图标上传/下载的保存目录一致。
            return Infrastructure.Deployment.DeploymentFiles.Under(AppDataPaths.PackageLocalFeedProviderFolder, RelativePath);
        }
    }
}
