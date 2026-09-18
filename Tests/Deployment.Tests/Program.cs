using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Infrastructure.Deployment;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.Core.WidgetData;

// 所有真实文件写入均限制在本次生成的临时目录；AppX/注册表/UAC 只使用替身，绝不改动机器注册状态。
var tests = new (string Name, Func<Task> Run)[]
{
    ("准备失败不卸载现有包", async () =>
    {
        var f = new Fixture { Storage = { FailAt = "Prepare" } };
        var result = await f.Coordinator.ApplyAsync(null, true, false);
        Check(!result.Succeeded && result.Stage == DeploymentStage.Preparing && result.ProviderEnabled == true);
        Check(!f.Trace.Contains("Remove") && !f.Trace.Contains("Register"));
    }),
    ("候选准备在卸载之前且相同版本不重复注册", async () =>
    {
        var f = new Fixture();
        Check((await f.Coordinator.ApplyAsync(null, true, false)).Succeeded);
        Check(f.Trace.IndexOf("Stage") < f.Trace.IndexOf("Remove"));
        Check(f.Trace.IndexOf("Publish") < f.Trace.IndexOf("Register"));
        var current = new Fixture { Storage = { Current = true } };
        Check((await current.Coordinator.ApplyAsync(null, true, false)).Succeeded);
        Check(!current.Trace.Contains("Stage") && !current.Trace.Contains("Remove"));
    }),
    ("发布失败停止注册并保留已保存配置", async () =>
    {
        var f = new Fixture { Storage = { FailAt = "Publish" } };
        var result = await f.Coordinator.ApplyAsync([new Feed()], true, false);
        Check(result.Stage == DeploymentStage.Publishing && result.ConfigurationSaved);
        Check(result.Compensation == DeploymentCompensation.ProviderDisabled && result.ProviderEnabled == false);
        Check(!f.Trace.Contains("Register"));
    }),
    ("UAC取消不回滚注册旧版", async () =>
    {
        var f = new Fixture { Platform = { RegisterStatus = ProviderRegistrationStatus.ElevationCancelled } };
        var result = await f.Coordinator.ApplyAsync(null, true, true);
        Check(result.Status == ProviderRegistrationStatus.ElevationCancelled);
        Check(result.Compensation == DeploymentCompensation.ProviderDisabled && f.Trace.Count(x => x == "Register") == 1);
    }),
    ("开发者模式确认结果透传", async () =>
    {
        var f = new Fixture { Platform = { RegisterStatus = ProviderRegistrationStatus.DeveloperModeConfirmationRequired } };
        var result = await f.Coordinator.ApplyAsync(null, true, false);
        Check(result.Status == ProviderRegistrationStatus.DeveloperModeConfirmationRequired && result.ProviderEnabled == false);
    }),
    ("卸载失败不得发布且补偿错误独立可见", async () =>
    {
        var f = new Fixture { Platform = { FailRemove = true } };
        var result = await f.Coordinator.ApplyAsync(null, true, false);
        Check(result.Stage == DeploymentStage.Unregistering && result.Compensation == DeploymentCompensation.Failed);
        Check(result.Error.Contains("remove failed") && result.CompensationError.Contains("remove failed"));
        Check(!f.Trace.Contains("Publish") && f.Storage.HasTransaction);
    }),
    ("注册返回成功仍须验证实际状态", async () =>
    {
        var f = new Fixture { Platform = { RegisterVisible = false } };
        var result = await f.Coordinator.ApplyAsync(null, true, false);
        Check(result.Stage == DeploymentStage.Verifying && !result.Succeeded);
        Check(!f.Trace.Contains("Commit"));
    }),
    ("中断恢复关闭未完成注册不安装旧版", async () =>
    {
        var f = new Fixture { Storage = { HasTransaction = true, PendingStage = DeploymentStage.Registering } };
        var state = await f.Coordinator.InspectAsync();
        Check(!state.Installed && !f.Trace.Contains("Register") && !f.Storage.HasTransaction);
    }),
    ("提交后中断只清日志", async () =>
    {
        var f = new Fixture { Storage = { HasTransaction = true, PendingStage = DeploymentStage.Completed } };
        Check((await f.Coordinator.InspectAsync()).Installed);
        Check(!f.Trace.Contains("Remove") && !f.Trace.Contains("Register"));
    }),
    ("关闭操作不发布文件", async () =>
    {
        var f = new Fixture();
        var result = await f.Coordinator.ApplyAsync(null, false, false);
        Check(result.Succeeded && result.ProviderEnabled == false);
        Check(!f.Trace.Contains("Publish") && !f.Trace.Contains("Register"));
    }),
    ("并发用例完整串行", async () =>
    {
        var f = new Fixture();
        var results = await Task.WhenAll(f.Coordinator.ApplyAsync(null, true, false), f.Coordinator.ApplyAsync(null, true, false));
        Check(results.All(x => x.Succeeded));
        int secondPrepare = f.Trace.FindIndex(f.Trace.IndexOf("Prepare") + 1, x => x == "Prepare");
        Check(f.Trace.IndexOf("Finish") < secondPrepare);
    }),
    ("未完成日志阻止图片清理", async () =>
    {
        var f = new Fixture { Storage = { HasTransaction = true } };
        Check((await f.Coordinator.CleanImagesAsync([])).Diagnostics.Count > 0);
        Check(!f.Trace.Contains("Clean"));
    }),
    ("路径越界拒绝及文件版本验证", () =>
    {
        using var directory = new TestDirectory();
        ExpectThrows(() => DeploymentFiles.Under(directory.Path, "..\\outside"));
        ExpectThrows(() => DeploymentFiles.Under(directory.Path, "C:\\outside"));
        ExpectThrows(() => DeploymentFiles.Under(directory.Path, "file:stream"));
        string file = System.IO.Path.Combine(directory.Path, "test.txt");
        File.WriteAllText(file, "v1");
        var version = DeploymentVersion.Capture(directory.Path, "template", ["test.txt"]);
        version.Save(directory.Path);
        Check(DeploymentVersion.Read(directory.Path)!.Matches(directory.Path));
        File.WriteAllText(file, "v2");
        Check(!version.Matches(directory.Path));
        return Task.CompletedTask;
    }),
    ("包外发布与清理脚本在临时目录实际执行", ScriptIntegrationAsync),
    ("旧安装原位更新保留配置和私有图片", WorkspaceUpgradeAsync),
    ("损坏用户清单阻止模板覆盖", CorruptWorkspaceAsync),
    ("小组件数据清理跨调用串行且保留锁定结果", WidgetDataResetCoordinatorAsync),
    ("日志异常文本隐藏用户目录", () =>
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var exception = new InvalidOperationException(
            "测试路径：" + System.IO.Path.Combine(localAppData, "FeedCustomizer", "test.log"));
        string redacted = LogPrivacy.RedactException(exception);
        Check(!redacted.Contains(localAppData, StringComparison.OrdinalIgnoreCase));
        Check(redacted.Contains("%LOCALAPPDATA%", StringComparison.Ordinal));
        return Task.CompletedTask;
    })
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"通过 {tests.Length} 项部署回归验证。");

