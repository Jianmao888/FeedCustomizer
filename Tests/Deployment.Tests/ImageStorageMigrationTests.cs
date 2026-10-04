using FeedCustomizer.Core.Infrastructure.Images;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Infrastructure.Storage;
using Windows.Storage;

/// <summary>在独立临时目录验证图片存储迁移的文件内容、路径兼容和删除边界。</summary>
internal static class ImageStorageMigrationTests
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> Cases { get; } =
    [
        ("图片导入保留原字节、扩展名及带连字符GUID名称", ImportPreservesContentAsync),
        ("图片文件读取保持缺失空值及流所有权", OpenReadPreservesBehaviorAsync),
        ("图片相对路径保留私有Provider根及既有越界校验", RelativePathsStayCompatibleAsync),
        ("图片失败清理保护默认图并拒绝目录外文件", DeleteIsBoundedAsync)
    ];

    private static async Task ImportPreservesContentAsync()
    {
        using var scope = new ImageTestScope();
        string source = Path.Combine(scope.Root, "selected.PNG");
        byte[] expected = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(source, expected);

        string fileName = await ImageFileStorage.SavePickedImageAsync(new StorageFile(source));
        Require(Path.GetExtension(fileName) == ".PNG");
        Require(Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "D", out _));
        string saved = Path.Combine(AppDataPaths.ImagesFolder, fileName);
        Require(File.ReadAllBytes(saved).SequenceEqual(expected));

        // 复制结束后两端均须释放，避免预览或部署随后因残留占用失败。
        using var reopenedSource = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.None);
        using var reopenedSaved = File.Open(saved, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    private static Task OpenReadPreservesBehaviorAsync()
    {
        using var scope = new ImageTestScope();
        Require(ImageFileStorage.OpenRead(string.Empty) is null);
        Require(ImageFileStorage.OpenRead(Path.Combine(scope.Root, "missing.png")) is null);

        string file = Path.Combine(scope.Root, "preview.png");
        File.WriteAllText(file, "preview-content");
        using (Stream stream = ImageFileStorage.OpenRead(file)
            ?? throw new InvalidOperationException("已有图片无法打开。"))
        using (var reader = new StreamReader(stream))
        {
            Require(reader.ReadToEnd() == "preview-content");
        }

        using var reopened = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
        return Task.CompletedTask;
    }

    private static Task RelativePathsStayCompatibleAsync()
    {
        using var scope = new ImageTestScope();
        Require(ImageFileStorage.GetImageFolderPath() == AppDataPaths.ImagesFolder);
        Require(ImageFileStorage.GetImageFullPathFromXmlRelativePath(" ") == string.Empty);
        Require(ImageFileStorage.GetImageFullPathFromXmlRelativePath(@"Images\custom.png") ==
            Path.Combine(AppDataPaths.ImagesFolder, "custom.png"));
        Require(ImageFileStorage.GetImageFullPathFromXmlRelativePath(@"Assets\logo.png") ==
            Path.Combine(AppDataPaths.PackageLocalFeedProviderFolder, "Assets", "logo.png"));

        try
        {
            ImageFileStorage.GetImageFullPathFromXmlRelativePath(@"..\outside.png");
        }
        catch (InvalidDataException)
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException("图片相对路径越界未被拒绝。");
    }

    private static Task DeleteIsBoundedAsync()
    {
        using var scope = new ImageTestScope();
        string defaultImage = Path.Combine(AppDataPaths.ImagesFolder, "Default.png");
        string downloadedImage = Path.Combine(AppDataPaths.ImagesFolder, "web-test.png");
        string outsideImage = Path.Combine(scope.Root, "outside.png");
        File.WriteAllText(defaultImage, "default");
        File.WriteAllText(downloadedImage, "download");
        File.WriteAllText(outsideImage, "outside");

        ImageFileStorage.DeleteImage(defaultImage);
        ImageFileStorage.DeleteImage(downloadedImage);
        ImageFileStorage.DeleteImage(downloadedImage);
        Require(File.Exists(defaultImage) && !File.Exists(downloadedImage));

        AppLog.Clear();
        ImageFileStorage.DeleteImage(outsideImage);
        Require(File.Exists(outsideImage));
        Require(AppLog.Events.Any(entry => entry.Level == "Warning"));
        return Task.CompletedTask;
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("图片存储迁移验证失败。");
        }
    }

    /// <summary>复用既有测试目录并恢复路径替身，所有文件副作用均限制在本次目录内。</summary>
    private sealed class ImageTestScope : IDisposable
    {
        private readonly TestDirectory _directory = new();
        private readonly string _previousRoot = AppDataPaths.TestRoot;

        internal string Root => _directory.Path;

        internal ImageTestScope()
        {
            AppDataPaths.TestRoot = Root;
            Directory.CreateDirectory(AppDataPaths.ImagesFolder);
        }

        /// <summary>恢复共享路径替身后，清理本次拥有的临时目录。</summary>
        public void Dispose()
        {
            AppDataPaths.TestRoot = _previousRoot;
            _directory.Dispose();
        }
    }
}
