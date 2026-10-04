using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Documents;
using FeedCustomizer.Core.Feedback;
using FeedCustomizer.Core.Infrastructure.Deployment;
using FeedCustomizer.Core.Infrastructure.Documents;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Infrastructure.Storage;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.Core.WidgetData;
using FeedCustomizer.Core.Windowing;
using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

// 所有真实文件写入均限制在本次生成的临时目录或测试项目的 obj 目录；
// AppX/注册表/UAC 只使用替身，绝不改动机器注册状态。
var tests = new (string Name, Func<Task> Run)[]
{
    ("启动未就绪不保存配置或卸载现有包", async () =>
    {
        var f = new Fixture { Storage = { WorkReady = false } };
        var result = await f.Coordinator.ApplyAsync([new Feed()], true, false);
        Check(!result.Succeeded && result.Stage == DeploymentStage.Preparing && result.ProviderEnabled == true);
        Check(!f.Trace.Contains("Save") && !f.Trace.Contains("Prepare"));
        Check(!f.Trace.Contains("Remove") && !f.Trace.Contains("Register"));
    }),
    ("启动修复失败不会在应用配置时再次修复", async () =>
    {
        var f = new Fixture { Storage = { WorkReady = false, FailAt = "Prepare" } };
        Check(!(await f.Coordinator.InspectAsync()).ResourcesCurrent);
        bool failed = false;
        try
        {
            await f.Coordinator.PrepareAsync();
        }
        catch (IOException)
        {
            failed = true;
        }

        Check(failed);
        Check(!(await f.Coordinator.ApplyAsync(null, true, false)).Succeeded);
        Check(f.Trace.Count(step => step == "Prepare") == 1);
        Check(!f.Trace.Contains("Remove") && !f.Trace.Contains("Publish"));
    }),
    ("启用与保存不重新检查或准备工作版本", async () =>
    {
        var f = new Fixture();
        Check((await f.Coordinator.ApplyAsync(null, true, false)).Succeeded);
        Check((await f.Coordinator.ApplyAsync([new Feed()], true, false)).Succeeded);
        Check((await f.Coordinator.ApplyAsync([new Feed()], false, false)).Succeeded);
        Check(!f.Trace.Contains("InspectWork") && !f.Trace.Contains("Prepare"));
    }),
    ("候选准备在卸载之前且相同版本不重复注册", async () =>
    {
        var f = new Fixture();
        Check((await f.Coordinator.ApplyAsync(null, true, false)).Succeeded);
        Check(f.Trace.IndexOf("Stage") < f.Trace.IndexOf("Remove"));
        Check(f.Trace.IndexOf("Publish") < f.Trace.IndexOf("Register"));
        var current = new Fixture { Storage = { Current = true } };
        Check((await current.Coordinator.ApplyAsync(null, true, false)).Succeeded);
        Check(!current.Trace.Contains("Stage") && !current.Trace.Contains("Remove") && !current.Trace.Contains("DeveloperMode"));
    }),
    ("仅配置计划贯穿卸载前候选和卸载后发布", async () =>
    {
        var f = new Fixture { Storage = { ConfigurationOnly = true } };
        Check((await f.Coordinator.ApplyAsync([new Feed()], true, false)).Succeeded);
        Check(f.Storage.StagedScope == ProviderDeploymentScope.Configuration);
        Check(f.Platform.PublishedScope == ProviderDeploymentScope.Configuration);
        Check(f.Trace.IndexOf("Stage") < f.Trace.IndexOf("Remove"));
        Check(f.Trace.IndexOf("Remove") < f.Trace.IndexOf("Publish"));
    }),
    ("发布失败停止注册并保留已保存配置", async () =>
    {
        var f = new Fixture { Platform = { FailPublish = true } };
        var result = await f.Coordinator.ApplyAsync([new Feed()], true, false);
        Check(result.Stage == DeploymentStage.Publishing && result.ConfigurationSaved);
        Check(result.Compensation == DeploymentCompensation.ProviderDisabled && result.ProviderEnabled == false);
        Check(!f.Trace.Contains("Register"));
    }),
    ("UAC取消不回滚注册旧版", async () =>
    {
        var f = new Fixture { Platform = { DeveloperMode = DeveloperModeState.Disabled, TemporaryRegisterStatus = ProviderRegistrationStatus.ElevationCancelled } };
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
    ("注册前检查在卸载停止后且关闭不检查", async () =>
    {
        var f = new Fixture();
        Check((await f.Coordinator.ApplyAsync(null, true, false)).Succeeded);
        Check(f.Trace.IndexOf("Remove") < f.Trace.IndexOf("DeveloperMode"));
        Check(f.Trace.IndexOf("Stop") < f.Trace.IndexOf("DeveloperMode"));
        Check(f.Trace.IndexOf("DeveloperMode") < f.Trace.IndexOf("Publish"));
        Check(f.Trace.Count(step => step == "Register:Normal") == 1);
        var closed = new Fixture { Platform = { DeveloperMode = DeveloperModeState.Disabled } };
        Check((await closed.Coordinator.ApplyAsync(null, false, false)).Succeeded);
        Check(!closed.Trace.Contains("DeveloperMode"));
    }),
    ("未开启且不自动开启只提示不发布注册", async () =>
    {
        var f = new Fixture { Platform = { DeveloperMode = DeveloperModeState.Disabled } };
        var result = await f.Coordinator.ApplyAsync([new Feed()], true, false);
        Check(result.Status == ProviderRegistrationStatus.DeveloperModeConfirmationRequired);
        Check(result.ExitCode is null && result.ConfigurationSaved && result.ProviderEnabled == false);
        Check(result.Stage == DeploymentStage.Registering && !f.Storage.HasTransaction);
        Check(f.Trace.Contains("Remove") && !f.Trace.Contains("Publish") && !f.Trace.Contains("Register"));
    }),
    ("未开启且自动开启直接提权注册", async () =>
    {
        var f = new Fixture { Platform = { DeveloperMode = DeveloperModeState.Disabled } };
        Check((await f.Coordinator.ApplyAsync(null, true, true)).Succeeded);
        Check(f.Trace.Count(step => step == "Publish") == 1);
        Check(f.Trace.Count(step => step == "Register:TemporaryDeveloperMode") == 1);
        Check(!f.Trace.Contains("Register:Normal"));
    }),
    ("读取失败仍普通注册且不因未知提权", async () =>
    {
        foreach (bool allowDeveloperMode in new[] { false, true })
        {
            var f = new Fixture { Platform = { DeveloperMode = DeveloperModeState.Unknown } };
            Check((await f.Coordinator.ApplyAsync(null, true, allowDeveloperMode)).Succeeded);
            Check(f.Trace.Count(step => step == "Register:Normal") == 1);
            Check(!f.Trace.Contains("Register:TemporaryDeveloperMode"));
        }
    }),
    ("普通注册需要开发者模式时单次兜底不重复发布", async () =>
    {
        foreach (DeveloperModeState state in new[] { DeveloperModeState.Unknown, DeveloperModeState.Enabled })
        {
            var f = new Fixture { Platform = { DeveloperMode = state, RegisterStatus = ProviderRegistrationStatus.DeveloperModeConfirmationRequired } };
            Check((await f.Coordinator.ApplyAsync(null, true, true)).Succeeded);
            Check(f.Trace.Count(step => step == "Publish") == 1);
            Check(f.Trace.Count(step => step == "Register:Normal") == 1);
            Check(f.Trace.Count(step => step == "Register:TemporaryDeveloperMode") == 1);
        }
    }),
    ("未知状态注册需要开发者模式且不自动时返回确认", async () =>
    {
        var f = new Fixture { Platform = { DeveloperMode = DeveloperModeState.Unknown, RegisterStatus = ProviderRegistrationStatus.DeveloperModeConfirmationRequired } };
        var result = await f.Coordinator.ApplyAsync(null, true, false);
        Check(result.Status == ProviderRegistrationStatus.DeveloperModeConfirmationRequired && result.ProviderEnabled == false);
        Check(f.Trace.Count(step => step == "Register:Normal") == 1);
        Check(!f.Trace.Contains("Register:TemporaryDeveloperMode"));
    }),
    ("其他注册错误不提权", async () =>
    {
        var f = new Fixture { Platform = { DeveloperMode = DeveloperModeState.Unknown, RegisterStatus = ProviderRegistrationStatus.Failed } };
        Check(!(await f.Coordinator.ApplyAsync(null, true, true)).Succeeded);
        Check(!f.Trace.Contains("Register:TemporaryDeveloperMode"));
    }),
    ("提权兜底失败或取消立即结束", async () =>
    {
        foreach (ProviderRegistrationStatus status in new[] { ProviderRegistrationStatus.Failed, ProviderRegistrationStatus.ElevationCancelled })
        {
            var f = new Fixture { Platform =
            {
                DeveloperMode = DeveloperModeState.Unknown,
                RegisterStatus = ProviderRegistrationStatus.DeveloperModeConfirmationRequired,
                TemporaryRegisterStatus = status
            } };
            var result = await f.Coordinator.ApplyAsync(null, true, true);
            Check(result.Status == status && result.ProviderEnabled == false);
            Check(f.Trace.Count(step => step == "Register:TemporaryDeveloperMode") == 1);
            Check(!f.Storage.HasTransaction);
        }
    }),
    ("已确认重试不再检查或普通注册", async () =>
    {
        var f = new Fixture { Platform = { DeveloperMode = DeveloperModeState.Unknown } };
        Check((await f.Coordinator.ApplyAsync(null, true, true, developerModeConfirmed: true)).Succeeded);
        Check(!f.Trace.Contains("DeveloperMode") && !f.Trace.Contains("Register:Normal"));
        Check(f.Trace.Count(step => step == "Register:TemporaryDeveloperMode") == 1);
        foreach (bool enable in new[] { false, true })
        {
            bool rejected = false;
            try
            {
                _ = f.Coordinator.ApplyAsync(null, enable, !enable, developerModeConfirmed: true);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Check(rejected);
        }
    }),
    ("直接提权路径发布失败禁止注册", async () =>
    {
        var f = new Fixture { Platform = { DeveloperMode = DeveloperModeState.Disabled, FailPublish = true } };
        var result = await f.Coordinator.ApplyAsync(null, true, true);
        Check(result.Stage == DeploymentStage.Publishing && result.ProviderEnabled == false);
        Check(!f.Trace.Contains("Register"));
    }),
    ("开发者模式缺失关闭而异常类型未知", () =>
    {
        Check(DeveloperModeRegistry.ParseValue(null) == DeveloperModeState.Disabled);
        Check(DeveloperModeRegistry.ParseValue(0) == DeveloperModeState.Disabled);
        Check(DeveloperModeRegistry.ParseValue(1) == DeveloperModeState.Enabled);
        Check(DeveloperModeRegistry.ParseValue("1") == DeveloperModeState.Unknown);
        Check(DeveloperModeRegistry.ParseValue(new byte[] { 1 }) == DeveloperModeState.Unknown);
        return Task.CompletedTask;
    }),
    ("卸载失败不得发布且补偿错误独立可见", async () =>
    {
        var f = new Fixture { Platform = { FailRemove = true } };
        var result = await f.Coordinator.ApplyAsync(null, true, false);
        Check(result.Stage == DeploymentStage.Unregistering && result.Compensation == DeploymentCompensation.Failed);
        Check(result.Error.Contains("remove failed") && result.CompensationError.Contains("remove failed"));
        Check(!f.Trace.Contains("Publish") && f.Storage.HasTransaction);
    }),
    ("注册状态查询失败不得当作未安装继续部署", async () =>
    {
        var f = new Fixture { Platform = { FailQuery = true } };
        var result = await f.Coordinator.ApplyAsync(null, true, false);
        Check(!result.Succeeded && result.Stage == DeploymentStage.Inspecting);
        Check(result.ProviderEnabled is null && !f.Storage.HasTransaction);
        Check(!f.Trace.Contains("Prepare") && !f.Trace.Contains("Remove") && !f.Trace.Contains("Publish"));
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
        var f = new Fixture { Storage = { WorkReady = false } };
        var result = await f.Coordinator.ApplyAsync(null, false, false);
        Check(result.Succeeded && result.ProviderEnabled == false);
        Check(!f.Trace.Contains("Publish") && !f.Trace.Contains("Register"));
    }),
    ("并发用例完整串行", async () =>
    {
        var f = new Fixture();
        var results = await Task.WhenAll(f.Coordinator.ApplyAsync(null, true, false), f.Coordinator.ApplyAsync(null, true, false));
        Check(results.All(x => x.Succeeded));
        int secondPlan = f.Trace.FindIndex(f.Trace.IndexOf("Plan") + 1, x => x == "Plan");
        Check(f.Trace.IndexOf("Finish") < secondPlan);
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
    ("部署计划只允许配置差异走部分发布", DeploymentPlanAsync),
    ("包外发布与清理脚本在临时目录实际执行", ScriptIntegrationAsync),
    ("组合脚本只启动一次且发布失败不会注册", CombinedScriptAsync),
    ("配置变化仅复制清单和图片", ConfigurationDeploymentAsync),
    ("工作版本只在启动时校验且修复保留配置", StartupWorkValidationAsync),
    ("配置发布不读取工作程序和静态资源", RuntimeWorkVersionAsync),
    ("候选构建仍验证实际复制的文件", CandidateValidationAsync),
    ("旧安装原位更新保留配置和私有图片", WorkspaceUpgradeAsync),
    ("损坏用户清单阻止模板覆盖", CorruptWorkspaceAsync),
    ("小组件数据清理跨调用串行且保留锁定结果", WidgetDataResetCoordinatorAsync),
    ("地区策略脚本只使用执行器临时诊断", RegionPolicyUsesTemporaryDiagnosticsAsync),
    ("地区策略只替换目标值并保留原始字节", RegionPolicyPreservesBytesAsync),
    ("地区策略拒绝歧义和损坏输入且不写入", RegionPolicyRejectsUnsafeEditsAsync),
    ("开发者模式脚本捕获操作与恢复错误", DeveloperModeCapturesFailureDiagnosticsAsync),
    ("临时开发者模式脚本执行恢复和错误合并", DeveloperModeScriptIntegrationAsync),
    ("同版本文档维护不启动PowerShell", CurrentDocumentMaintenanceSkipsCleanupAsync),
    ("旧文档结构版本不触发同包版本同步或状态重写", LegacyDocumentSchemaDoesNotTriggerSynchronizationAsync),
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
    }),
    ("伪本地化资源与英文资源同步", PseudoLocalizationResourcesStaySynchronizedAsync)
};

tests =
[
    .. tests,
    .. SettingsMigrationTests.Cases,
    .. DonationMigrationTests.Cases,
    .. RegionMigrationTests.Cases,
    .. UiThreadRunnerTests.Cases,
    .. ImageStorageMigrationTests.Cases
];

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

/// <summary>
/// 通过同一生成脚本在测试中创建临时副本，确保提交的伪资源不会在英文资源更新后悄然过期。
/// </summary>
static async Task PseudoLocalizationResourcesStaySynchronizedAsync()
{
    string repositoryRoot = FindRepositoryRoot();
    string sourcePath = System.IO.Path.Combine(
        repositoryRoot,
        "FeedCustomizer",
        "Strings",
        "en-US",
        "Resources.resw");
    string pseudoPath = System.IO.Path.Combine(
        repositoryRoot,
        "FeedCustomizer",
        "Strings",
        "qps-ploc",
        "Resources.resw");
    string scriptPath = System.IO.Path.Combine(
        repositoryRoot,
        "Tests",
        "PseudoLocalization",
        "Generate-PseudoResources.ps1");

    string generatedPath = System.IO.Path.Combine(
        repositoryRoot,
        "Tests",
        "Deployment.Tests",
        "obj",
        "PseudoLocalization",
        Guid.NewGuid().ToString("N"),
        "Resources.resw");

    var startInfo = new ProcessStartInfo
    {
        FileName = "powershell.exe",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("-NoLogo");
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-NonInteractive");
    startInfo.ArgumentList.Add("-ExecutionPolicy");
    startInfo.ArgumentList.Add("Bypass");
    startInfo.ArgumentList.Add("-File");
    startInfo.ArgumentList.Add(scriptPath);
    startInfo.ArgumentList.Add("-SourcePath");
    startInfo.ArgumentList.Add(sourcePath);
    startInfo.ArgumentList.Add("-DestinationPath");
    startInfo.ArgumentList.Add(generatedPath);

    using Process process = Process.Start(startInfo)
        ?? throw new Exception("无法启动伪本地化资源生成脚本。");
    string standardError = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new Exception($"伪本地化资源生成失败：{standardError}");
    }

    IReadOnlyDictionary<string, string> english = ReadResourceValues(sourcePath);
    IReadOnlyDictionary<string, string> pseudo = ReadResourceValues(pseudoPath);
    IReadOnlyDictionary<string, string> generated = ReadResourceValues(generatedPath);

    Check(english.Keys.OrderBy(key => key).SequenceEqual(pseudo.Keys.OrderBy(key => key)));
    Check(pseudo.OrderBy(entry => entry.Key).SequenceEqual(generated.OrderBy(entry => entry.Key)));
    Check(pseudo["LanguageTag"] == "en-US");

    foreach ((string key, string value) in pseudo)
    {
        if (key == "LanguageTag")
        {
            continue;
        }

        Check(value.StartsWith("[[", StringComparison.Ordinal));
        Check(value.EndsWith("]]", StringComparison.Ordinal));
        Check(value.Length > english[key].Length);
        Check(ExtractCompositeFormatItems(english[key])
            .SequenceEqual(ExtractCompositeFormatItems(value)));
    }
}

/// <summary>
/// 从测试输出目录逐层向上定位仓库根目录，避免依赖开发机上的固定工作目录。
/// </summary>
static string FindRepositoryRoot()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
         directory is not null;
         directory = directory.Parent)
    {
        if (File.Exists(System.IO.Path.Combine(
            directory.FullName,
            "FeedCustomizer",
            "FeedCustomizer.csproj")))
        {
            return directory.FullName;
        }
    }

    throw new DirectoryNotFoundException("无法定位 FeedCustomizer 仓库根目录。");
}

/// <summary>
/// 读取 RESW 的键值对；测试只检查字符串资源，不需要引入 WinUI 的资源运行时依赖。
/// </summary>
static IReadOnlyDictionary<string, string> ReadResourceValues(string path)
{
    return XDocument.Load(path)
        .Root!
        .Elements("data")
        .ToDictionary(
            element => (string?)element.Attribute("name")
                ?? throw new InvalidDataException("RESW 资源缺少 name 属性。"),
            element => element.Element("value")?.Value
                ?? throw new InvalidDataException("RESW 资源缺少 value 元素。"),
            StringComparer.Ordinal);
}

/// <summary>
/// 复合格式项的位置和格式必须保持不变，否则伪资源无法代表真实翻译在运行时的行为。
/// </summary>
static IEnumerable<string> ExtractCompositeFormatItems(string value)
{
    return System.Text.RegularExpressions.Regex.Matches(
        value,
        "\\{[0-9]+(?:,-?[0-9]+)?(?::[^{}]+)?\\}")
        .Select(match => match.Value);
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

static Task DeploymentPlanAsync()
{
    var version = new DeploymentVersion("template", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AppxManifest.xml"] = "manifest",
        ["FeedProvider\\FeedProvider.exe"] = "program",
        ["Assets\\Logo.png"] = "asset",
        ["Images\\used.png"] = "image"
    });

    Check(version.Plan(new(true, true, false, [], [])).Scope == ProviderDeploymentScope.Current);
    ProviderDeploymentPlan configuration = version.Plan(new(
        true,
        true,
        false,
        ["Images\\old.png"],
        ["AppxManifest.xml", "Images\\used.png"]));
    Check(configuration.Scope == ProviderDeploymentScope.Configuration);
    Check(configuration.CopyPaths.Count == 2 && !configuration.CopyPaths.Contains("Images\\old.png"));
    Check(version.Plan(new(true, false, false, [], ["AppxManifest.xml"])).Scope == ProviderDeploymentScope.Full);
    Check(version.Plan(new(false, true, false, [], ["AppxManifest.xml"])).Scope == ProviderDeploymentScope.Full);
    Check(version.Plan(new(true, true, true, [], ["AppxManifest.xml"])).Scope == ProviderDeploymentScope.Full);
    Check(version.Plan(new(true, true, false, [], ["FeedProvider\\FeedProvider.exe"])).Scope == ProviderDeploymentScope.Full);
    Check(version.Plan(new(true, true, false, [], ["Assets\\Logo.png"])).Scope == ProviderDeploymentScope.Full);
    Check(!DeploymentVersion.IsConfigurationPath("Images\\..\\FeedProvider\\FeedProvider.exe"));
    return Task.CompletedTask;
}

static async Task ConfigurationDeploymentAsync()
{
    using var directory = new TestDirectory();
    CreateWorkspace(directory.Path);
    string work = AppDataPaths.PackageLocalFeedProviderFolder;
    string target = AppDataPaths.FeedProviderFolder;
    var deployed = new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(target));
    var storage = new ProviderDeploymentStorage(deployed);
    await storage.PrepareWorkAtStartupAsync();

    ProviderDeploymentPlan full = await storage.PlanAsync(false);
    Check(full.Scope == ProviderDeploymentScope.Full);
    await storage.StageAsync(full);
    Check((await deployed.PublishAsync(storage.CandidatePath, full)).Result.ExitCode == 0);

    string executable = DeploymentFiles.Under(target, "FeedProvider\\FeedProvider.exe");
    string asset = DeploymentFiles.Under(target, "Assets\\StoreLogo.scale-200.png");
    DateTime unchangedTime = DateTime.UtcNow.AddDays(-2);
    File.SetLastWriteTimeUtc(executable, unchangedTime);
    File.SetLastWriteTimeUtc(asset, unchangedTime);
    DateTime programTime = File.GetLastWriteTimeUtc(executable);
    DateTime assetTime = File.GetLastWriteTimeUtc(asset);

    string imagePath = DeploymentFiles.Under(work, "Images\\user.png");
    File.WriteAllText(imagePath, "image-v1");
    await storage.SaveFeedsAsync([new Feed
    {
        Id = "user-feed",
        Name = "用户源",
        Url = "https://example.com",
        ImagePath = "Images\\user.png"
    }]);

    ProviderDeploymentPlan configuration = await storage.PlanAsync(true);
    Check(configuration.Scope == ProviderDeploymentScope.Configuration);
    Check(configuration.CopyPaths.Contains("AppxManifest.xml"));
    Check(configuration.CopyPaths.Contains("Images\\user.png"));
    Check(!configuration.CopyPaths.Any(path => path.StartsWith("FeedProvider\\", StringComparison.OrdinalIgnoreCase)));
    await storage.StageAsync(configuration);
    string candidate = DeploymentFiles.Under(work, ".deployment\\candidate");
    Check(!File.Exists(DeploymentFiles.Under(candidate, "FeedProvider\\FeedProvider.exe")));
    Check(!File.Exists(DeploymentFiles.Under(candidate, "Assets\\StoreLogo.scale-200.png")));

    // 独占程序文件：配置发布既不能覆盖它，也不应重新打开它计算哈希。
    using (var locked = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        Check((await deployed.PublishAsync(storage.CandidatePath, configuration)).Result.ExitCode == 0);
    }

    Check(File.GetLastWriteTimeUtc(executable) == programTime);
    Check(File.GetLastWriteTimeUtc(asset) == assetTime);
    Check(File.ReadAllText(DeploymentFiles.Under(target, "Images\\user.png")) == "image-v1");
    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Current);

    File.WriteAllText(imagePath, "image-v2");
    ProviderDeploymentPlan imageOnly = await storage.PlanAsync(true);
    Check(imageOnly.Scope == ProviderDeploymentScope.Configuration);
    Check(imageOnly.CopyPaths.Count == 1 && imageOnly.CopyPaths[0] == "Images\\user.png");
    await storage.StageAsync(imageOnly);
    Check((await deployed.PublishAsync(storage.CandidatePath, imageOnly)).Result.ExitCode == 0);
    Check(File.GetLastWriteTimeUtc(executable) == programTime);
    Check(File.GetLastWriteTimeUtc(asset) == assetTime);
    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Current);

    await storage.SaveFeedsAsync([new Feed
    {
        Id = "user-feed",
        Name = "不再引用图片",
        Url = "https://example.com"
    }]);
    ProviderDeploymentPlan removedImage = await storage.PlanAsync(true);
    Check(removedImage.Scope == ProviderDeploymentScope.Configuration);
    Check(removedImage.CopyPaths.Count == 1 && removedImage.CopyPaths[0] == "AppxManifest.xml");
    await storage.StageAsync(removedImage);
    Check((await deployed.PublishAsync(storage.CandidatePath, removedImage)).Result.ExitCode == 0);
    Check(File.Exists(DeploymentFiles.Under(target, "Images\\user.png")));
    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Current);

    // 发布复用规划结果，不重复检查期间发生的外部变化；下一次规划仍须发现程序漂移。
    await storage.SaveFeedsAsync([new Feed
    {
        Id = "user-feed",
        Name = "再次修改名称",
        Url = "https://example.com"
    }]);
    ProviderDeploymentPlan stale = await storage.PlanAsync(true);
    await storage.StageAsync(stale);
    File.WriteAllText(executable, "program-tampered");
    string unexpectedRuntime = DeploymentFiles.Under(target, "FeedProvider\\unexpected.dll");
    File.WriteAllText(unexpectedRuntime, "unexpected-program");
    File.WriteAllText(DeploymentFiles.Under(target, DeploymentFiles.VersionFile), "changed after planning");
    Check((await deployed.PublishAsync(storage.CandidatePath, stale)).Result.ExitCode == 0);
    Check(File.ReadAllText(DeploymentFiles.Under(target, "AppxManifest.xml")) ==
        File.ReadAllText(AppDataPaths.PackageLocalManifestPath));
    Check(File.ReadAllText(executable) == "program-tampered");
    Check(File.Exists(unexpectedRuntime));

    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Full);
    File.WriteAllText(executable, "test-program");
    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Full);
    File.Delete(unexpectedRuntime);
    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Current);
    File.WriteAllText(DeploymentFiles.Under(target, DeploymentFiles.VersionFile), "broken metadata");
    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Full);
}

