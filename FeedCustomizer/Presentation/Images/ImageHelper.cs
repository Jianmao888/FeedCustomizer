using FeedCustomizer.Core.Infrastructure.Images;
using FeedCustomizer.Core.Infrastructure.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace FeedCustomizer.Presentation.Images;

/// <summary>处理图片选择器的窗口适配与 WinUI 预览转换，文件访问交给基础设施。</summary>
public static class ImageHelper
{
    private static readonly IAppLog Log = AppLog.For(nameof(ImageHelper));

    /// <summary>在主窗口选择图片，并将所选文件交给图片存储保存。</summary>
    /// <returns>保存后的文件名；用户取消或保存失败时返回空字符串。</returns>
    public static async Task<string> PickAndSaveImageAsync()
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };

            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".ico");

            // WinUI 3 文件选择器必须绑定主窗口，不能将此窗口上下文移入文件存储。
            var mainWindow = App.MainWindow;
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return string.Empty;
            }

            return await ImageFileStorage.SavePickedImageAsync(file);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "选择并保存图片失败");
            return string.Empty;
        }
    }

    /// <summary>将存储打开的图片流转换为 WinUI 预览；文件缺失或转换失败时返回空值。</summary>
    public static async Task<BitmapImage?> LoadImageFromPathAsync(string imagePath)
    {
        try
        {
            using Stream? sourceStream = ImageFileStorage.OpenRead(imagePath);
            if (sourceStream is null)
            {
                return null;
            }

            var bitmapImage = new BitmapImage();
            // 源流由本次预览拥有，必须保持到异步转换完成，不能在存储返回前关闭。
            using (var stream = sourceStream.AsRandomAccessStream())
            {
                await bitmapImage.SetSourceAsync(stream);
            }

            return bitmapImage;
        }
        catch (Exception ex)
        {
            // 保持预览失败返回空值；诊断只记录错误类型与码，不复制外部图片路径。
            Log.Warning(
                "加载图片预览失败，异常类型={ExceptionType}，HRESULT={ErrorCode}",
                ex.GetType().FullName,
                $"0x{ex.HResult:X8}");
            return null;
        }
    }
}
