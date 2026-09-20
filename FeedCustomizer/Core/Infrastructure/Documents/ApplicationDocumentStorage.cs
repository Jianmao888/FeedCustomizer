using FeedCustomizer.Core.Documents;
using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.ApplicationModel;

namespace FeedCustomizer.Core.Infrastructure.Documents;

/// <summary>
/// 应用文档的磁盘实现。安装目录只读，私有 Documents 目录是运行时唯一副本；
/// Provider 工作目录与真实注册目录中的 HelpDoc 仅作为升级遗留产物清理，绝不作为文档来源。
/// </summary>
internal sealed class ApplicationDocumentStorage : IApplicationDocumentStorage
{
    private const string StateElement = "ApplicationDocuments";
    private readonly string _sourceRoot;
    private readonly string _targetRoot;
    private readonly string _statePath;
    private readonly string _legacyPrivateHelpPath;
    private readonly LegacyDocumentPowerShellAdapter _legacyPowerShell;

    internal ApplicationDocumentStorage(
        string sourceRoot,
        string targetRoot,
        string statePath,
        string legacyPrivateHelpPath,
        string currentPackageVersion,
        LegacyDocumentPowerShellAdapter legacyPowerShell)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot);
        _targetRoot = Path.GetFullPath(targetRoot);
        _statePath = Path.GetFullPath(statePath);
        _legacyPrivateHelpPath = Path.GetFullPath(legacyPrivateHelpPath);
        CurrentPackageVersion = currentPackageVersion;
        _legacyPowerShell = legacyPowerShell;

        // 所有可写目录必须位于应用既有的私有数据根下，构造阶段一次验证，后续方法只使用固定路径。
        EnsureStrictChild(AppDataPaths.PackageLocalBase, _targetRoot);
        EnsureStrictChild(AppDataPaths.PackageLocalBase, _statePath);
        EnsureStrictChild(AppDataPaths.PackageLocalBase, _legacyPrivateHelpPath);
    }

    internal static ApplicationDocumentStorage CreateDefault(
        LegacyDocumentPowerShellAdapter legacyPowerShell)
    {
        string packageRoot;
        string packageVersion;
        try
        {
            packageRoot = Package.Current.InstalledLocation.Path;
            var version = Package.Current.Id.Version;
            packageVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // 未打包调试仍沿用输出目录；正式 MSIX 始终使用不可变安装位置。
            packageRoot = AppContext.BaseDirectory;
            packageVersion = "unpackaged";
        }

        return new ApplicationDocumentStorage(
            Path.Combine(packageRoot, "Documents"),
            AppDataPaths.PackageLocalDocumentsFolder,
            AppDataPaths.PackageLocalDocumentsStatePath,
            AppDataPaths.LegacyPackageLocalHelpDocFolder,
            packageVersion,
            legacyPowerShell);
    }

    public string CurrentPackageVersion { get; }

    public ApplicationDocumentState? ReadState()
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        try
        {
            XElement root = XDocument.Load(_statePath).Root
                ?? throw new InvalidDataException("应用文档状态缺少根节点。");
            if (!string.Equals(root.Name.LocalName, StateElement, StringComparison.Ordinal))
            {
                throw new InvalidDataException("应用文档状态根节点无效。");
            }

            return new ApplicationDocumentState(
                (string?)root.Attribute("PackageVersion") ?? string.Empty,
                (int?)root.Attribute("CatalogSchema") ?? 0,
                (string?)root.Attribute("LegacyCleanupAttemptedVersion") ?? string.Empty,
                (bool?)root.Attribute("LegacyCleanupCompleted") ?? false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException or FormatException or OverflowException)
        {
            // 状态损坏时从不可变包源重建；不能把损坏状态伪装成当前版本。
            throw new InvalidDataException("应用文档版本状态无法读取。", ex);
        }
    }

    public void WriteState(ApplicationDocumentState state)
    {
        string directory = Path.GetDirectoryName(_statePath)
            ?? throw new InvalidDataException("应用文档状态路径无父目录。");
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory);

        string temporary = _statePath + ".tmp";
        try
        {
            var document = new XDocument(
                new XElement(
                    StateElement,
                    new XAttribute("PackageVersion", state.PackageVersion),
                    new XAttribute("CatalogSchema", state.CatalogSchema),
                    new XAttribute("LegacyCleanupAttemptedVersion", state.LegacyCleanupAttemptedVersion),
                    new XAttribute("LegacyCleanupCompleted", state.LegacyCleanupCompleted)));
            document.Save(temporary);
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public bool LegacyPrivateHelpExists()
    {
        // 这里只做不启动外部进程的升级识别。真正删除前会递归拒绝重解析点；
        // 即使旧目录异常，也不能阻止新文档先从可信安装源完成同步。
        return Directory.Exists(_legacyPrivateHelpPath);
    }

    public async Task ReplaceCatalogAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sourceRoot))
        {
            throw new DirectoryNotFoundException($"安装包缺少文档目录：{_sourceRoot}");
        }

        RejectReparsePoint(_sourceRoot);
        string parent = Path.GetDirectoryName(_targetRoot)
            ?? throw new InvalidDataException("应用文档目录没有父目录。");
        Directory.CreateDirectory(parent);
        RejectReparsePoint(parent);

        // 候选和上个目录都是固定的应用专用兄弟目录，不接受外部路径输入。
        string candidate = _targetRoot + ".candidate";
        string previous = _targetRoot + ".previous";
        DeleteOwnedDirectoryIfExists(candidate);
        DeleteOwnedDirectoryIfExists(previous);
        Directory.CreateDirectory(candidate);

        try
        {
            await CopyTreeAsync(_sourceRoot, candidate, cancellationToken);
            ValidateCandidate(candidate);

            // 候选已完整构建并验证后才切换。若新目录移动失败，立即恢复旧目录，
            // 避免因更新中断而把唯一可用文档先清空。
            if (Directory.Exists(_targetRoot))
            {
                RejectReparsePoint(_targetRoot);
                Directory.Move(_targetRoot, previous);
            }

            try
            {
                Directory.Move(candidate, _targetRoot);
            }
            catch
            {
                if (!Directory.Exists(_targetRoot) && Directory.Exists(previous))
                {
                    Directory.Move(previous, _targetRoot);
                }

                throw;
            }

            DeleteOwnedDirectoryIfExists(previous);
        }
        finally
        {
            DeleteOwnedDirectoryIfExists(candidate);
        }
    }

    public string? ResolveDocumentPath(ApplicationDocumentKind kind, string languageTag)
    {
        string? relative = ApplicationDocumentCatalog.GetRelativeEntry(kind, languageTag);
        if (relative is null)
        {
            return null;
        }

        string path = ResolveChild(_targetRoot, relative);
        return File.Exists(path) ? path : null;
    }

    public async Task<LegacyDocumentCleanupResult> RemoveLegacyHelpAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        bool privateRemoved = false;
        bool registeredRemoved = false;

        try
        {
            if (Directory.Exists(_legacyPrivateHelpPath))
            {
                DeleteOwnedDirectoryIfExists(_legacyPrivateHelpPath);
                privateRemoved = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"私有旧文档清理失败：{ex.Message}");
        }

        try
        {
            registeredRemoved = await _legacyPowerShell.RemoveLegacyHelpAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 旧副本不再参与打开，清理失败不能回滚已发布的新文档；诊断保留到统一日志。
            diagnostics.Add($"实际注册目录旧文档清理失败：{ex.Message}");
        }

        return new LegacyDocumentCleanupResult(privateRemoved, registeredRemoved, diagnostics);
    }

    private static async Task CopyTreeAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        foreach (string directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(directory);
            string relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(ResolveChild(destinationRoot, relative));
        }

        foreach (string source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(source);
            string relative = Path.GetRelativePath(sourceRoot, source);
            string destination = ResolveChild(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static void ValidateCandidate(string candidate)
    {
        foreach (string relative in ApplicationDocumentCatalog.RequiredEntries)
        {
            if (!File.Exists(ResolveChild(candidate, relative)))
            {
                throw new FileNotFoundException($"文档候选缺少必要文件：{relative}");
            }
        }
    }

    private void DeleteOwnedDirectoryIfExists(string path)
    {
        string fullPath = Path.GetFullPath(path);
        EnsureStrictChild(AppDataPaths.PackageLocalBase, fullPath);
        if (!Directory.Exists(fullPath))
        {
            return;
        }

        RejectReparseTree(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    private static string ResolveChild(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            throw new InvalidDataException("文档相对路径无效。");
        }

        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        EnsureStrictChild(fullRoot, path);
        return path;
    }

    private static void EnsureStrictChild(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("文档路径越出应用专用目录。");
        }
    }

    private static void RejectReparseTree(string root)
    {
        RejectReparsePoint(root);
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(path);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"文档目录不允许重解析点：{path}");
        }
    }
}