static async Task StartupWorkValidationAsync()
{
    using var directory = new TestDirectory();
    CreateWorkspace(directory.Path);
    var adapter = new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(AppDataPaths.FeedProviderFolder));
    var original = new ProviderDeploymentStorage(adapter);
    Check(!await original.InspectWorkAtStartupAsync());
    Check(!original.WorkReady);
    await original.PrepareWorkAtStartupAsync();
    Check(original.WorkReady);
    string work = AppDataPaths.PackageLocalFeedProviderFolder;
    File.WriteAllText(DeploymentFiles.Under(work, "Images\\user.png"), "user-image");
    await original.SaveFeedsAsync([new Feed
    {
        Id = "preserved-feed",
        Name = "保留的订阅",
        Url = "https://example.com",
        ImagePath = "Images\\user.png"
    }]);

    // 模拟正常启动：检查成功后即信任工作目录，页面再次初始化不得读取程序内容。
    var current = new ProviderDeploymentStorage(adapter);
    Check(await current.InspectWorkAtStartupAsync());
    string executable = DeploymentFiles.Under(work, "FeedProvider\\FeedProvider.exe");
    using (var locked = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        Check(await current.InspectWorkAtStartupAsync());
        await current.PrepareWorkAtStartupAsync();
    }

    // 新进程重新检查仍能发现损坏；修复只恢复模板资源，不能丢失用户配置或自定义图片。
    File.WriteAllText(executable, "damaged-program");
    string asset = DeploymentFiles.Under(work, "Assets\\StoreLogo.scale-200.png");
    File.Delete(asset);
    var repair = new ProviderDeploymentStorage(adapter);
    Check(!await repair.InspectWorkAtStartupAsync());
    Check(!repair.WorkReady);
    await repair.PrepareWorkAtStartupAsync();
    Check(repair.WorkReady);
    Check(File.ReadAllText(executable) == "test-program");
    Check(File.ReadAllText(asset) == "logo");
    Check((await ManifestXmlService.Read()).Single().Id == "preserved-feed");
    Check(File.ReadAllText(DeploymentFiles.Under(work, "Images\\user.png")) == "user-image");

    // 同版本号重建也必须在下次启动识别更新，不能仅依赖包版本号或沿用上个进程的结果。
    File.WriteAllText(Path.Combine(directory.Path, "package", "Resources", "FeedProvider", "FeedProvider.exe"),
        "updated-program");
    var updated = new ProviderDeploymentStorage(adapter);
    Check(!await updated.InspectWorkAtStartupAsync());
    await updated.PrepareWorkAtStartupAsync();
    Check(File.ReadAllText(executable) == "updated-program");
    Check((await ManifestXmlService.Read()).Single().Id == "preserved-feed");
}

