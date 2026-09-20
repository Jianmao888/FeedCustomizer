using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Documents;
using FeedCustomizer.Core.Feedback;
using FeedCustomizer.Core.Infrastructure.Deployment;
using FeedCustomizer.Core.Infrastructure.Documents;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.Core.WidgetData;
using FeedCustomizer.Core.Windowing;
using System.IO.Compression;

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
    ("地区策略脚本只使用执行器临时诊断", RegionPolicyUsesTemporaryDiagnosticsAsync),
    ("开发者模式脚本捕获操作与恢复错误", DeveloperModeCapturesFailureDiagnosticsAsync),
    ("同版本文档维护不启动PowerShell", CurrentDocumentMaintenanceSkipsCleanupAsync),
    ("全新安装同步文档但不启动PowerShell", FreshDocumentInstallationSkipsCleanupAsync),
    ("应用更新同步文档并且每版本只清理一次", UpdatedDocumentMaintenanceCleansOnceAsync),
    ("文档被手动删除时只从包内自愈", MissingDocumentSelfHealingSkipsCleanupAsync),
    ("文档存储使用完整候选替换并保留相对资源", DocumentStorageReplacesCompleteCatalogAsync),
    ("旧文档清理脚本限定固定目录", LegacyDocumentCleanupScriptIsBoundedAsync),
    ("首次窗口在工作区居中且保持默认大小", () =>
    {
        WindowRectangle placement = WindowPlacementPolicy.CreateCenteredDefault(
            new WindowRectangle(0, 0, 1920, 1040),
            96);
        Check(placement == new WindowRectangle(680, 120, 560, 800));
        return Task.CompletedTask;
    }),
    ("默认窗口大于工作区时左上角保持可见", () =>
    {
        WindowRectangle placement = WindowPlacementPolicy.CreateCenteredDefault(
            new WindowRectangle(100, 50, 500, 700),
            96);
        Check(placement == new WindowRectangle(100, 50, 560, 800));
        return Task.CompletedTask;
    }),
    ("默认窗口仅高度过大时水平居中并从顶部开始", () =>
    {
        WindowRectangle placement = WindowPlacementPolicy.CreateCenteredDefault(
            new WindowRectangle(0, 40, 1920, 700),
            96);
        Check(placement == new WindowRectangle(680, 40, 560, 800));
        return Task.CompletedTask;
    }),
    ("窗口恢复按目标DPI缩放并校正到工作区", () =>
    {
        var state = new WindowPlacementState(
            WindowPlacementPolicy.CurrentSchemaVersion,
            5000,
            -2000,
            560,
            800,
            96,
            false);
        WindowRectangle placement = WindowPlacementPolicy.Restore(
            state,
            new WindowRectangle(-1920, 0, 1920, 1040),
            144);
        Check(placement == new WindowRectangle(-1, 0, 840, 1200));
        return Task.CompletedTask;
    }),
    ("窗口恢复只校正左上角而不改变右下方", () =>
    {
        var state = new WindowPlacementState(
            WindowPlacementPolicy.CurrentSchemaVersion,
            1800,
            900,
            560,
            500,
            96,
            false);
        WindowRectangle placement = WindowPlacementPolicy.Restore(
            state,
            new WindowRectangle(0, 0, 1920, 1040),
            96);
        Check(placement == new WindowRectangle(1800, 900, 560, 500));
        return Task.CompletedTask;
    }),
    ("损坏窗口状态被拒绝", () =>
    {
        var state = new WindowPlacementState(
            WindowPlacementPolicy.CurrentSchemaVersion,
            0,
            0,
            -1,
            800,
            96,
            false);
        ExpectThrows(() => WindowPlacementPolicy.Restore(
            state,
            new WindowRectangle(0, 0, 1920, 1040),
            96));
        return Task.CompletedTask;
    }),
    ("PowerShell 失败日志包含脱敏诊断", PowerShellFailureLoggingAsync),
    ("日志归档只包含顶层应用日志", LogArchiveSelectionAsync),
    ("日志归档可读取正在追加的日志快照", ActiveLogArchiveAsync),
    ("空日志归档包含诊断说明", EmptyLogArchiveAsync),
    ("日志导出目录位于真实下载目录", () =>
    {
        string exportDirectory = LogArchiveDestination.CreateDirectoryPath(
            @"C:\Users\test\Downloads",
            "FeedCustomizer");
        Check(exportDirectory == @"C:\Users\test\Downloads\FeedCustomizer");

        string rootDirectory = LogArchiveDestination.CreateDirectoryPath(
            @"D:\",
            "FeedCustomizer");
        Check(rootDirectory == @"D:\FeedCustomizer");
        return Task.CompletedTask;
    }),
    ("日志导出目录拒绝越界", () =>
    {
        ExpectThrows(() => LogArchiveDestination.CreateDirectoryPath(
            @"C:\Users\test\Downloads",
            @"..\Outside"));
        return Task.CompletedTask;
    }),
    ("邮件调度在客户端接管后停止降级", FeedbackDispatcherStopsAfterHandledAsync),
    ("邮件调度在通道不支持时继续降级", FeedbackDispatcherFallsBackAsync),
    ("反馈邮箱与固定标识有效", () =>
    {
        Check(Constants.FeedbackEmailAddress == "jianmao888@outlook.com");
        Check(Guid.TryParse(Constants.FeedbackIdentifier, out _));
        return Task.CompletedTask;
    }),
    ("旧地区策略诊断文件按固定路径清理", () =>
    {
        using var directory = new TestDirectory();
        AppDataPaths.TestRoot = directory.Path;
        Directory.CreateDirectory(AppDataPaths.PackageLocalLogPath);
        string legacyPath = System.IO.Path.Combine(
            AppDataPaths.PackageLocalLogPath,
            AppLogConfiguration.LegacyRegionPolicyLogFileName);
        File.WriteAllText(legacyPath, "legacy");
        Check(AppLogConfiguration.DeleteLegacyRegionPolicyLog());
        Check(!File.Exists(legacyPath));
        return Task.CompletedTask;
    }),
    ("日志异常文本隐藏用户目录", () =>
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var exception = new InvalidOperationException(
            "测试路径：" + System.IO.Path.Combine(localAppData, "FeedCustomizer", "test.log"));
        string redacted = LogPrivacy.RedactException(exception);
        Check(!redacted.Contains(localAppData, StringComparison.OrdinalIgnoreCase));
        Check(redacted.Contains("%LOCALAPPDATA%", StringComparison.Ordinal));
        string truncated = LogPrivacy.PrepareDiagnostic(
            new string('x', LogPrivacy.MaximumDiagnosticLength + 100));
        Check(truncated.Contains("诊断已截断", StringComparison.Ordinal));
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

static async Task LogArchiveSelectionAsync()
{
    using var directory = new TestDirectory();
    string nested = System.IO.Path.Combine(directory.Path, "nested");
    Directory.CreateDirectory(nested);
    File.WriteAllText(System.IO.Path.Combine(directory.Path, "FeedCustomizer-20260919.log"), "first");
    File.WriteAllText(System.IO.Path.Combine(directory.Path, "unrelated.log"), "ignored");
    File.WriteAllText(System.IO.Path.Combine(nested, "FeedCustomizer-nested.log"), "ignored");

    await using var output = new MemoryStream();
    int count = await LogArchiveBuilder.CreateAsync(directory.Path, output, "empty");
    Check(count == 1);
    output.Position = 0;
    using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
    Check(archive.Entries.Count == 1);
    Check(archive.Entries[0].Name == "FeedCustomizer-20260919.log");
}

static async Task EmptyLogArchiveAsync()
{
    using var directory = new TestDirectory();
    await using var output = new MemoryStream();
    int count = await LogArchiveBuilder.CreateAsync(directory.Path, output, "no logs");
    Check(count == 0);
    output.Position = 0;
    using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
    ZipArchiveEntry information = archive.GetEntry("ExportInfo.txt")
        ?? throw new Exception("空日志归档缺少说明文件。");
    using var reader = new StreamReader(information.Open());
    Check(await reader.ReadToEndAsync() == "no logs");
}

static async Task ActiveLogArchiveAsync()
{
    using var directory = new TestDirectory();
    string logPath = System.IO.Path.Combine(directory.Path, "FeedCustomizer-active.log");
    await using var activeLog = new FileStream(
        logPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.ReadWrite | FileShare.Delete);
    await activeLog.WriteAsync("active"u8.ToArray());
    await activeLog.FlushAsync();

    await using var output = new MemoryStream();
    int count = await LogArchiveBuilder.CreateAsync(directory.Path, output, "empty");
    Check(count == 1);
    output.Position = 0;
    using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
    using var reader = new StreamReader(archive.Entries.Single().Open());
    Check(await reader.ReadToEndAsync() == "active");
}

static async Task FeedbackDispatcherStopsAfterHandledAsync()
{
    var first = new FakeFeedbackTransport(
        "first",
        FeedbackMailTransportStatus.ClientHandled,
        attachmentRequested: true);
    var second = new FakeFeedbackTransport(
        "second",
        FeedbackMailTransportStatus.Launched,
        attachmentRequested: false);
    var dispatcher = new FeedbackMailDispatcher([first, second]);
    FeedbackMailTransportResult result = await dispatcher.LaunchAsync(CreateFeedbackMessage(), IntPtr.Zero);
    Check(result.Status == FeedbackMailTransportStatus.ClientHandled);
    Check(first.Calls == 1);
    Check(second.Calls == 0);
}

static async Task FeedbackDispatcherFallsBackAsync()
{
    var first = new FakeFeedbackTransport(
        "first",
        FeedbackMailTransportStatus.Unsupported,
        attachmentRequested: false);
    var second = new FakeFeedbackTransport(
        "second",
        FeedbackMailTransportStatus.Launched,
        attachmentRequested: false);
    var dispatcher = new FeedbackMailDispatcher([first, second]);
    FeedbackMailTransportResult result = await dispatcher.LaunchAsync(CreateFeedbackMessage(), IntPtr.Zero);
    Check(result.Status == FeedbackMailTransportStatus.Launched);
    Check(first.Calls == 1);
    Check(second.Calls == 1);
}

static FeedbackMailMessage CreateFeedbackMessage() =>
    new("feedback@example.com", "subject", "body", "archive.zip", "archive.zip");

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

static async Task RegionPolicyUsesTemporaryDiagnosticsAsync()
{
    var executor = new CapturingExecutor();
    var adapter = new RegionPolicyPowerShellAdapter(executor);
    string missingPolicyName = $"FeedCustomizer-test-{Guid.NewGuid():N}.json";
    PowerShellResult result = await adapter.EnablePolicyAsync(missingPolicyName, "test-guid");
    Check(result.ExitCode == 0);
    PowerShellScript generatedScript = executor.Script ?? throw new Exception("未捕获地区策略脚本。");
    Check(generatedScript.Content.Contains("$errorPath", StringComparison.Ordinal));
    Check(!generatedScript.Content.Contains("RegionPolicyError", StringComparison.Ordinal));
    Check(!generatedScript.Content.Contains("diagnosticsPath", StringComparison.Ordinal));
    Check(!generatedScript.Content.Contains("WindowsIdentity", StringComparison.Ordinal));

    // 使用必定不存在的策略文件以普通权限执行，只验证脚本语法和临时错误回传，不修改系统文件。
    PowerShellResult executionResult = await new PowerShellProcessExecutor().ExecuteAsync(
        generatedScript with
        {
            RequiresElevation = false
        });
    Check(executionResult.ExitCode == 3);
    Check(executionResult.Error.Contains("File not found", StringComparison.Ordinal));
}

static async Task PowerShellFailureLoggingAsync()
{
    AppLog.Clear();
    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string sensitivePath = System.IO.Path.Combine(localAppData, "FeedCustomizer", "private.txt");
    string script = string.Join(
        Environment.NewLine,
        "'controlled-output' | Out-File -FilePath $outputPath -Encoding UTF8",
        $"({PowerShellLiteral.Quote(sensitivePath)} + ' controlled-error') | Out-File -FilePath $errorPath -Encoding UTF8",
        "exit 9");
    PowerShellResult result = await new PowerShellProcessExecutor().ExecuteAsync(
        new PowerShellScript("DiagnosticFailure.ps1", script));
    Check(result.ExitCode == 9);

    TestLogEvent failureEvent = AppLog.Events.LastOrDefault(logEvent =>
        logEvent.Level == "Warning" &&
        logEvent.MessageTemplate.Contains("PowerShell 执行产生错误诊断", StringComparison.Ordinal))
        ?? throw new Exception("未记录 PowerShell 失败诊断。");
    string loggedValues = string.Join(
        Environment.NewLine,
        failureEvent.PropertyValues.Select(value => value?.ToString() ?? string.Empty));
    Check(loggedValues.Contains("controlled-output", StringComparison.Ordinal));
    Check(loggedValues.Contains("controlled-error", StringComparison.Ordinal));
    Check(loggedValues.Contains("%LOCALAPPDATA%", StringComparison.Ordinal));
    Check(!loggedValues.Contains(localAppData, StringComparison.OrdinalIgnoreCase));
}

static async Task DeveloperModeCapturesFailureDiagnosticsAsync()
{
    var executor = new CapturingExecutor();
    var adapter = new DeveloperModePowerShellAdapter(executor);
    PowerShellResult result = await adapter.RegisterPackageAsync("C:\\test\\AppxManifest.xml");
    Check(result.ExitCode == 0);
    PowerShellScript generatedScript = executor.Script ?? throw new Exception("未捕获开发者模式脚本。");
    Check(generatedScript.RequiresElevation);
    Check(generatedScript.Content.Contains("$errorPath", StringComparison.Ordinal));
    Check(generatedScript.Content.Contains("operation failure type", StringComparison.Ordinal));
    Check(generatedScript.Content.Contains("restore failure type", StringComparison.Ordinal));
    Check(generatedScript.Content.Contains("$originalRead", StringComparison.Ordinal));
}

static async Task CurrentDocumentMaintenanceSkipsCleanupAsync()
{
    var storage = new FakeDocumentStorage
    {
        State = new ApplicationDocumentState("2.0.0.0", ApplicationDocumentCatalog.SchemaVersion, "2.0.0.0", true),
        DocumentPath = "C:\\private\\Documents\\Help\\en-US.html",
    };
    var service = new ApplicationDocumentService(storage);

    await service.MaintainAfterStartupAsync();

    Check(storage.ReplaceCalls == 0);
    Check(storage.CleanupCalls == 0);
    Check(storage.WriteStateCalls == 0);
}

static async Task FreshDocumentInstallationSkipsCleanupAsync()
{
    var storage = new FakeDocumentStorage();
    var service = new ApplicationDocumentService(storage);

    await service.MaintainAfterStartupAsync();

    Check(storage.ReplaceCalls == 1);
    Check(storage.CleanupCalls == 0);
    Check(storage.State?.PackageVersion == storage.CurrentPackageVersion);
}

static async Task UpdatedDocumentMaintenanceCleansOnceAsync()
{
    var storage = new FakeDocumentStorage
    {
        State = new ApplicationDocumentState("1.0.0.0", ApplicationDocumentCatalog.SchemaVersion, "1.0.0.0", false),
        DocumentPath = "C:\\private\\Documents\\Help\\en-US.html",
    };
    var service = new ApplicationDocumentService(storage);

    await service.MaintainAfterStartupAsync();
    await service.MaintainAfterStartupAsync();

    Check(storage.ReplaceCalls == 1);
    Check(storage.CleanupCalls == 1);
    Check(storage.State?.PackageVersion == storage.CurrentPackageVersion);
    Check(storage.State?.LegacyCleanupAttemptedVersion == storage.CurrentPackageVersion);
    Check(storage.State?.LegacyCleanupCompleted == true);
}

static async Task MissingDocumentSelfHealingSkipsCleanupAsync()
{
    var storage = new FakeDocumentStorage
    {
        State = new ApplicationDocumentState("2.0.0.0", ApplicationDocumentCatalog.SchemaVersion, "2.0.0.0", true),
    };
    var service = new ApplicationDocumentService(storage);

    ApplicationDocumentResult result = await service.PrepareAsync(
        ApplicationDocumentKind.Help,
        "zh-CN");

    Check(result.Succeeded);
    Check(result.FilePath.EndsWith("zh-CN.html", StringComparison.Ordinal));
    Check(storage.ReplaceCalls == 1);
    Check(storage.CleanupCalls == 0);
}

static async Task LegacyDocumentCleanupScriptIsBoundedAsync()
{
    var executor = new CapturingExecutor
    {
        Result = new PowerShellResult(0, "False", string.Empty),
    };
    var adapter = new LegacyDocumentPowerShellAdapter(executor);

    Check(!await adapter.RemoveLegacyHelpAsync());
    PowerShellScript script = executor.Script ?? throw new Exception("未捕获旧文档清理脚本。");
    Check(script.Content.Contains("'FeedCustomProvider'", StringComparison.Ordinal));
    Check(script.Content.Contains("'HelpDoc'", StringComparison.Ordinal));
    Check(script.Content.Contains("ReparsePoint", StringComparison.Ordinal));
    Check(script.Content.Contains("Remove-Item -LiteralPath $target", StringComparison.Ordinal));
    Check(!script.Content.Contains("Remove-Item $target", StringComparison.Ordinal));
}

static async Task DocumentStorageReplacesCompleteCatalogAsync()
{
    using var directory = new TestDirectory();
    AppDataPaths.TestRoot = directory.Path;
    string source = System.IO.Path.Combine(directory.Path, "package", "Documents");
    string help = System.IO.Path.Combine(source, "Help");
    string licenses = System.IO.Path.Combine(source, "OpenSourceLicenses");
    string privacy = System.IO.Path.Combine(source, "Privacy");
    Directory.CreateDirectory(help);
    Directory.CreateDirectory(licenses);
    Directory.CreateDirectory(privacy);
    File.WriteAllText(System.IO.Path.Combine(help, "en-US.html"), "<img src=\"winui3.png\">");
    File.WriteAllText(System.IO.Path.Combine(help, "zh-CN.html"), "中文帮助");
    File.WriteAllText(System.IO.Path.Combine(help, "winui3.png"), "image");
    File.WriteAllText(System.IO.Path.Combine(licenses, "en-US.html"), "Open-source licenses");
    File.WriteAllText(System.IO.Path.Combine(licenses, "zh-CN.html"), "开源软件许可");
    File.WriteAllText(System.IO.Path.Combine(privacy, "en-US.html"), "Privacy statement");
    File.WriteAllText(System.IO.Path.Combine(privacy, "zh-CN.html"), "隐私声明");

    var executor = new CapturingExecutor
    {
        Result = new PowerShellResult(0, "False", string.Empty),
    };
    var storage = new ApplicationDocumentStorage(
        source,
        AppDataPaths.PackageLocalDocumentsFolder,
        AppDataPaths.PackageLocalDocumentsStatePath,
        AppDataPaths.LegacyPackageLocalHelpDocFolder,
        "2.0.0.0",
        new LegacyDocumentPowerShellAdapter(executor));

    await storage.ReplaceCatalogAsync(CancellationToken.None);

    string? localized = storage.ResolveDocumentPath(ApplicationDocumentKind.Help, "zh-CN");
    string? fallback = storage.ResolveDocumentPath(ApplicationDocumentKind.Help, "fr-FR");
    string? licenseLocalized = storage.ResolveDocumentPath(ApplicationDocumentKind.OpenSourceLicenses, "zh-CN");
    string? licenseFallback = storage.ResolveDocumentPath(ApplicationDocumentKind.OpenSourceLicenses, "fr-FR");
    string? privacyLocalized = storage.ResolveDocumentPath(ApplicationDocumentKind.Privacy, "zh-CN");
    string? privacyFallback = storage.ResolveDocumentPath(ApplicationDocumentKind.Privacy, "fr-FR");
    Check(localized is not null && File.ReadAllText(localized) == "中文帮助");
    Check(fallback is not null && fallback.EndsWith("en-US.html", StringComparison.Ordinal));
    Check(File.Exists(System.IO.Path.Combine(AppDataPaths.PackageLocalDocumentsFolder, "Help", "winui3.png")));
    Check(licenseLocalized is not null && File.ReadAllText(licenseLocalized) == "开源软件许可");
    Check(licenseFallback is not null && licenseFallback.EndsWith("en-US.html", StringComparison.Ordinal));
    Check(privacyLocalized is not null && File.ReadAllText(privacyLocalized) == "隐私声明");
    Check(privacyFallback is not null && privacyFallback.EndsWith("en-US.html", StringComparison.Ordinal));
    Check(executor.Script is null);

    var state = new ApplicationDocumentState("2.0.0.0", ApplicationDocumentCatalog.SchemaVersion, string.Empty, true);
    storage.WriteState(state);
    Check(storage.ReadState() == state);
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

sealed class CapturingExecutor : IPowerShellExecutor
{
    public PowerShellScript? Script { get; private set; }

    public PowerShellResult Result { get; set; } = new(0, string.Empty, string.Empty);

    public Task<PowerShellResult> ExecuteAsync(
        PowerShellScript script,
        CancellationToken cancellationToken = default)
    {
        Script = script;
        return Task.FromResult(Result);
    }
}

/// <summary>
/// 文档协调测试替身只记录语义调用，避免测试启动真实 PowerShell 或接触用户文档目录。
/// </summary>
sealed class FakeDocumentStorage : IApplicationDocumentStorage
{
    public string CurrentPackageVersion { get; } = "2.0.0.0";

    public ApplicationDocumentState? State { get; set; }

    public string? DocumentPath { get; set; }

    public bool LegacyHelpExists { get; set; }

    public int ReplaceCalls { get; private set; }

    public int CleanupCalls { get; private set; }

    public int WriteStateCalls { get; private set; }

    public ApplicationDocumentState? ReadState() => State;

    public void WriteState(ApplicationDocumentState state)
    {
        WriteStateCalls++;
        State = state;
    }

    public bool LegacyPrivateHelpExists() => LegacyHelpExists;

    public Task ReplaceCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReplaceCalls++;
        DocumentPath ??= "C:\\private\\Documents\\Help\\zh-CN.html";
        return Task.CompletedTask;
    }

    public string? ResolveDocumentPath(ApplicationDocumentKind kind, string languageTag)
    {
        _ = kind;
        _ = languageTag;
        return DocumentPath;
    }

    public Task<LegacyDocumentCleanupResult> RemoveLegacyHelpAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupCalls++;
        return Task.FromResult(new LegacyDocumentCleanupResult(true, true, []));
    }
}

sealed class FakeFeedbackTransport(
    string name,
    FeedbackMailTransportStatus status,
    bool attachmentRequested) : IFeedbackMailTransport
{
    public string Name => name;

    public int Calls { get; private set; }

    public Task<FeedbackMailTransportResult> TryLaunchAsync(
        FeedbackMailMessage message,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken = default)
    {
        _ = message;
        _ = ownerWindowHandle;
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(new FeedbackMailTransportResult(
            status,
            name,
            attachmentRequested,
            string.Empty));
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
