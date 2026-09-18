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

// 部署回归测试只验证业务顺序与文件副作用，不把桌面应用的 Serilog 生命周期带入纯逻辑测试。
namespace FeedCustomizer.Core.Infrastructure.Logging
{
    internal interface IAppLog
    {
        void Debug(string messageTemplate, params object?[] propertyValues);
        void Information(string messageTemplate, params object?[] propertyValues);
        void Warning(string messageTemplate, params object?[] propertyValues);
        void Warning(Exception exception, string messageTemplate, params object?[] propertyValues);
        void Error(string messageTemplate, params object?[] propertyValues);
        void Error(Exception exception, string messageTemplate, params object?[] propertyValues);
        void Fatal(Exception exception, string messageTemplate, params object?[] propertyValues);
    }

    internal static class AppLog
    {
        private static readonly IAppLog NullLogger = new NullAppLog();

        internal static IAppLog For<T>()
        {
            return NullLogger;
        }

        internal static IAppLog For(string sourceContext)
        {
            return NullLogger;
        }

        private sealed class NullAppLog : IAppLog
        {
            public void Debug(string messageTemplate, params object?[] propertyValues)
            {
            }

            public void Information(string messageTemplate, params object?[] propertyValues)
            {
            }

            public void Warning(string messageTemplate, params object?[] propertyValues)
            {
            }

            public void Warning(Exception exception, string messageTemplate, params object?[] propertyValues)
            {
            }

            public void Error(string messageTemplate, params object?[] propertyValues)
            {
            }

            public void Error(Exception exception, string messageTemplate, params object?[] propertyValues)
            {
            }

            public void Fatal(Exception exception, string messageTemplate, params object?[] propertyValues)
            {
            }
        }
    }
}