static void Check(bool condition) { if (!condition) throw new Exception("断言失败。"); }
static void ExpectThrows(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("未拒绝非法路径。");
}

static async Task ScriptIntegrationAsync()
{
    using var directory = new TestDirectory();
    string candidate = System.IO.Path.Combine(directory.Path, "候选 ' & 目录");
    string target = System.IO.Path.Combine(directory.Path, "实际部署");
    Directory.CreateDirectory(candidate);
    Directory.CreateDirectory(System.IO.Path.Combine(target, "FeedProvider"));
    Directory.CreateDirectory(System.IO.Path.Combine(target, "Images"));
    File.WriteAllText(System.IO.Path.Combine(target, "FeedProvider", "old.dll"), "obsolete");
    string[] paths = ["AppxManifest.xml", "FeedProvider\\FeedProvider.exe", "Images\\Default.png", "Images\\used.png"];
    foreach (string path in paths)
    {
        string full = DeploymentFiles.Under(candidate, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, path == "AppxManifest.xml" ? "<Package />" : "data");
    }
    var version = DeploymentVersion.Capture(candidate, "version-1", paths);
    version.Save(candidate);
    var adapter = new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(target));
    Check(!await adapter.IsCurrentAsync(System.IO.Path.Combine(candidate, DeploymentFiles.VersionFile)));
    await adapter.PublishAsync(candidate);
    Check(!File.Exists(System.IO.Path.Combine(target, "FeedProvider", "old.dll")));
    Check(await adapter.IsCurrentAsync(System.IO.Path.Combine(candidate, DeploymentFiles.VersionFile)));
    File.WriteAllText(System.IO.Path.Combine(target, DeploymentFiles.VersionFile), "broken metadata");
    Check(!await adapter.IsCurrentAsync(System.IO.Path.Combine(candidate, DeploymentFiles.VersionFile)));
    await adapter.PublishAsync(candidate);
    Check((await adapter.ReadManifestAsync()).Contains("Package"));

    // 图片清理必须保留默认、被引用和刚下载的文件，只删除超过宽限期的孤儿。
    foreach (string name in new[] { "Default.png", "used.png", "orphan.png", "recent.png" })
    {
        string path = System.IO.Path.Combine(target, "Images", name);
        File.WriteAllText(path, "image");
        if (name != "recent.png") File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
    }
    Check(await adapter.CleanImagesAsync(["Default.png", "used.png"], DateTime.UtcNow.AddDays(-1)) == 1);
    Check(!File.Exists(System.IO.Path.Combine(target, "Images", "orphan.png")));
    Check(File.Exists(System.IO.Path.Combine(target, "Images", "Default.png")));
    Check(File.Exists(System.IO.Path.Combine(target, "Images", "used.png")));
    Check(File.Exists(System.IO.Path.Combine(target, "Images", "recent.png")));

    // 候选在构建后被破坏时，脚本必须在写目标之前失败，不能发布未经校验的数据。
    File.WriteAllText(DeploymentFiles.Under(candidate, "FeedProvider\\FeedProvider.exe"), "corrupted");
    try { await adapter.PublishAsync(candidate); throw new Exception("损坏候选未被拒绝。"); }
    catch (IOException) { }
    Check(File.ReadAllText(DeploymentFiles.Under(target, "FeedProvider\\FeedProvider.exe")) == "data");
}

