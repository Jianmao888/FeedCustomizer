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
        // 该类负责从网页下载网站图标（favicon / <link rel="icon"> 指定的图标），并按原始格式保存。

        // 最大允许解析的 HTML 字节长度（2 MB）
        private const int MaxHtmlLength = 2 * 1024 * 1024;
        // 最大允许的图标文件大小（5 MB）
        private const int MaxIconBytes = 5 * 1024 * 1024;
        // 复用的 HttpClient 实例，使用 CreateWebsiteClient 进行统一配置
        private static readonly HttpClient WebsiteClient = CreateWebsiteClient();

        // DownloadAsync: 主入口，给定页面 URI，返回下载并按原始格式保存后的相对路径与页面最终 URI。
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
                    string relativeIconPath = await SaveIconAsync(iconBytes);
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

        // CreateWebsiteClient: 创建并配置 HttpClient 用于请求网页与图标。
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

        // GetIconCandidates: 从页面 HTML 中提取可能的图标 URL 列表。
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

        // DownloadIconBytesAsync: 以流式方式下载图标二进制，并在下载前后检查大小限制。
        // - 使用 ResponseHeadersRead 只读取头部并延迟读取主体，便于在检查 Content-Length 后决定是否继续。
        // - 如果 Content-Length 提示大于 MaxIconBytes 则提前抛出异常。
        // - 读取字节数组后再次校验实际长度，防止服务器未提供 Content-Length 或返回更大数据。
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

        // SaveIconAsync: 校验图标字节并以其原始格式保存，返回保存的相对路径。
        // 注意点：
        // - 使用 InMemoryRandomAccessStream 与 BitmapDecoder 进行解码校验，支持 ICO、PNG、JPEG、WEBP 等格式。
        // - 校验解码后宽高有效且在合理范围内（防止非常大的图像导致内存爆炸或安全问题）。
        // - 保存时直接写入原始字节，不缩放、不重新编码，因此扩展名与解码器识别出的格式保持一致。
        // - 调用方应捕获异常并记录失败原因。
        private static async Task<string> SaveIconAsync(byte[] iconBytes)
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

            string extension = GetIconFileExtension(decoder);
            Directory.CreateDirectory(AppDataPaths.ImagesFolder);
            string fileName = $"web-{Guid.NewGuid():N}{extension}";
            string fullPath = Path.Combine(AppDataPaths.ImagesFolder, fileName);
            try
            {
                await File.WriteAllBytesAsync(fullPath, iconBytes);
            }
            catch
            {
                ImageHelper.DeleteImage(fullPath);
                throw;
            }

            return $"Images\\{fileName}";
        }

        // GetIconFileExtension: 根据解码器识别出的真实格式返回对应扩展名（含前导 "."）。
        // 若解码器未提供扩展名，则回退为 .png。
        private static string GetIconFileExtension(BitmapDecoder decoder)
        {
            string? extension = decoder.DecoderInformation.FileExtensions.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return extension.StartsWith('.') ? extension : "." + extension;
            }

            return ".png";
        }

        [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex LinkTagRegex();

        [GeneratedRegex(@"(?<name>[\w:-]+)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex HtmlAttributeRegex();
    }

    public sealed record WebsiteIconResult(string RelativeIconPath, Uri PageUri);
}
