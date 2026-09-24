using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Infrastructure.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace FeedCustomizer.Core.Infrastructure.Deployment;

/// <summary>部署文件操作的统一路径边界、原子单文件写入和版本摘要实现。</summary>
internal static class DeploymentFiles
{
    private static readonly IAppLog Log = AppLog.For(nameof(DeploymentFiles));
    internal const string VersionFile = ".deployment-version.xml";

    /// <summary>只接受根目录下的相对路径，拒绝跳出目录及重解析点，防止删除/覆盖落到其他目录。</summary>
    internal static string Under(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException($"不允许的部署相对路径：{relative}");
        string path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"部署路径越界：{relative}");
        RejectLink(fullRoot);
        string current = fullRoot;
        foreach (string segment in Path.GetRelativePath(fullRoot, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            RejectLink(current);
        }
        return path;
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"部署不允许重解析点：{path}");
    }

    /// <summary>逐级枚举而不跟随目录链接；先验证再递归，避免系统枚举跳入不受控目录。</summary>
    internal static IEnumerable<string> Enumerate(string root, string relative = ".")
    {
        string folder = relative == "." ? root : Under(root, relative);
        RejectLink(folder);
        if (!Directory.Exists(folder)) yield break;
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetRelativePath(root, file);
            _ = Under(root, name);
            yield return name;
        }
        foreach (string child in Directory.EnumerateDirectories(folder))
            foreach (string file in Enumerate(root, Path.GetRelativePath(root, child))) yield return file;
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>临时文件与目标同卷，完成写入后再替换；异常时旧文件仍然完整。</summary>
    internal static void AtomicCopy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".deployment-tmp";
        RejectLink(temporary);
        try
        {
            File.Copy(source, temporary, true);
            File.SetAttributes(temporary, FileAttributes.Normal);
            if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
            File.Move(temporary, destination, true);
        }
        finally { TryDeleteTemporary(temporary); }
    }

    internal static void SaveXml(XDocument document, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".deployment-tmp";
        RejectLink(temporary);
        try { document.Save(temporary); File.Move(temporary, path, true); }
        finally { TryDeleteTemporary(temporary); }
    }

    private static void TryDeleteTemporary(string path)
    {
        // 临时文件清理属于次要诊断，不能遮蔽最初的复制/替换错误；下次原路径写入会覆盖它。
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "清理部署临时文件失败");
        }
    }

    /// <summary>仅清理传入的应用专用事务子目录；逐个验证文件，不使用递归删除跟随链接。</summary>
    internal static void Clear(string root)
    {
        RejectLink(root);
        if (!Directory.Exists(root)) return;
        foreach (string file in Enumerate(root).ToArray())
        {
            string path = Under(root, file);
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        DeleteEmptyDirectories(root);
    }

    private static void DeleteEmptyDirectories(string root)
    {
        foreach (string child in Directory.EnumerateDirectories(root))
        { RejectLink(child); DeleteEmptyDirectories(child); }
        Directory.Delete(root);
    }
}

/// <summary>
/// 统一版本清单：模板身份、全部受管理文件的相对路径和 SHA-256。
/// 哈希集中在这里；相同 MSIX 版本的开发重建也能被识别，不再依赖 first_run flag。
/// </summary>
internal sealed record DeploymentVersion(string Template, Dictionary<string, string> Files)
{
    internal static DeploymentVersion Capture(string root, string template, IEnumerable<string> paths) =>
        new(template, paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, p => DeploymentFiles.Hash(DeploymentFiles.Under(root, p)), StringComparer.OrdinalIgnoreCase));

    internal void Save(string root) => DeploymentFiles.SaveXml(new XDocument(new XElement("DeploymentVersion",
        new XAttribute("Schema", 1), new XAttribute("Template", Template),
        Files.Select(f => new XElement("File", new XAttribute("Path", f.Key), new XAttribute("Sha256", f.Value))))),
        DeploymentFiles.Under(root, DeploymentFiles.VersionFile));

    internal static DeploymentVersion? Read(string root)
    {
        string path = DeploymentFiles.Under(root, DeploymentFiles.VersionFile);
        if (!File.Exists(path)) return null;
        var element = XDocument.Load(path).Root ?? throw new InvalidDataException("版本清单为空。");
        if ((int?)element.Attribute("Schema") != 1) throw new InvalidDataException("不支持的部署清单版本。");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in element.Elements("File"))
        {
            string name = (string?)file.Attribute("Path") ?? throw new InvalidDataException("缺少文件路径。");
            _ = DeploymentFiles.Under(root, name);
            if (!files.TryAdd(name, (string?)file.Attribute("Sha256") ?? throw new InvalidDataException("缺少摘要。")))
                throw new InvalidDataException($"版本清单包含重复路径：{name}");
        }
        return new((string?)element.Attribute("Template") ?? string.Empty, files);
    }

    internal bool Matches(string root, bool includeManifest = true) => Files.All(f =>
        (!includeManifest && f.Key.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)) ||
        (File.Exists(DeploymentFiles.Under(root, f.Key)) && DeploymentFiles.Hash(DeploymentFiles.Under(root, f.Key)) == f.Value));

    /// <summary>
    /// 只有模板一致且程序文件完整时才能复用注册副本；旧图片可留待引用清理，
    /// 但不允许程序或静态资源的任何差异进入仅配置发布。
    /// </summary>
    internal ProviderDeploymentPlan Plan(DeployedFileInspection inspection)
    {
        string[] allPaths = Files.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!inspection.MetadataValid || !inspection.TemplateMatches || inspection.UnexpectedRuntime)
        {
            return new(ProviderDeploymentScope.Full, allPaths);
        }

        var changed = new HashSet<string>(inspection.MetadataChanged, StringComparer.OrdinalIgnoreCase);
        changed.UnionWith(inspection.ContentChanged);
        if (changed.Count == 0)
        {
            return new(ProviderDeploymentScope.Current, []);
        }

        if (!changed.All(IsConfigurationPath))
        {
            return new(ProviderDeploymentScope.Full, allPaths);
        }

        // 从预期清单中移除的旧图片没有复制源；它只改变版本元数据，稍后由图片清理回收。
        string[] copyPaths = inspection.ContentChanged
            .Where(Files.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(ProviderDeploymentScope.Configuration, copyPaths);
    }

    internal static bool IsConfigurationPath(string path)
    {
        if (path.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!path.StartsWith("Images\\", StringComparison.OrdinalIgnoreCase) || path.Contains(':') ||
            path.Contains('/') ||
            Path.IsPathRooted(path))
        {
            return false;
        }

        return path.Split('\\').All(segment =>
            !string.IsNullOrWhiteSpace(segment) && segment is not "." and not "..");
    }
}