static async Task WorkspaceUpgradeAsync()
{
    using var directory = new TestDirectory();
    CreateWorkspace(directory.Path);
    string work = AppDataPaths.PackageLocalFeedProviderFolder;
    var storage = new ProviderDeploymentStorage(new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(AppDataPaths.FeedProviderFolder)));
    await storage.PrepareWorkAsync();
    var feeds = new List<Feed> { new() { Id = "existing-user-feed", Name = "旧用户订阅", Url = "https://example.com", ImagePath = "Images\\user.png" } };
    File.WriteAllText(DeploymentFiles.Under(work, "Images\\user.png"), "user-image");
    await storage.SaveFeedsAsync(feeds);
    // 模拟升级前版本：只有旧 flag，运行时目录留有 CoreCLR 文件，没有新版本清单。
    File.Delete(DeploymentFiles.Under(work, DeploymentFiles.VersionFile));
    File.WriteAllText(DeploymentFiles.Under(work, ".first_run_complete"), "1.0.0.0");
    File.WriteAllText(DeploymentFiles.Under(work, "FeedProvider\\old.dll"), "obsolete");
    Check(!await storage.IsWorkCurrentAsync());
    await storage.PrepareWorkAsync();
    Check((await ManifestXmlService.Read()).Single().Id == "existing-user-feed");
    Check(File.ReadAllText(DeploymentFiles.Under(work, "Images\\user.png")) == "user-image");
    Check(!File.Exists(DeploymentFiles.Under(work, "FeedProvider\\old.dll")));
    Check(await storage.IsWorkCurrentAsync());
    await storage.StageAsync();
    await storage.BeginAsync(false);
    await storage.RecordStageAsync(DeploymentStage.Publishing);
    // 使用同一磁盘数据重新创建实例，证明日志阶段能跨实例恢复。
    var reopened = new ProviderDeploymentStorage(new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(AppDataPaths.FeedProviderFolder)));
    Check(reopened.HasTransaction && reopened.PendingStage == DeploymentStage.Publishing);
    await reopened.PublishAsync();
    await reopened.CommitAsync();
    await reopened.FinishAsync();
    Check(await storage.IsDeploymentCurrentAsync());
    Check(File.ReadAllText(DeploymentFiles.Under(AppDataPaths.FeedProviderFolder, "Images\\user.png")) == "user-image");

    // 同时在私有与实际部署目录构造旧孤儿；未应用的草稿和包内默认图不能被清理。
    foreach (string root in new[] { work, AppDataPaths.FeedProviderFolder })
    {
        foreach (string image in new[] { "orphan.png", "draft.png" })
        {
            string path = DeploymentFiles.Under(root, "Images\\" + image);
            File.WriteAllText(path, "image");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        }
    }
    var cleanup = await storage.CleanImagesAsync(["Images\\draft.png"], true);
    Check(cleanup.Deleted == 2 && cleanup.Diagnostics.Count == 0);
    foreach (string root in new[] { work, AppDataPaths.FeedProviderFolder })
    {
        Check(!File.Exists(DeploymentFiles.Under(root, "Images\\orphan.png")));
        Check(File.Exists(DeploymentFiles.Under(root, "Images\\draft.png")));
        Check(File.Exists(DeploymentFiles.Under(root, "Images\\Default.png")));
    }
    // 注册清单解析失败必须整轮跳过，不得继续删除私有图片。
    File.WriteAllText(DeploymentFiles.Under(AppDataPaths.FeedProviderFolder, "AppxManifest.xml"), "broken");
    Check((await storage.CleanImagesAsync([], true)).Diagnostics.Count > 0);
}