static async Task RuntimeWorkVersionAsync()
{
    using var directory = new TestDirectory();
    CreateWorkspace(directory.Path);
    var adapter = new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(AppDataPaths.FeedProviderFolder));
    var initial = new ProviderDeploymentStorage(adapter);
    await initial.PrepareWorkAtStartupAsync();
    ProviderDeploymentPlan full = await initial.PlanAsync(false);
    await initial.StageAsync(full);
    Check((await adapter.PublishAsync(initial.CandidatePath, full)).Result.ExitCode == 0);

    // 从现有版本清单启动，验证运行时复用的是该对象，而非重新读取工作文件生成摘要。
    var storage = new ProviderDeploymentStorage(adapter);
    Check(await storage.InspectWorkAtStartupAsync());
    string work = AppDataPaths.PackageLocalFeedProviderFolder;
    string executable = DeploymentFiles.Under(work, "FeedProvider\\FeedProvider.exe");
    string asset = DeploymentFiles.Under(work, "Assets\\StoreLogo.scale-200.png");
    string metadata = DeploymentFiles.Under(work, DeploymentFiles.VersionFile);
    string image = DeploymentFiles.Under(work, "Images\\user.png");
    var feeds = new List<Feed>
    {
        new() { Id = "runtime-feed", Name = "运行时配置", Url = "https://example.com", ImagePath = "Images\\user.png" }
    };
    using (var lockedProgram = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.None))
    using (var lockedAsset = new FileStream(asset, FileMode.Open, FileAccess.Read, FileShare.None))
    using (var lockedMetadata = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        File.WriteAllText(image, "image-v1");
        await storage.SaveFeedsAsync(feeds);
        ProviderDeploymentPlan configuration = await storage.PlanAsync(true);
        Check(configuration.Scope == ProviderDeploymentScope.Configuration);
        Check(configuration.CopyPaths.Contains("AppxManifest.xml") && configuration.CopyPaths.Contains("Images\\user.png"));
        await storage.StageAsync(configuration);
        Check((await adapter.PublishAsync(storage.CandidatePath, configuration)).Result.ExitCode == 0);
        Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Current);

        File.WriteAllText(image, "image-v2");
        ProviderDeploymentPlan changedImage = await storage.PlanAsync(true);
        Check(changedImage.Scope == ProviderDeploymentScope.Configuration);
        Check(changedImage.CopyPaths.Count == 1 && changedImage.CopyPaths[0] == "Images\\user.png");
        await storage.StageAsync(changedImage);
        Check((await adapter.PublishAsync(storage.CandidatePath, changedImage)).Result.ExitCode == 0);
        Check(File.ReadAllText(DeploymentFiles.Under(AppDataPaths.FeedProviderFolder, "Images\\user.png")) == "image-v2");

        feeds[0].ImagePath = string.Empty;
        await storage.SaveFeedsAsync(feeds);
        ProviderDeploymentPlan removedImage = await storage.PlanAsync(true);
        Check(removedImage.Scope == ProviderDeploymentScope.Configuration);
        DeploymentVersion expected = DeploymentVersion.Read(DeploymentFiles.Under(work, ".deployment"))!;
        Check(!expected.Files.ContainsKey("Images\\user.png"));
        await storage.StageAsync(removedImage);
        Check((await adapter.PublishAsync(storage.CandidatePath, removedImage)).Result.ExitCode == 0);
    }

    // 工作程序在运行时漂移不触发重新校验或改变预期摘要；注册版本漂移仍由现有规划发现。
    File.WriteAllText(executable, "changed-at-runtime");
    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Current);
    File.WriteAllText(DeploymentFiles.Under(AppDataPaths.FeedProviderFolder, "FeedProvider\\FeedProvider.exe"), "damaged-registered");
    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Full);
}

