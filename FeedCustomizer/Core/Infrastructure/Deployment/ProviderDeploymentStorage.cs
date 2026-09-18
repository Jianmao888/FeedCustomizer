using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.ApplicationModel;

namespace FeedCustomizer.Core.Infrastructure.Deployment;

/// <summary>
/// 三个目录的所有权在此集中定义：包内 Resources/Assets 是模板；包私有目录是唯一用户数据源；
/// 真实 LocalAppData 是可重建注册产物，只通过包外适配器访问。现有目录名称不变。
/// </summary>
internal sealed class ProviderDeploymentStorage(ProviderDeploymentPowerShellAdapter deployed) : IProviderDeploymentStorage
{
    private static readonly IAppLog Log = AppLog.For<ProviderDeploymentStorage>();
    private string Work => AppDataPaths.PackageLocalFeedProviderFolder;
    private string State => DeploymentFiles.Under(Work, ".deployment");
    private string Candidate => DeploymentFiles.Under(State, "candidate");
    private string Prepared => DeploymentFiles.Under(State, "prepared");
    private string Journal => DeploymentFiles.Under(State, "operation.xml");
    private readonly Lazy<(string Identity, Dictionary<string, string> Sources)> _template = new(CreateTemplate);

    public string ManifestPath => AppDataPaths.ManifestPath;
    public bool HasTransaction => File.Exists(Journal);
    public bool TransactionCommitted => PendingStage == DeploymentStage.Completed;
    public DeploymentStage PendingStage
    {
        get
        {
            var root = XDocument.Load(Journal).Root;
            if ((int?)root?.Attribute("Schema") != 1 ||
                !Enum.TryParse((string?)root.Attribute("Stage"), out DeploymentStage stage) || !Enum.IsDefined(stage))
                throw new InvalidDataException("部署操作日志损坏，停止自动恢复以避免猜测当前注册状态。");
            return stage;
        }
    }

    /// <summary>缺少新版清单的旧安装自然进入准备流程，不读取旧 flag，也不迁移目录。</summary>
    public Task<bool> IsWorkCurrentAsync()
    {
        try
        {
            var version = DeploymentVersion.Read(Work);
            return Task.FromResult(version is not null && version.Template == _template.Value.Identity &&
                version.Files.Count == _template.Value.Sources.Count && _template.Value.Sources.Keys.All(version.Files.ContainsKey) &&
                version.Matches(Work, includeManifest: false) &&
                ManifestXmlService.IsPresentationCurrent(AppDataPaths.PackageLocalManifestPath));
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or InvalidDataException)
        {
            // 版本元数据损坏允许重建；真正的用户清单会在 PrepareWork 中严格读取，失败则停止。
            Log.Warning(ex, "检查 Provider 部署版本失败，将重新准备工作副本");
            return Task.FromResult(false);
        }
    }