static async Task CorruptWorkspaceAsync()
{
    using var directory = new TestDirectory();
    CreateWorkspace(directory.Path);
    Directory.CreateDirectory(AppDataPaths.PackageLocalFeedProviderFolder);
    File.WriteAllText(AppDataPaths.PackageLocalManifestPath, "broken-user-data");
    var storage = new ProviderDeploymentStorage(new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(AppDataPaths.FeedProviderFolder)));
    try { await storage.PrepareWorkAsync(); throw new Exception("损坏配置未阻止准备。"); }
    catch (System.Xml.XmlException) { }
    Check(File.ReadAllText(AppDataPaths.PackageLocalManifestPath) == "broken-user-data");
}

static async Task WidgetDataResetCoordinatorAsync()
{
    var platform = new FakeWidgetDataPlatform();
    var coordinator = new WidgetDataResetCoordinator(platform);

    Task<WidgetDataClearResult> first = coordinator.ClearAsync();
    await platform.FirstCallStarted.Task;
    Task<WidgetDataClearResult> second = coordinator.ClearAsync();
    await Task.Delay(30);
    Check(platform.Calls == 1);

    platform.FirstCallResult.TrySetResult(new WidgetDataClearResult(WidgetDataClearStatus.Locked, "locked"));
    Check((await first).Status == WidgetDataClearStatus.Locked);
    Check((await second).Status == WidgetDataClearStatus.Succeeded);
    Check(platform.Calls == 2);
}

static void CreateWorkspace(string root)
{
    AppDataPaths.TestRoot = root;
    string package = System.IO.Path.Combine(root, "package");
    Windows.ApplicationModel.Package.Current.InstalledLocation.Path = package;
    Directory.CreateDirectory(System.IO.Path.Combine(package, "Resources", "FeedProvider"));
    Directory.CreateDirectory(System.IO.Path.Combine(package, "Resources", "Images"));
    Directory.CreateDirectory(System.IO.Path.Combine(package, "Assets"));
    File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "Template.xml"), System.IO.Path.Combine(package, "Resources", "AppxManifest.xml"));
    File.WriteAllText(System.IO.Path.Combine(package, "Resources", "FeedProvider", "FeedProvider.exe"), "test-program");
    File.WriteAllText(System.IO.Path.Combine(package, "Resources", "Images", "Default.png"), "default-image");
    File.WriteAllText(System.IO.Path.Combine(package, "Assets", "StoreLogo.scale-200.png"), "logo");
}