static async Task CandidateValidationAsync()
{
    using var directory = new TestDirectory();
    CreateWorkspace(directory.Path);
    var adapter = new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(AppDataPaths.FeedProviderFolder));
    var storage = new ProviderDeploymentStorage(adapter);
    await storage.PrepareWorkAtStartupAsync();
    ProviderDeploymentPlan plan = await storage.PlanAsync(false);

    // 不再全量检查工作目录，但计划中的文件复制后仍须在候选中校验。
    File.WriteAllText(AppDataPaths.PackageLocalManifestPath, "<Package Changed='true' />");
    bool rejected = false;
    try
    {
        await storage.StageAsync(plan);
    }
    catch (InvalidDataException ex)
    {
        rejected = ex.Message.Contains("部署候选的文件内容已变化");
    }

    Check(rejected);
    Check(Directory.Exists(storage.CandidatePath));
    Check(!Directory.Exists(AppDataPaths.FeedProviderFolder));
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
    string versionPath = System.IO.Path.Combine(candidate, DeploymentFiles.VersionFile);
    ProviderDeploymentPlan fullPlan = version.Plan(await adapter.InspectAsync(versionPath));
    Check(fullPlan.Scope == ProviderDeploymentScope.Full);
    Check((await adapter.PublishAsync(candidate, fullPlan)).Result.ExitCode == 0);
    Check(!File.Exists(System.IO.Path.Combine(target, "FeedProvider", "old.dll")));
    Check(version.Plan(await adapter.InspectAsync(versionPath)).Scope == ProviderDeploymentScope.Current);
    File.WriteAllText(System.IO.Path.Combine(target, DeploymentFiles.VersionFile), "broken metadata");
    Check(version.Plan(await adapter.InspectAsync(versionPath)).Scope == ProviderDeploymentScope.Full);
    Check((await adapter.PublishAsync(candidate, fullPlan)).Result.ExitCode == 0);
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

    // 源文件缺失仍是复制失败；允许前面的文件已替换，但失败后不能更新版本元数据。
    string registeredVersion = File.ReadAllText(System.IO.Path.Combine(target, DeploymentFiles.VersionFile));
    DeploymentVersion.Capture(candidate, "version-2", paths).Save(candidate);
    File.Delete(DeploymentFiles.Under(candidate, "FeedProvider\\FeedProvider.exe"));
    ProviderPublicationAttempt copyFailure = await adapter.PublishAsync(candidate, fullPlan);
    Check(copyFailure.Result.ExitCode != 0 && copyFailure.Stage == DeploymentStage.Publishing);
    Check(copyFailure.RegistrationElapsedMilliseconds is null);
    Check(File.ReadAllText(System.IO.Path.Combine(target, DeploymentFiles.VersionFile)) == registeredVersion);
    Check(File.ReadAllText(DeploymentFiles.Under(target, "FeedProvider\\FeedProvider.exe")) == "data");
}

