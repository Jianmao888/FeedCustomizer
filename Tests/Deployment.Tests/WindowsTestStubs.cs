// 替身提供包路径、包版本、显示名称、日志记录和可控 UI 调度；应用规则与文件操作均链接真实实现。
// 这些类型只在独立测试项目编译，不进入 WinUI 应用，也不创建真实控件。
namespace Microsoft.UI.Dispatching
{
    /// <summary>显式控制派发时机的最小调度器替身，不创建真实 UI 线程或消息循环。</summary>
    public sealed class DispatcherQueue
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _actions = new();

        /// <summary>由测试控制线程访问状态，用于选择直接执行或排队执行。</summary>
        public bool HasThreadAccess { get; set; }

        internal bool AcceptEnqueue { get; set; } = true;

        internal int PendingCount => _actions.Count;

        /// <summary>保存待执行回调，测试通过显式推进模拟排队与取消之间的时序。</summary>
        public bool TryEnqueue(Action action)
        {
            if (!AcceptEnqueue)
            {
                return false;
            }

            _actions.Enqueue(action);
            return true;
        }

        internal void RunNext()
        {
            if (!_actions.TryDequeue(out Action? action))
            {
                throw new InvalidOperationException("调度器替身没有待执行的回调。");
            }

            bool previousThreadAccess = HasThreadAccess;
            HasThreadAccess = true;
            try
            {
                action();
            }
            finally
            {
                HasThreadAccess = previousThreadAccess;
            }
        }
    }
}

namespace FeedCustomizer.Core.Infrastructure.Storage
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
        internal static string ImagesFolder => Path.Combine(PackageLocalFeedProviderFolder, "Images");
    }
}

namespace Windows.Storage
{
    /// <summary>只将测试选定的文件交给真实复制实现，不调用文件选择器或真实 WinRT 存储。</summary>
    internal sealed class StorageFile(string path)
    {
        public string Name => System.IO.Path.GetFileName(path);

        /// <summary>提供由复制实现释放的源流，保留选择文件后的流所有权。</summary>
        public Task<TestStorageReadStream> OpenReadAsync()
        {
            return Task.FromResult(new TestStorageReadStream(File.OpenRead(path)));
        }
    }

    /// <summary>模拟 WinRT 源流到 .NET 流的转换，只包装测试文件。</summary>
    internal sealed class TestStorageReadStream(Stream stream) : IDisposable
    {
        /// <summary>借出已有流；生命周期由源流包装统一管理。</summary>
        public Stream AsStream()
        {
            return stream;
        }

        /// <summary>释放源流，使测试能够观察复制结束后的文件占用。</summary>
        public void Dispose()
        {
            stream.Dispose();
        }
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