/// <summary>只替换脚本的固定根目录定义；继续使用真实执行器、脚本和 Windows PowerShell。</summary>
sealed class SandboxExecutor(string target) : IPowerShellExecutor
{
    public Task<PowerShellResult> ExecuteAsync(PowerShellScript script, CancellationToken cancellationToken = default)
    {
        const string original = "$target = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FeedCustomProvider'";
        if (!script.Content.Contains(original)) throw new InvalidOperationException("测试根目录替换失败，拒绝执行。");
        return new PowerShellProcessExecutor().ExecuteAsync(script with
        { Content = script.Content.Replace(original, "$target = " + PowerShellLiteral.Quote(target)) }, cancellationToken);
    }
}

sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FeedCustomizer-tests-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => DeploymentFiles.Clear(Path);
}

sealed class Fixture
{
    public List<string> Trace { get; } = [];
    public FakeStorage Storage { get; }
    public FakePlatform Platform { get; }
    public ProviderDeploymentCoordinator Coordinator { get; }
    public Fixture()
    {
        Storage = new(Trace); Platform = new(Trace);
        Coordinator = new(Storage, Platform);
    }
}

sealed class FakeStorage(List<string> trace) : IProviderDeploymentStorage
{
    public string ManifestPath => "test-manifest";
    public bool HasTransaction { get; set; }
    public DeploymentStage PendingStage { get; set; }
    public bool TransactionCommitted => PendingStage == DeploymentStage.Completed;
    public bool Current { get; set; }
    public string? FailAt { get; set; }
    private void Record(string name) { trace.Add(name); if (name == FailAt) throw new IOException(name + " failed"); }
    public Task<bool> IsWorkCurrentAsync() => Task.FromResult(true);
    public async Task PrepareWorkAsync() { Record("Prepare"); await Task.Delay(10); }
    public Task SaveFeedsAsync(List<Feed> feeds) { Record("Save"); return Task.CompletedTask; }
    public Task<bool> IsDeploymentCurrentAsync() => Task.FromResult(Current);
    public Task StageAsync() { Record("Stage"); return Task.CompletedTask; }
    public Task BeginAsync(bool installed) { Record("Begin"); HasTransaction = true; PendingStage = DeploymentStage.Staging; return Task.CompletedTask; }
    public Task RecordStageAsync(DeploymentStage stage) { PendingStage = stage; return Task.CompletedTask; }
    public Task PublishAsync() { Record("Publish"); return Task.CompletedTask; }
    public Task CommitAsync() { Record("Commit"); PendingStage = DeploymentStage.Completed; return Task.CompletedTask; }
    public Task FinishAsync() { Record("Finish"); HasTransaction = false; return Task.CompletedTask; }
    public Task<ImageCleanupResult> CleanImagesAsync(IReadOnlyCollection<string> draftImages, bool installed)
    { Record("Clean"); return Task.FromResult(new ImageCleanupResult(0, [])); }
}

sealed class FakePlatform(List<string> trace) : IProviderRegistrationPlatform
{
    public bool Installed { get; set; } = true;
    public bool FailRemove { get; set; }
    public bool RegisterVisible { get; set; } = true;
    public ProviderRegistrationStatus RegisterStatus { get; set; } = ProviderRegistrationStatus.Success;
    public Task<bool> IsInstalledAsync(CancellationToken token) { trace.Add("Query"); return Task.FromResult(Installed); }
    public Task RemoveAsync(CancellationToken token)
    { trace.Add("Remove"); if (FailRemove) throw new IOException("remove failed"); Installed = false; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken token) { trace.Add("Stop"); return Task.CompletedTask; }
    public Task<ProviderRegistrationResult> RegisterAsync(bool allowDeveloperMode, CancellationToken token)
    {
        trace.Add("Register"); Installed = RegisterStatus == ProviderRegistrationStatus.Success && RegisterVisible;
        return Task.FromResult(new ProviderRegistrationResult(RegisterStatus, 0, string.Empty, string.Empty, "test-manifest"));
    }
}

sealed class FakeWidgetDataPlatform : IWidgetDataResetPlatform
{
    public int Calls;
    public TaskCompletionSource<bool> FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<WidgetDataClearResult> FirstCallResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<WidgetDataClearResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref Calls);
        if (call == 1)
        {
            FirstCallStarted.TrySetResult(true);
            return await FirstCallResult.Task.WaitAsync(cancellationToken);
        }

        return new WidgetDataClearResult(WidgetDataClearStatus.Succeeded, "success");
    }
}