    /// <summary>完整构建新版工作候选后再发布，始终从现有私有清单读取订阅源。</summary>
    public async Task PrepareWorkAsync()
    {
        if (await IsWorkCurrentAsync()) return;
        List<Feed> feeds = File.Exists(AppDataPaths.PackageLocalManifestPath)
            ? await ManifestXmlService.Read() : [];
        // 上面的读取失败时直接退出；不以空模板覆盖损坏的用户清单，也不回读注册副本。
        DeploymentFiles.Clear(Prepared);
        foreach (var source in _template.Value.Sources)
            DeploymentFiles.AtomicCopy(source.Value, DeploymentFiles.Under(Prepared, source.Key));

        string manifest = DeploymentFiles.Under(Prepared, "AppxManifest.xml");
        await ManifestXmlService.Write(feeds, manifest);
        ManifestXmlService.SynchronizePresentation(manifest);
        ValidateRequiredFiles(Prepared);
        var version = DeploymentVersion.Capture(Prepared, _template.Value.Identity, _template.Value.Sources.Keys);
        version.Save(Prepared);

        // 工作目录不是注册目录，更新它无需停止 Provider。配置清单最后替换，旧版 flag 完全不参与判断。
        foreach (string path in version.Files.Keys.Where(p => !p.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)))
            DeploymentFiles.AtomicCopy(DeploymentFiles.Under(Prepared, path), DeploymentFiles.Under(Work, path));
        DeploymentFiles.AtomicCopy(manifest, AppDataPaths.PackageLocalManifestPath);
        RemoveObsoleteRuntime(Work, version);
        DeploymentFiles.AtomicCopy(DeploymentFiles.Under(Prepared, DeploymentFiles.VersionFile), DeploymentFiles.Under(Work, DeploymentFiles.VersionFile));
        DeploymentFiles.Clear(Prepared);
    }

    /// <summary>原子保存唯一配置源；部署失败不回滚用户刚保存的订阅源。</summary>
    public async Task SaveFeedsAsync(List<Feed> feeds)
    {
        string temporary = DeploymentFiles.Under(State, "saved-manifest.xml");
        DeploymentFiles.AtomicCopy(AppDataPaths.PackageLocalManifestPath, temporary);
        await ManifestXmlService.Write(feeds, temporary);
        DeploymentFiles.AtomicCopy(temporary, AppDataPaths.PackageLocalManifestPath);
        File.Delete(temporary);
    }

    public async Task<bool> IsDeploymentCurrentAsync()
    {
        // 检查只生成小型清单，不复制整个候选。正常启动时避免重复写入 AOT 程序与全部资源。
        var paths = await GetDeploymentPathsAsync();
        DeploymentVersion.Capture(Work, _template.Value.Identity, paths).Save(State);
        return await deployed.IsCurrentAsync(DeploymentFiles.Under(State, DeploymentFiles.VersionFile));
    }

    /// <summary>候选仅包含当前模板、生成清单及其引用的图片，不把整个私有目录复制到注册目录。</summary>
    public async Task StageAsync()
    {
        var paths = await GetDeploymentPathsAsync();
        DeploymentFiles.Clear(Candidate);
        foreach (string path in paths)
            DeploymentFiles.AtomicCopy(DeploymentFiles.Under(Work, path), DeploymentFiles.Under(Candidate, path));
        ValidateRequiredFiles(Candidate);
        DeploymentVersion.Capture(Candidate, _template.Value.Identity, paths).Save(Candidate);
    }

    private async Task<HashSet<string>> GetDeploymentPathsAsync()
    {
        var paths = new HashSet<string>(_template.Value.Sources.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var feed in await ManifestXmlService.Read())
        {
            if (string.IsNullOrWhiteSpace(feed.ImagePath)) continue;
            string fullPath = DeploymentFiles.Under(Work, feed.ImagePath);
            string relative = Path.GetRelativePath(Work, fullPath);
            // 自定义图片只能引用 Images 中的文件，不能把操作日志、备份或任意私有文件发布出去。
            if (!relative.StartsWith("Images" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"不支持的订阅源图片路径：{feed.ImagePath}");
            paths.Add(relative);
        }
        return paths;
    }

    public Task BeginAsync(bool installed)
    {
        if (HasTransaction) throw new InvalidOperationException("存在未完成部署操作。");
        DeploymentFiles.SaveXml(new XDocument(new XElement("Operation", new XAttribute("Schema", 1),
            new XAttribute("PreviouslyInstalled", installed), new XAttribute("Stage", DeploymentStage.Staging))), Journal);
        return Task.CompletedTask;
    }

    public Task RecordStageAsync(DeploymentStage stage)
    {
        var document = XDocument.Load(Journal);
        document.Root!.SetAttributeValue("Stage", stage);
        DeploymentFiles.SaveXml(document, Journal);
        return Task.CompletedTask;
    }

    public Task PublishAsync() => deployed.PublishAsync(Candidate);
    public Task CommitAsync() => RecordStageAsync(DeploymentStage.Completed);

    public Task FinishAsync()
    {
        // 候选是可覆盖的临时产物；移除日志即结束操作，下次准备会重新构建候选。
        File.Delete(Journal);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 同时清理私有与真实目录。任一引用清单读取失败就跳过整轮，避免把“未知”当作“无引用”。
    /// 新文件保留一天，覆盖网页图标正在下载、编辑页尚未回传等跨页面时间窗口。
    /// </summary>
    public async Task<ImageCleanupResult> CleanImagesAsync(IReadOnlyCollection<string> draftImages, bool installed)
    {
        var diagnostics = new List<string>();
        int deleted = 0;
        try
        {
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Default.png" };
            foreach (string path in draftImages) retained.Add(Path.GetFileName(path));
            foreach (var feed in await ManifestXmlService.Read()) retained.Add(Path.GetFileName(feed.ImagePath));
            // 真实清单只参与保留引用，绝不被写回私有清单。发布失败后的旧图标也不会误删。
            // 已卸载时旧注册清单只是历史产物，不应永久阻止其图片回收。
            string registered = installed ? await deployed.ReadManifestAsync() : string.Empty;
            if (installed && string.IsNullOrWhiteSpace(registered))
                throw new FileNotFoundException("Provider 已注册但无法读取其清单，暂缓图片清理。");
            if (!string.IsNullOrWhiteSpace(registered))
                foreach (var feed in ManifestXmlService.ReadFromString(registered)) retained.Add(Path.GetFileName(feed.ImagePath));
            foreach (string path in _template.Value.Sources.Keys.Where(p => p.StartsWith("Images" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                retained.Add(Path.GetFileName(path));

            DateTime cutoff = DateTime.UtcNow.AddDays(-1);
            string images = DeploymentFiles.Under(Work, "Images");
            if (Directory.Exists(images))
            {
                foreach (string path in Directory.EnumerateFiles(images))
                {
                    string file = DeploymentFiles.Under(images, Path.GetFileName(path));
                    if (retained.Contains(Path.GetFileName(file)) || File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                    try { File.Delete(file); deleted++; }
                    catch (IOException ex) { diagnostics.Add(ex.Message); }
                    catch (UnauthorizedAccessException ex) { diagnostics.Add(ex.Message); }
                }
            }
            // 使用包外文件视图删除真实部署图标，修复原先只清理重定向目录的问题。
            deleted += await deployed.CleanImagesAsync(retained, cutoff);
        }
        catch (Exception ex) { diagnostics.Add(ex.ToString()); }
        return new ImageCleanupResult(deleted, diagnostics);
    }

    private static void ValidateRequiredFiles(string root)
    {
        foreach (string path in new[] { "AppxManifest.xml", "FeedProvider\\FeedProvider.exe", "Images\\Default.png" })
            if (!File.Exists(DeploymentFiles.Under(root, path))) throw new FileNotFoundException($"部署候选缺少文件：{path}");
        _ = XDocument.Load(DeploymentFiles.Under(root, "AppxManifest.xml"));
    }

    private static void RemoveObsoleteRuntime(string root, DeploymentVersion version)
    {
        // 旧 CoreCLR 文件只可能从专用程序子目录移除；禁止对包含用户图片的根目录整体清空。
        foreach (string relative in DeploymentFiles.Enumerate(root, "FeedProvider").ToArray())
            if (!version.Files.ContainsKey(relative)) File.Delete(DeploymentFiles.Under(root, relative));
    }

    private static (string, Dictionary<string, string>) CreateTemplate()
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
            packageRoot = AppContext.BaseDirectory; 
            packageVersion = "unpackaged"; 
        }

        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in DeploymentFiles.Enumerate(Path.Combine(packageRoot, "Resources")))
        {
            sources.Add(relative, DeploymentFiles.Under(Path.Combine(packageRoot, "Resources"), relative));
        }
        foreach (string relative in DeploymentFiles.Enumerate(Path.Combine(packageRoot, "Assets")))
        {
            sources.Add(Path.Combine("Assets", relative), DeploymentFiles.Under(Path.Combine(packageRoot, "Assets"), relative));
        }
        
        if (!sources.ContainsKey("AppxManifest.xml") || !sources.ContainsKey("FeedProvider\\FeedProvider.exe"))
        {
            throw new FileNotFoundException("安装包缺少 Provider 模板或程序。");
        }

        // 安装包本身不可变，模板摘要每个进程只计算一次；包含所有资源，支持同版本号重建。
        string identity = packageVersion + "|" + RuntimeInformation.ProcessArchitecture + "|" +
            string.Join("\n", sources.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + DeploymentFiles.Hash(p.Value)));
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))), sources);
    }
}
