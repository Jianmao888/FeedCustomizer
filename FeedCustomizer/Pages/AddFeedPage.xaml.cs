using FeedCustomizer.Core.Interface;
using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AddFeedPage : Page, IWindowCloseAware
    {
        private const int MaxHtmlLength = 2 * 1024 * 1024;
        private const int MaxIconBytes = 5 * 1024 * 1024;
        private static readonly HttpClient WebsiteClient = CreateWebsiteClient();
        private FeedViewModel FeedViewModel;
        private bool _iconModeInitialized;
        private bool _suppressIconModeChange;

        public AddFeedPage()
        {
            InitializeComponent();
            FeedViewModel = new FeedViewModel();
            _iconModeInitialized = true;
            UpdateIconModeVisibility();
            UpdateImagePreviews();
        }

        /// <summary>
        /// Called when the Page is navigated to.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is FeedViewModel feedViewModel)
            {
                FeedViewModel = feedViewModel;

                _suppressIconModeChange = true;
                bool usesWebsiteIcon = Path.GetFileName(feedViewModel.ImagePath)
                    .StartsWith("web-", StringComparison.OrdinalIgnoreCase);
                WebsiteIconRadioButton.IsChecked = usesWebsiteIcon;
                CustomIconRadioButton.IsChecked = !usesWebsiteIcon;
                UpdateImagePreviews(feedViewModel.BitmapImage);
                UpdateIconModeVisibility();
                _suppressIconModeChange = false;
            }
        }

        private void OnIconModeChanged(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            UpdateIconModeVisibility();

            if (_iconModeInitialized && !_suppressIconModeChange)
            {
                FeedViewModel.MarkIconModeChanged();
            }
        }

        private void UpdateIconModeVisibility()
        {
            if (WebsiteImageControls is null ||
                CustomImageControls is null ||
                WebsiteIconRadioButton is null ||
                CustomIconRadioButton is null)
            {
                return;
            }

            WebsiteImageControls.Visibility = WebsiteIconRadioButton.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
            CustomImageControls.Visibility = CustomIconRadioButton.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void OnSelectImageButtonClick(object sender, RoutedEventArgs e)
        {
            // 标记参数为已使用以消除 IDE0060 警告
            _ = sender;
            _ = e;

            if (await FeedViewModel.SelectImage() is BitmapImage bitmapImage)
            {
                UpdateImagePreviews(bitmapImage);
            }
        }

        private void OnClearImageButtonClick(object sender, RoutedEventArgs e)
        {
            // 标记参数为已使用以消除 IDE0060 警告
            _ = sender;
            _ = e;

            FeedViewModel.ClearImage();
            UpdateImagePreviews();
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);

            // 撤销更改
            FeedViewModel.CancelChanges();
        }

        private async void OnSaveButtonClick(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            if (App.MainWindow is not MainWindow window)
            {
                return;
            }

            SaveButton.IsEnabled = false;
            Uri? websiteUri = null;
            bool overlayVisible = false;
            try
            {
                websiteUri = ManifestXmlService.NormalizeHttpUri(FeedViewModel.Url);
                FeedViewModel.Url = websiteUri.AbsoluteUri;

                if (WebsiteIconRadioButton.IsChecked == true)
                {
                    window.SetLoadingOverlayVisible(true);
                    overlayVisible = true;
                    WebsiteIconResult iconResult = await DownloadWebsiteIconAsync(websiteUri);
                    websiteUri = iconResult.PageUri;
                    FeedViewModel.Url = websiteUri.AbsoluteUri;
                    FeedViewModel.SetDownloadedImage(iconResult.RelativeIconPath);
                    BitmapImage downloadedImage = await ImageHelper.LoadImageFromPathAsync(
                        ImageHelper.GetImageFullPathFromXmlRelativePath(iconResult.RelativeIconPath))
                        ?? throw new InvalidDataException("网页图标已保存，但无法加载预览。");
                    UpdateImagePreviews(downloadedImage);
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                FeedViewModel.CommitChanges();
                if (overlayVisible)
                {
                    window.SetLoadingOverlayVisible(false);
                    overlayVisible = false;
                }
                Frame.GoBack();
            }
            catch (Exception ex)
            {
                if (overlayVisible)
                {
                    window.SetLoadingOverlayVisible(false);
                    overlayVisible = false;
                }
                SaveButton.IsEnabled = FeedViewModel.CanSave.Value;
                string details = string.Join(
                    Environment.NewLine,
                    $"URL: {websiteUri?.AbsoluteUri ?? FeedViewModel.Url}",
                    $"Exception: {ex.GetType().FullName}",
                    $"Message: {ex.Message}",
                    string.Empty,
                    ex.ToString());
                await window.ShowWebIconFetchErrorDialogAsync(details);
            }
            finally
            {
                if (overlayVisible)
                {
                    window.SetLoadingOverlayVisible(false);
                }
            }
        }

        private static HttpClient CreateWebsiteClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) FeedCustomizer/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd(
                "text/html,application/xhtml+xml,image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
            return client;
        }

        private void UpdateImagePreviews(BitmapImage? bitmapImage = null)
        {
            bool usesDefaultImage = string.Equals(
                Path.GetFileName(FeedViewModel.ImagePath),
                Constants.DefaultImageName,
                StringComparison.OrdinalIgnoreCase);
            Visibility placeholderVisibility = usesDefaultImage
                ? Visibility.Visible
                : Visibility.Collapsed;
            Visibility imageVisibility = usesDefaultImage
                ? Visibility.Collapsed
                : Visibility.Visible;

            WebsiteDefaultIcon.Visibility = placeholderVisibility;
            CustomDefaultIcon.Visibility = placeholderVisibility;
            WebsitePreviewImage.Visibility = imageVisibility;
            PreviewImage.Visibility = imageVisibility;

            if (!usesDefaultImage)
            {
                bitmapImage ??= FeedViewModel.BitmapImage;
                WebsitePreviewImage.Source = bitmapImage;
                PreviewImage.Source = bitmapImage;
            }
        }

        private static async Task<WebsiteIconResult> DownloadWebsiteIconAsync(Uri requestedUri)
        {
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, requestedUri);
            using HttpResponseMessage pageResponse = await WebsiteClient.SendAsync(
                pageRequest,
                HttpCompletionOption.ResponseHeadersRead);
            pageResponse.EnsureSuccessStatusCode();

            if (pageResponse.Content.Headers.ContentLength > MaxHtmlLength)
            {
                throw new InvalidDataException("网页内容超过 2 MB，已停止解析图标。");
            }

            string html = await pageResponse.Content.ReadAsStringAsync();
            if (html.Length > MaxHtmlLength)
            {
                throw new InvalidDataException("网页内容超过 2 MB，已停止解析图标。");
            }

            Uri pageUri = pageResponse.RequestMessage?.RequestUri ?? requestedUri;
            List<Uri> candidates = GetIconCandidates(html, pageUri);
            var errors = new List<string>();

            foreach (Uri candidate in candidates.Take(12))
            {
                try
                {
                    byte[] iconBytes = await DownloadIconBytesAsync(candidate);
                    string relativeIconPath = await SaveIconAsPngAsync(iconBytes);
                    return new WebsiteIconResult(relativeIconPath, pageUri);
                }
                catch (Exception ex)
                {
                    errors.Add($"{candidate}: {ex.Message}");
                }
            }

            throw new InvalidDataException(
                "网页没有可用的图标。" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        private static List<Uri> GetIconCandidates(string html, Uri pageUri)
        {
            var candidates = new List<Uri>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match tagMatch in LinkTagRegex().Matches(html))
            {
                var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match attributeMatch in HtmlAttributeRegex().Matches(tagMatch.Value))
                {
                    string value = attributeMatch.Groups["double"].Success
                        ? attributeMatch.Groups["double"].Value
                        : attributeMatch.Groups["single"].Success
                            ? attributeMatch.Groups["single"].Value
                            : attributeMatch.Groups["bare"].Value;
                    attributes[attributeMatch.Groups["name"].Value] = WebUtility.HtmlDecode(value);
                }

                if (!attributes.TryGetValue("rel", out string? rel) ||
                    !rel.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(value => value.Contains("icon", StringComparison.OrdinalIgnoreCase)) ||
                    !attributes.TryGetValue("href", out string? href) ||
                    href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(pageUri, href, out Uri? iconUri) ||
                    (iconUri.Scheme != Uri.UriSchemeHttp && iconUri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                if (seen.Add(iconUri.AbsoluteUri))
                {
                    candidates.Add(iconUri);
                }
            }

            var fallback = new Uri(pageUri, "/favicon.ico");
            if (seen.Add(fallback.AbsoluteUri))
            {
                candidates.Add(fallback);
            }

            return candidates;
        }

        private static async Task<byte[]> DownloadIconBytesAsync(Uri iconUri)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, iconUri);
            using HttpResponseMessage response = await WebsiteClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxIconBytes)
            {
                throw new InvalidDataException("图标文件超过 5 MB。 ");
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0 || bytes.Length > MaxIconBytes)
            {
                throw new InvalidDataException("图标文件为空或超过 5 MB。");
            }

            return bytes;
        }

        private static async Task<string> SaveIconAsPngAsync(byte[] iconBytes)
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(iconBytes.AsBuffer());
            input.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
            uint sourceWidth = decoder.OrientedPixelWidth;
            uint sourceHeight = decoder.OrientedPixelHeight;
            if (sourceWidth == 0 || sourceHeight == 0)
            {
                throw new InvalidDataException("网页图标尺寸无效。");
            }
            if (sourceWidth > 16384 || sourceHeight > 16384)
            {
                throw new InvalidDataException("网页图标尺寸超过安全限制。");
            }

            double scale = Math.Min(1d, 256d / Math.Max(sourceWidth, sourceHeight));
            uint targetWidth = (uint)Math.Max(1, (int)Math.Round(sourceWidth * scale));
            uint targetHeight = (uint)Math.Max(1, (int)Math.Round(sourceHeight * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight
            };
            PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            Directory.CreateDirectory(AppDataPaths.ImagesFolder);
            string fileName = $"web-{Guid.NewGuid():N}.png";
            string fullPath = Path.Combine(AppDataPaths.ImagesFolder, fileName);
            try
            {
                using FileStream fileStream = File.Create(fullPath);
                using IRandomAccessStream output = fileStream.AsRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    targetWidth,
                    targetHeight,
                    decoder.DpiX > 0 ? decoder.DpiX : 96,
                    decoder.DpiY > 0 ? decoder.DpiY : 96,
                    pixelData.DetachPixelData());
                await encoder.FlushAsync();
            }
            catch
            {
                ImageHelper.DeleteImage(fullPath);
                throw;
            }

            return $"Images\\{fileName}";
        }

        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            // 标记参数为已使用以消除 IDE0060 警告
            _ = sender;
            _ = e;

            Frame.GoBack();
        }

        public void OnWindowClosing()
        {
            // 撤销更改
            FeedViewModel.CancelChanges();
        }

        [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex LinkTagRegex();

        [GeneratedRegex(@"(?<name>[\w:-]+)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex HtmlAttributeRegex();

        private sealed record WebsiteIconResult(string RelativeIconPath, Uri PageUri);
    }
}
