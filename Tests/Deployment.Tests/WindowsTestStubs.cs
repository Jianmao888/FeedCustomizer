// 测试仅替换包路径、包版本和显示名称来源，XML 读写/规范化和部署存储均链接真实实现。
// 这些类型只在独立测试项目编译，不进入 WinUI 应用。
namespace FeedCustomizer.Core.Tools
{
    internal static class AppDataPaths
    {
        internal static string TestRoot { get; set; } = string.Empty;
        internal static string PackageLocalFeedProviderFolder => Path.Combine(TestRoot, "private", "Local", "FeedCustomProvider");
        internal static string PackageLocalBase => Path.Combine(TestRoot, "private");
        internal static string PackageLocalDocumentsFolder => Path.Combine(PackageLocalBase, "Local", "Documents");
        internal static string PackageLocalDocumentsStatePath => Path.Combine(PackageLocalBase, "Local", ".application-documents.xml");
        internal static string LegacyPackageLocalHelpDocFolder => Path.Combine(PackageLocalFeedProviderFolder, "HelpDoc");
        internal static string PackageLocalManifestPath => Path.Combine(PackageLocalFeedProviderFolder, "AppxManifest.xml");
        internal static string PackageLocalLogPath => Path.Combine(TestRoot, "private", "Local", "Logs");
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

// 测试记录调用方提交给日志门面的结构化事件，不把桌面应用的 Serilog 文件生命周期带入纯逻辑测试。
namespace FeedCustomizer.Core.Infrastructure.Logging
{
    internal sealed record TestLogEvent(
        string Level,
        string SourceContext,
        string MessageTemplate,
        object?[] PropertyValues);

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
        private static readonly System.Collections.Concurrent.ConcurrentQueue<TestLogEvent> RecordedEvents = new();

        internal static IReadOnlyCollection<TestLogEvent> Events => RecordedEvents.ToArray();

        internal static IAppLog For<T>()
        {
            return new TestAppLog(typeof(T).FullName ?? typeof(T).Name);
        }

        internal static IAppLog For(string sourceContext)
        {
            return new TestAppLog(sourceContext);
        }

        internal static void Clear()
        {
            while (RecordedEvents.TryDequeue(out _))
            {
            }
        }

        private sealed class TestAppLog(string sourceContext) : IAppLog
        {
            public void Debug(string messageTemplate, params object?[] propertyValues)
            {
                Record("Debug", messageTemplate, propertyValues);
            }

            public void Information(string messageTemplate, params object?[] propertyValues)
            {
                Record("Information", messageTemplate, propertyValues);
            }

            public void Warning(string messageTemplate, params object?[] propertyValues)
            {
                Record("Warning", messageTemplate, propertyValues);
            }

            public void Warning(Exception exception, string messageTemplate, params object?[] propertyValues)
            {
                Record("Warning", messageTemplate, propertyValues);
            }

            public void Error(string messageTemplate, params object?[] propertyValues)
            {
                Record("Error", messageTemplate, propertyValues);
            }

            public void Error(Exception exception, string messageTemplate, params object?[] propertyValues)
            {
                Record("Error", messageTemplate, propertyValues);
            }

            public void Fatal(Exception exception, string messageTemplate, params object?[] propertyValues)
            {
                Record("Fatal", messageTemplate, propertyValues);
            }

            private void Record(string level, string messageTemplate, object?[] propertyValues)
            {
                RecordedEvents.Enqueue(new TestLogEvent(
                    level,
                    sourceContext,
                    messageTemplate,
                    [.. propertyValues]));
            }
        }
    }
}