static async Task CombinedScriptAsync()
{
    bool duplicateRejected = false;
    try
    {
        _ = PowerShellScriptComposer.Compose(
            "DuplicateSteps.ps1",
            string.Empty,
            [
                new PowerShellScriptStep("First", "Write-Output 1", 20),
                new PowerShellScriptStep("Second", "Write-Output 2", 20)
            ]);
    }
    catch (ArgumentException)
    {
        duplicateRejected = true;
    }

    Check(duplicateRejected);

    using var directory = new TestDirectory();
    string candidate = Path.Combine(directory.Path, "candidate");
    string target = Path.Combine(directory.Path, "target");
    string marker = Path.Combine(directory.Path, "registered.txt");
    string[] paths = ["AppxManifest.xml", "FeedProvider\\FeedProvider.exe", "Images\\Default.png"];
    foreach (string path in paths)
    {
        string source = DeploymentFiles.Under(candidate, path);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, path == "AppxManifest.xml" ? "<Package />" : "content");
    }

    DeploymentVersion.Capture(candidate, "template", paths).Save(candidate);
    var plan = new ProviderDeploymentPlan(ProviderDeploymentScope.Full, paths);
    string manifest = DeploymentFiles.Under(target, "AppxManifest.xml");
    string registrationStub = $$"""
        function Add-AppxPackage {
            [CmdletBinding()]
            param(
                [switch]$Register,
                [switch]$ForceApplicationShutdown,
                [Parameter(Position = 0)][string]$Path
            )
            if (-not (Test-Path -LiteralPath (Join-Path $target '.deployment-version.xml'))) {
                throw 'Registration started before publication finished'
            }
            [IO.File]::WriteAllText({{PowerShellLiteral.Quote(marker)}}, $Path)
        }
        """;
    var executor = new SandboxExecutor(target, registrationStub);
    var adapter = new ProviderDeploymentPowerShellAdapter(executor);
    ProviderPublicationAttempt success = await adapter.PublishAndRegisterAsync(candidate, plan, manifest, default);
    Check(executor.Calls == 1 && success.Result.ExitCode == 0 && success.Stage == DeploymentStage.Registering);
    Check(success.PublicationElapsedMilliseconds >= 0 && success.RegistrationElapsedMilliseconds >= 0);
    Check(File.ReadAllText(marker) == manifest);

    string executable = DeploymentFiles.Under(target, "FeedProvider\\FeedProvider.exe");
    File.SetLastWriteTimeUtc(executable, DateTime.UtcNow.AddDays(-2));
    DateTime programTime = File.GetLastWriteTimeUtc(executable);
    File.WriteAllText(DeploymentFiles.Under(candidate, "AppxManifest.xml"), "<Package Version='2' />");
    DeploymentVersion.Capture(candidate, "template", paths).Save(candidate);
    var configuration = new ProviderDeploymentPlan(
        ProviderDeploymentScope.Configuration,
        ["AppxManifest.xml"]);
    // 组合脚本也必须允许程序文件被独占，确保发布片段没有恢复任何全量内容校验。
    using (var locked = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        ProviderPublicationAttempt configurationSuccess = await adapter.PublishAndRegisterAsync(
            candidate,
            configuration,
            manifest,
            default);
        Check(executor.Calls == 2 && configurationSuccess.Result.ExitCode == 0);
    }
    Check(File.GetLastWriteTimeUtc(executable) == programTime);

    File.Delete(marker);
    string registeredVersion = File.ReadAllText(DeploymentFiles.Under(target, DeploymentFiles.VersionFile));
    string registeredManifest = File.ReadAllText(manifest);
    File.WriteAllText(DeploymentFiles.Under(candidate, "AppxManifest.xml"), "<Package Version='3' />");
    DeploymentVersion.Capture(candidate, "template", paths).Save(candidate);
    using (var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        ProviderPublicationAttempt publishFailure = await adapter.PublishAndRegisterAsync(candidate, plan, manifest, default);
        Check(executor.Calls == 3 && publishFailure.Stage == DeploymentStage.Publishing);
        Check(publishFailure.Result.ExitCode == 20 && publishFailure.Result.Error.Contains("Step: Publishing"));
        Check(publishFailure.PublicationElapsedMilliseconds >= 0 && publishFailure.RegistrationElapsedMilliseconds is null);
    }
    Check(!File.Exists(marker));
    Check(File.ReadAllText(manifest) == registeredManifest);
    Check(File.ReadAllText(DeploymentFiles.Under(target, DeploymentFiles.VersionFile)) == registeredVersion);

    // 配置发布的文件范围约束仍须在复制前拒绝程序文件，不能因取消哈希检查而放宽。
    var invalidConfiguration = new ProviderDeploymentPlan(
        ProviderDeploymentScope.Configuration,
        ["FeedProvider\\FeedProvider.exe"]);
    ProviderPublicationAttempt invalidPlan = await adapter.PublishAndRegisterAsync(
        candidate,
        invalidConfiguration,
        manifest,
        default);
    Check(executor.Calls == 4 && invalidPlan.Stage == DeploymentStage.Publishing);
    Check(invalidPlan.Result.Error.Contains("Configuration deployment contains a program file"));
    Check(!File.Exists(marker));

    string failedRegistrationStub = """
        function Add-AppxPackage {
            [CmdletBinding()]
            param(
                [switch]$Register,
                [switch]$ForceApplicationShutdown,
                [Parameter(Position = 0)][string]$Path
            )
            throw '0x80073CFF simulated registration failure'
        }
        """;
    var failedExecutor = new SandboxExecutor(target, failedRegistrationStub);
    var failedAdapter = new ProviderDeploymentPowerShellAdapter(failedExecutor);
    ProviderPublicationAttempt registrationFailure = await failedAdapter.PublishAndRegisterAsync(
        candidate,
        plan,
        manifest,
        default);
    Check(failedExecutor.Calls == 1 && registrationFailure.Stage == DeploymentStage.Registering);
    Check(registrationFailure.Result.ExitCode == 21);
    Check(registrationFailure.Result.Error.Contains("0x80073CFF"));
    Check(registrationFailure.PublicationElapsedMilliseconds >= 0 && registrationFailure.RegistrationElapsedMilliseconds >= 0);
}

