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

namespace FeedCustomizer.Core.Tools
{
    public static partial class WebsiteIconDownloader
    {
        private const int MaxHtmlLength = 2 * 1024 * 1024;
        private const int MaxIconBytes = 5 * 1024 * 1024;
        private static readonly HttpClient WebsiteClient = CreateWebsiteClient();

        public static async Task<WebsiteIconResult> DownloadAsync(Uri requestedUri)
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

        [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex LinkTagRegex();

        [GeneratedRegex(@"(?<name>[\w:-]+)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex HtmlAttributeRegex();
    }

    public sealed record WebsiteIconResult(string RelativeIconPath, Uri PageUri);
}
