// 测试仅替换包路径、包版本和显示名称来源，XML 读写/规范化和部署存储均链接真实实现。
// 这些类型只在独立测试项目编译，不进入 WinUI 应用。
namespace FeedCustomizer.Core.Tools
{
    internal static class AppDataPaths
    {
        internal static string TestRoot { get; set; } = string.Empty;
        internal static string PackageLocalFeedProviderFolder => Path.Combine(TestRoot, "private", "Local", "FeedCustomProvider");
        internal static string PackageLocalManifestPath => Path.Combine(PackageLocalFeedProviderFolder, "AppxManifest.xml");
        internal static string FeedProviderFolder => Path.Combine(TestRoot, "real", "FeedCustomProvider");
        internal static string ManifestPath => Path.Combine(FeedProviderFolder, "AppxManifest.xml");
    }
}

namespace Windows.ApplicationModel
{
    internal sealed class Package
    {
        internal static Package Current { get; } = new();
        internal Location InstalledLocation { get; } = new();
        internal Identity Id { get; } = new();
        internal string PublisherDisplayName => "测试发布者";
    }
    internal sealed class Location { internal string Path { get; set; } = string.Empty; }
    internal sealed class Identity { internal Version Version { get; } = new(1, 0, 0, 0); }
}

namespace Microsoft.Windows.ApplicationModel.Resources
{
    internal sealed class ResourceLoader
    {
        internal string GetString(string key) => "测试提供程序";
    }
}