static async Task WorkspaceUpgradeAsync()
{
    using var directory = new TestDirectory();
    CreateWorkspace(directory.Path);
    string work = AppDataPaths.PackageLocalFeedProviderFolder;
    var storage = new ProviderDeploymentStorage(new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(AppDataPaths.FeedProviderFolder)));
    await storage.PrepareWorkAtStartupAsync();
    var feeds = new List<Feed> { new() { Id = "existing-user-feed", Name = "旧用户订阅", Url = "https://example.com", ImagePath = "Images\\user.png" } };
    File.WriteAllText(DeploymentFiles.Under(work, "Images\\user.png"), "user-image");
    await storage.SaveFeedsAsync(feeds);
    // 模拟升级前版本：只有旧 flag，运行时目录留有 CoreCLR 文件，没有新版本清单。
    File.Delete(DeploymentFiles.Under(work, DeploymentFiles.VersionFile));
    File.WriteAllText(DeploymentFiles.Under(work, ".first_run_complete"), "1.0.0.0");
    File.WriteAllText(DeploymentFiles.Under(work, "FeedProvider\\old.dll"), "obsolete");
    // 重新创建进程级存储实例模拟应用再次启动，运行期间不再探测工作版本漂移。
    storage = new ProviderDeploymentStorage(new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(AppDataPaths.FeedProviderFolder)));
    Check(!await storage.InspectWorkAtStartupAsync());
    await storage.PrepareWorkAtStartupAsync();
    Check((await ManifestXmlService.Read()).Single().Id == "existing-user-feed");
    Check(File.ReadAllText(DeploymentFiles.Under(work, "Images\\user.png")) == "user-image");
    Check(!File.Exists(DeploymentFiles.Under(work, "FeedProvider\\old.dll")));
    Check(await storage.InspectWorkAtStartupAsync());
    ProviderDeploymentPlan plan = await storage.PlanAsync(false);
    Check(plan.Scope == ProviderDeploymentScope.Full);
    await storage.StageAsync(plan);
    await storage.BeginAsync(false);
    await storage.RecordStageAsync(DeploymentStage.Publishing);
    // 使用同一磁盘数据重新创建实例，证明日志阶段能跨实例恢复。
    var reopenedAdapter = new ProviderDeploymentPowerShellAdapter(new SandboxExecutor(AppDataPaths.FeedProviderFolder));
    var reopened = new ProviderDeploymentStorage(reopenedAdapter);
    Check(reopened.HasTransaction && reopened.PendingStage == DeploymentStage.Publishing);
    Check((await reopenedAdapter.PublishAsync(reopened.CandidatePath, plan)).Result.ExitCode == 0);
    await reopened.CommitAsync();
    await reopened.FinishAsync();
    Check((await storage.PlanAsync(true)).Scope == ProviderDeploymentScope.Current);
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
    try { await storage.PrepareWorkAtStartupAsync(); throw new Exception("损坏配置未阻止准备。"); }
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
    Check(generatedScript.Content.Contains("icacls $policyPath /setowner $owner", StringComparison.Ordinal));
    Check(generatedScript.Content.Contains("icacls $policyDirectory /restore $aclBackup", StringComparison.Ordinal));
    Check(!generatedScript.Content.Contains("icacls $policyPath /restore $aclBackup", StringComparison.Ordinal));
    Check(
        generatedScript.Content.IndexOf("icacls $policyPath /setowner $owner", StringComparison.Ordinal) <
        generatedScript.Content.IndexOf("icacls $policyDirectory /restore $aclBackup", StringComparison.Ordinal));

    // 使用必定不存在的策略文件以普通权限执行，只验证脚本语法和临时错误回传，不修改系统文件。
    PowerShellResult executionResult = await new PowerShellProcessExecutor().ExecuteAsync(
        generatedScript with
        {
            RequiresElevation = false
        });
    Check(executionResult.ExitCode == 3);
    Check(executionResult.Error.Contains("File not found", StringComparison.Ordinal));
}

static async Task RegionPolicyPreservesBytesAsync()
{
    const string original = """
        {
          "policies": [
            { "guid": "other-policy", "defaultState": "disabled" },
            {
              "$comment": "中文注释与表情 📰 不应使字节偏移错位。",
              "guid": "{16d2b50e-fa7c-4bb1-ab17-01d766530b3b}",
              "defaultState": "disabled",
              "conditions": { "region": { "enabled": ["AT", "CN"], "disabled": [] } }
            }
          ]
        }
        """;
    int valueOffset = original.IndexOf("\"defaultState\": \"disabled\"", original.IndexOf("$comment", StringComparison.Ordinal), StringComparison.Ordinal)
        + "\"defaultState\": \"".Length;
    string expected = original[..valueOffset] + "enabled" + original[(valueOffset + "disabled".Length)..];

    using var directory = new TestDirectory();
    foreach (bool withBom in new[] { false, true })
    {
        foreach (string newLine in new[] { "\n", "\r\n" })
        {
            foreach (bool trailingNewLine in new[] { false, true })
            {
                string path = System.IO.Path.Combine(directory.Path, "policy.json");
                string marker = System.IO.Path.Combine(directory.Path, "permissions.txt");
                var encoding = new System.Text.UTF8Encoding(withBom, true);
                string suffix = trailingNewLine ? newLine : string.Empty;
                byte[] originalBytes = encoding.GetPreamble().Concat(encoding.GetBytes(original.ReplaceLineEndings(newLine) + suffix)).ToArray();
                byte[] expectedBytes = encoding.GetPreamble().Concat(encoding.GetBytes(expected.ReplaceLineEndings(newLine) + suffix)).ToArray();
                File.WriteAllBytes(path, originalBytes);
                File.Delete(marker);
                PowerShellScript script = await CreateRegionPolicySandboxScriptAsync(path, marker);
                var executor = new PowerShellProcessExecutor();

                PowerShellResult first = await executor.ExecuteAsync(script);
                if (first.ExitCode != 0)
                {
                    throw new Exception($"地区策略字节替换失败：{first.Error}");
                }

                Check(File.ReadAllBytes(path).SequenceEqual(expectedBytes));
                Check(expectedBytes.Length == originalBytes.Length - 1);
                Check(File.Exists(marker));

                // 第二次执行不进入权限操作，也不能通过重写相同内容伪装成幂等。
                File.Delete(marker);
                DateTime sentinel = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, sentinel);
                PowerShellResult second = await executor.ExecuteAsync(script);
                Check(second.ExitCode == 0);
                Check(File.ReadAllBytes(path).SequenceEqual(expectedBytes));
                Check(File.GetLastWriteTimeUtc(path) == sentinel);
                Check(!File.Exists(marker));
            }
        }
    }
}

static async Task RegionPolicyRejectsUnsafeEditsAsync()
{
    const string guid = "{16d2b50e-fa7c-4bb1-ab17-01d766530b3b}";
    string policy = "{\"guid\":\"" + guid + "\",\"defaultState\":\"disabled\"}";
    string valid = "{\"policies\":[" + policy + "]}";
    var encoding = new System.Text.UTF8Encoding(false, true);
    (string Name, byte[] Bytes, int ExitCode)[] cases =
    [
        ("目标缺失", encoding.GetBytes("{\"policies\":[]}"), 2),
        ("目标重复", encoding.GetBytes("{\"policies\":[" + policy + "," + policy + "]}"), 1),
        ("状态未知", encoding.GetBytes(valid.Replace("disabled", "unknown", StringComparison.Ordinal)), 1),
        ("字段缺失", encoding.GetBytes("{\"policies\":[{\"guid\":\"" + guid + "\"}]}"), 1),
        ("字段顺序变化", encoding.GetBytes("{\"policies\":[{\"defaultState\":\"disabled\",\"guid\":\"" + guid + "\"}]}"), 1),
        ("不跨越其他属性", encoding.GetBytes(valid.Replace("\",\"defaultState", "\",\"extra\":{},\"defaultState", StringComparison.Ordinal)), 1),
        ("文本定位歧义", encoding.GetBytes("{\"policies\":[" + policy + "],\"extra\":" + policy + "}"), 1),
        ("重复状态导致候选校验失败", encoding.GetBytes(valid.Replace("\"defaultState\":\"disabled\"", "\"defaultState\":\"disabled\",\"defaultState\":\"disabled\"", StringComparison.Ordinal)), 1),
        ("JSON 损坏", encoding.GetBytes(valid[..^1]), 1),
        ("UTF-8 损坏", encoding.GetBytes(valid).Concat(new byte[] { 0xFF }).ToArray(), 1),
        ("UTF-16 不自动转码", System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(valid)).ToArray(), 1)
    ];

    using var directory = new TestDirectory();
    string path = System.IO.Path.Combine(directory.Path, "policy.json");
    string marker = System.IO.Path.Combine(directory.Path, "permissions.txt");
    foreach (var test in cases)
    {
        File.WriteAllBytes(path, test.Bytes);
        File.Delete(marker);
        PowerShellScript script = await CreateRegionPolicySandboxScriptAsync(path, marker);
        PowerShellResult result = await new PowerShellProcessExecutor().ExecuteAsync(script);
        if (result.ExitCode != test.ExitCode)
        {
            throw new Exception($"地区策略未按预期拒绝{test.Name}，退出码={result.ExitCode}，诊断={result.Error}");
        }

        Check(!string.IsNullOrWhiteSpace(result.Error));
        Check(File.ReadAllBytes(path).SequenceEqual(test.Bytes));
        Check(!File.Exists(marker));
    }

    // 在权限阶段模拟外部文件更新，不能覆盖更新后的内容。
    File.WriteAllBytes(path, encoding.GetBytes(valid));
    byte[] conflictingBytes = encoding.GetBytes(valid + "\r\n");
    PowerShellScript conflictScript = await CreateRegionPolicySandboxScriptAsync(path, marker, conflictingBytes);
    PowerShellResult conflict = await new PowerShellProcessExecutor().ExecuteAsync(conflictScript);
    Check(conflict.ExitCode == 1);
    Check(conflict.Error.Contains("changed before writing", StringComparison.Ordinal));
    Check(File.ReadAllBytes(path).SequenceEqual(conflictingBytes));
}

static async Task<PowerShellScript> CreateRegionPolicySandboxScriptAsync(string path, string permissionMarker, byte[]? conflictingBytes = null)
{
    var capture = new CapturingExecutor();
    await new RegionPolicyPowerShellAdapter(capture).EnablePolicyAsync("policy.json", "{16d2b50e-fa7c-4bb1-ab17-01d766530b3b}");
    PowerShellScript generated = capture.Script ?? throw new Exception("未捕获地区策略脚本。");
    const string systemPathLine = "$policyPath = [System.IO.Path]::Combine([System.Environment]::SystemDirectory, $policyFileName)";
    Check(generated.Content.Contains(systemPathLine, StringComparison.Ordinal));
    Check(!generated.Content.Contains("ConvertTo-Json", StringComparison.Ordinal));
    Check(generated.Content.All(character => character <= 0x7F));

    // 只把固定系统路径改为测试临时文件，并遮蔽权限命令；内容处理仍执行真实生成脚本。
    string conflictAction = conflictingBytes is null
        ? "'mock attributes'"
        : $"[IO.File]::WriteAllBytes({PowerShellLiteral.Quote(path)}, [Convert]::FromBase64String({PowerShellLiteral.Quote(Convert.ToBase64String(conflictingBytes))}))";
    string permissionStubs = $$"""
        function icacls
        {
            [IO.File]::AppendAllText({{PowerShellLiteral.Quote(permissionMarker)}}, 'mock ACL')
        }
        function takeown
        {
            'mock owner'
        }
        function attrib
        {
            {{conflictAction}}
        }
        """;
    return generated with
    {
        RequiresElevation = false,
        Content = permissionStubs + Environment.NewLine + generated.Content.Replace(
            systemPathLine,
            "$policyPath = " + PowerShellLiteral.Quote(path),
            StringComparison.Ordinal)
    };
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
    Check(generatedScript.Content.Contains(AppxPackagePowerShellScript.CreateRegisterBody("C:\\test\\AppxManifest.xml"), StringComparison.Ordinal));
    Check(generatedScript.Timeout == TimeSpan.FromMinutes(4));
}

static async Task DeveloperModeScriptIntegrationAsync()
{
    // 用脚本函数完整遮蔽注册表和 AppX 命令；执行器取消提权，测试不会读取或改写真实注册表。
    var capture = new CapturingExecutor();
    await new DeveloperModePowerShellAdapter(capture).RegisterPackageAsync("C:\\test's folder\\AppxManifest.xml");
    PowerShellScript generated = capture.Script ?? throw new Exception("未捕获临时开发者模式脚本。");
    foreach (string originalValue in new[] { "$null", "0", "1" })
    {
        foreach (bool registrationFailure in new[] { false, true })
        {
            PowerShellResult result = await RunAsync(originalValue, registrationFailure, restoreFailure: false);
            if ((result.ExitCode == 0) != !registrationFailure)
            {
                throw new Exception($"临时开发者模式脚本结果异常，原值={originalValue}，注册失败={registrationFailure}，退出码={result.ExitCode}，诊断={result.Error}");
            }
            Check(result.Output.Contains("MockRestored", StringComparison.Ordinal));
            if (registrationFailure)
            {
                Check(result.Error.Contains("controlled-registration-failure", StringComparison.Ordinal));
            }
        }
    }

    PowerShellResult restoreOnly = await RunAsync("0", registrationFailure: false, restoreFailure: true);
    Check(restoreOnly.ExitCode != 0 && restoreOnly.Error.Contains("controlled-restore-failure", StringComparison.Ordinal));
    PowerShellResult both = await RunAsync("0", registrationFailure: true, restoreFailure: true);
    Check(both.ExitCode != 0 && both.Error.Contains("controlled-registration-failure", StringComparison.Ordinal));
    Check(both.Error.Contains("controlled-restore-failure", StringComparison.Ordinal));

    async Task<PowerShellResult> RunAsync(string originalValue, bool registrationFailure, bool restoreFailure)
    {
        string mocks = $$"""
            $script:mockOriginal = {{originalValue}}
            $script:mockValue = $script:mockOriginal
            $script:mockWrites = 0
            $script:mockRegistrationFailure = ${{registrationFailure.ToString().ToLowerInvariant()}}
            $script:mockRestoreFailure = ${{restoreFailure.ToString().ToLowerInvariant()}}
            function Test-Path
            {
                param($LiteralPath)
                return $true
            }
            function Get-Item
            {
                param($LiteralPath, $ErrorAction)
                $key = New-Object psobject
                $key | Add-Member ScriptMethod GetValue {
                    param($name, $default, $options)
                    return $script:mockValue
                }
                $key | Add-Member ScriptMethod GetValueKind {
                    param($name)
                    return 'DWord'
                }
                $key | Add-Member ScriptMethod GetValueNames {
                    if ($null -ne $script:mockValue)
                    {
                        return 'AllowDevelopmentWithoutDevLicense'
                    }
                }
                return $key
            }
            function Set-ItemProperty
            {
                param($LiteralPath, $Name, $Value, $Type, $ErrorAction)
                $script:mockWrites++
                if ($script:mockWrites -gt 1)
                {
                    if ($script:mockRestoreFailure)
                    {
                        throw 'controlled-restore-failure'
                    }
                    if ($Value -ne $script:mockOriginal -or $Type -ne 'DWord')
                    {
                        throw 'incorrect-original-value'
                    }
                    Write-Output 'MockRestored'
                }
                $script:mockValue = $Value
            }
            function Remove-ItemProperty
            {
                param($LiteralPath, $Name, $ErrorAction)
                if ($null -ne $script:mockOriginal)
                {
                    throw 'incorrect-removal'
                }
                $script:mockValue = $null
                Write-Output 'MockRestored'
            }
            function Add-AppxPackage
            {
                param([switch]$Register, [switch]$ForceApplicationShutdown, [Parameter(Position=0)]$Manifest)
                if ($script:mockValue -ne 1 -or $Manifest -ne "C:\test's folder\AppxManifest.xml")
                {
                    throw 'incorrect-registration-context'
                }
                if ($script:mockRegistrationFailure)
                {
                    throw 'controlled-registration-failure'
                }
            }
            """;
        return await new PowerShellProcessExecutor().ExecuteAsync(generated with
        {
            RequiresElevation = false,
            Content = mocks + Environment.NewLine + generated.Content
        });
    }
}

static async Task CurrentDocumentMaintenanceSkipsCleanupAsync()
{
    var storage = new FakeDocumentStorage
    {
        State = new ApplicationDocumentState("2.0.0.0", "2.0.0.0", true),
        DocumentPath = "C:\\private\\Documents\\Help\\en-US.html",
    };
    var service = new ApplicationDocumentService(storage);

    await service.MaintainAfterStartupAsync();

    Check(storage.ReplaceCalls == 0);
    Check(storage.CleanupCalls == 0);
    Check(storage.WriteStateCalls == 0);
}

static async Task LegacyDocumentSchemaDoesNotTriggerSynchronizationAsync()
{
    using var directory = new TestDirectory();
    AppDataPaths.TestRoot = directory.Path;
    var executor = new CapturingExecutor();
    var storage = new ApplicationDocumentStorage(
        System.IO.Path.Combine(directory.Path, "package", "Documents"),
        AppDataPaths.PackageLocalDocumentsFolder,
        AppDataPaths.PackageLocalDocumentsStatePath,
        AppDataPaths.LegacyPackageLocalHelpDocFolder,
        "2.0.0.0",
        new LegacyDocumentPowerShellAdapter(executor));
    var service = new ApplicationDocumentService(storage);
    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AppDataPaths.PackageLocalDocumentsStatePath)!);

    // 保留未完成的历史清理状态，并故意不创建包内文档源：若误判需要同步或清理，维护就会失败。
    // 旧属性的数值和可解析性均不应影响包版本判断，也不能让同版本启动主动重写状态文件。
    foreach (string schema in new[] { "1", "3", "invalid" })
    {
        string legacyXml = $"""
            <ApplicationDocuments PackageVersion="2.0.0.0" CatalogSchema="{schema}"
                LegacyCleanupAttemptedVersion="1.0.0.0" LegacyCleanupCompleted="false" />
            """;
        File.WriteAllText(AppDataPaths.PackageLocalDocumentsStatePath, legacyXml);
        Check(storage.ReadState() == new ApplicationDocumentState("2.0.0.0", "1.0.0.0", false));

        await service.MaintainAfterStartupAsync();

        Check(File.ReadAllText(AppDataPaths.PackageLocalDocumentsStatePath) == legacyXml);
        Check(!Directory.Exists(AppDataPaths.PackageLocalDocumentsFolder));
        Check(executor.Script is null);
    }
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
        State = new ApplicationDocumentState("1.0.0.0", "1.0.0.0", false),
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
        State = new ApplicationDocumentState("2.0.0.0", "2.0.0.0", true),
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

    var state = new ApplicationDocumentState("2.0.0.0", string.Empty, true);
    storage.WriteState(state);
    Check(storage.ReadState() == state);
    Check(XDocument.Load(AppDataPaths.PackageLocalDocumentsStatePath).Root!.Attribute("CatalogSchema") is null);
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
sealed class SandboxExecutor(string target, string? registrationStub = null) : IPowerShellExecutor
{
    public int Calls { get; private set; }

    public Task<PowerShellResult> ExecuteAsync(PowerShellScript script, CancellationToken cancellationToken = default)
    {
        const string original = "$target = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FeedCustomProvider'";
        if (!script.Content.Contains(original))
        {
            throw new InvalidOperationException("测试根目录替换失败，拒绝执行。");
        }

        Calls++;
        string content = script.Content.Replace(original, "$target = " + PowerShellLiteral.Quote(target));
        if (registrationStub is not null)
        {
            content = registrationStub + Environment.NewLine + content;
        }

        return new PowerShellProcessExecutor().ExecuteAsync(script with
        {
            Content = content
        }, cancellationToken);
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
    public string CandidatePath => "test-candidate";
    public bool WorkReady { get; set; } = true;
    public bool HasTransaction { get; set; }
    public DeploymentStage PendingStage { get; set; }
    public bool TransactionCommitted => PendingStage == DeploymentStage.Completed;
    public bool Current { get; set; }
    public bool ConfigurationOnly { get; set; }
    public ProviderDeploymentScope? StagedScope { get; private set; }
    public string? FailAt { get; set; }
    private void Record(string name) { trace.Add(name); if (name == FailAt) throw new IOException(name + " failed"); }
    public Task<bool> InspectWorkAtStartupAsync()
    {
        Record("InspectWork");
        return Task.FromResult(WorkReady);
    }
    public Task PrepareWorkAtStartupAsync()
    {
        Record("Prepare");
        WorkReady = true;
        return Task.CompletedTask;
    }
    public Task SaveFeedsAsync(List<Feed> feeds) { Record("Save"); return Task.CompletedTask; }
    public async Task<ProviderDeploymentPlan> PlanAsync(bool installed)
    {
        Record("Plan");
        // 保留可控的异步让步，验证锁覆盖整个部署用例而非仅同步执行的片段。
        await Task.Delay(10);
        ProviderDeploymentScope scope = installed && Current
            ? ProviderDeploymentScope.Current
            : installed && ConfigurationOnly
                ? ProviderDeploymentScope.Configuration
                : ProviderDeploymentScope.Full;
        IReadOnlyList<string> paths = scope == ProviderDeploymentScope.Current ? [] : ["AppxManifest.xml"];
        return new ProviderDeploymentPlan(scope, paths);
    }
    public Task StageAsync(ProviderDeploymentPlan plan)
    {
        StagedScope = plan.Scope;
        Record("Stage");
        return Task.CompletedTask;
    }
    public Task BeginAsync(bool installed) { Record("Begin"); HasTransaction = true; PendingStage = DeploymentStage.Staging; return Task.CompletedTask; }
    public Task RecordStageAsync(DeploymentStage stage) { PendingStage = stage; return Task.CompletedTask; }
    public Task CommitAsync() { Record("Commit"); PendingStage = DeploymentStage.Completed; return Task.CompletedTask; }
    public Task FinishAsync() { Record("Finish"); HasTransaction = false; return Task.CompletedTask; }
    public Task<ImageCleanupResult> CleanImagesAsync(IReadOnlyCollection<string> draftImages, bool installed)
    { Record("Clean"); return Task.FromResult(new ImageCleanupResult(0, [])); }
}

sealed class FakePlatform(List<string> trace) : IProviderDeploymentPlatform
{
    public bool Installed { get; set; } = true;
    public bool FailQuery { get; set; }
    public bool FailRemove { get; set; }
    public bool FailPublish { get; set; }
    public bool RegisterVisible { get; set; } = true;
    public ProviderDeploymentScope? PublishedScope { get; private set; }
    public ProviderRegistrationStatus RegisterStatus { get; set; } = ProviderRegistrationStatus.Success;
    public ProviderRegistrationStatus TemporaryRegisterStatus { get; set; } = ProviderRegistrationStatus.Success;
    public DeveloperModeState DeveloperMode { get; set; } = DeveloperModeState.Enabled;
    public DeveloperModeState ReadDeveloperModeState()
    {
        trace.Add("DeveloperMode");
        return DeveloperMode;
    }
    public Task<bool> IsInstalledAsync(CancellationToken token)
    {
        trace.Add("Query");
        if (FailQuery)
        {
            throw new IOException("query failed");
        }

        return Task.FromResult(Installed);
    }
    public Task RemoveAsync(CancellationToken token)
    {
        trace.Add("Remove");
        if (FailRemove)
        {
            throw new IOException("remove failed");
        }

        Installed = false;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token)
    {
        trace.Add("Stop");
        return Task.CompletedTask;
    }
    public Task<ProviderRegistrationResult> PublishAndRegisterAsync(
        string candidatePath,
        ProviderDeploymentPlan plan,
        ProviderRegistrationMode mode,
        CancellationToken token)
    {
        PublishedScope = plan.Scope;
        trace.Add("Publish");
        if (FailPublish)
        {
            throw new IOException("publish failed");
        }

        return CompleteRegistration(mode);
    }

    public Task<ProviderRegistrationResult> RegisterWithTemporaryDeveloperModeAsync(CancellationToken token)
    {
        return CompleteRegistration(ProviderRegistrationMode.TemporaryDeveloperMode);
    }

    private Task<ProviderRegistrationResult> CompleteRegistration(ProviderRegistrationMode mode)
    {
        trace.Add("Register");
        trace.Add("Register:" + mode);
        ProviderRegistrationStatus status = mode == ProviderRegistrationMode.Normal ? RegisterStatus : TemporaryRegisterStatus;
        Installed = status == ProviderRegistrationStatus.Success && RegisterVisible;
        return Task.FromResult(new ProviderRegistrationResult(status, 0, string.Empty, string.Empty, "test-manifest")
        {
            Stage = DeploymentStage.Registering
        });
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
