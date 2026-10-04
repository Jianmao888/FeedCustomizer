using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Infrastructure.Region;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Region;

/// <summary>使用纯输入与替身覆盖区域规则、策略解析和用例串行化，不读取或修改机器设置。</summary>
internal static class RegionMigrationTests
{
    private static readonly Guid TargetPolicyId = new("16d2b50e-fa7c-4bb1-ab17-01d766530b3b");
    private static readonly Guid OtherPolicyId = new("85d05b2e-d070-4272-9bf7-dcf9a4357cd7");

    internal static IReadOnlyList<(string Name, Func<Task> Run)> Cases { get; } =
    [
        ("地区规则区分欧盟、非欧盟和未知区域", CountryClassificationAsync),
        ("地区策略解析兼容大小写、字段顺序和 GUID 花括号", PolicyIdentityFormatsAsync),
        ("地区策略解析不跨入其他策略对象读取状态", PolicyDoesNotCrossObjectsAsync),
        ("地区策略解析只约束目标而不校验无关政策字段", PolicyIgnoresUnrelatedSchemaAsync),
        ("地区策略解析拒绝重复目标和歧义属性", PolicyRejectsAmbiguityAsync),
        ("地区策略解析对缺失和损坏结构返回未知诊断", PolicyRejectsInvalidStructureAsync),
        ("地区服务缓存有效结果且解锁后立即刷新快照", ServiceCachesAndUpdatesAsync),
        ("地区服务未知结果可重试且保留保守提示", ServiceRetriesUnknownStateAsync),
        ("地区服务失败诊断属于本次操作且失效旧策略缓存", ServiceKeepsOperationDiagnosticsAsync),
        ("地区查询异常返回稳定诊断并允许后续重新检测", ServiceReadFailureDiagnosticsAsync),
        ("地区查询和解锁在服务内部串行", ServiceSerializesOperationsAsync),
        ("地区服务锁等待取消不遗留平台操作", ServiceCancelsLockWaitAsync),
        ("地区服务读取取消向上传递并释放串行锁", ServiceCancelsReadAsync),
        ("地区服务修改开始前取消不调用平台且保留已有快照", ServiceCancelsBeforeEnableAsync),
        ("地区服务活动解锁取消仍观察恢复且保持串行锁", ServiceDefersActiveEnableCancellationAsync),
        ("Windows 地区适配器使用稳定退出码和本次执行诊断", PlatformMapsOperationResultAsync)
    ];

    private static Task CountryClassificationAsync()
    {
        string[] memberCodes =
        [
            "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
            "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
            "PL", "PT", "RO", "SK", "SI", "ES", "SE"
        ];
        foreach (string code in memberCodes)
        {
            Check(EuropeanUnionRegionRules.IsNonEuropeanUnion(code) == false, $"欧盟成员被误判：{code}");
        }

        foreach (string code in new[] { "CN", "US", "GB", " cn ", "us" })
        {
            Check(EuropeanUnionRegionRules.IsNonEuropeanUnion(code) == true, $"非欧盟成员被误判：{code}");
        }

        foreach (string? code in new string?[] { null, "", " ", "USA", "??", "XX", "ZZ" })
        {
            Check(EuropeanUnionRegionRules.IsNonEuropeanUnion(code) is null, "无效区域被解释为确定身份。");
        }

        Check(!new RegionState(null, false, "unknown").ShouldShowWarning, "未知设备区域不应误报警告。");
        Check(new RegionState(true, null, "unknown").ShouldShowWarning, "未知策略不应误判为已解限。");
        Check(!new RegionState(false, null, "unknown").ShouldShowWarning, "欧盟设备不应显示地区限制警告。");
        return Task.CompletedTask;
    }

    private static Task PolicyIdentityFormatsAsync()
    {
        string[] policyIds = [TargetPolicyId.ToString("B"), TargetPolicyId.ToString("D").ToUpperInvariant()];
        foreach (string policyId in policyIds)
        {
            string enabled = $$"""
                {"policies":[{"defaultState":"EnAbLeD","guid":"{{policyId}}"}]}
                """;
            RegionPolicyStateResult result = RegionPolicyJsonParser.Parse(enabled, TargetPolicyId);
            Check(result.IsEnabled == true && result.Diagnostic.Length == 0, "合法策略未识别为启用。");
        }

        string disabled = $$"""
            {"POLICIES":[{"GUID":"{{TargetPolicyId:B}}","DEFAULTSTATE":"DISABLED"}]}
            """;
        Check(RegionPolicyJsonParser.Parse(disabled, TargetPolicyId).IsEnabled == false, "禁用策略解析错误。");
        return Task.CompletedTask;
    }

    private static Task PolicyDoesNotCrossObjectsAsync()
    {
        string json = $$"""
            {"policies":[{"guid":"{{TargetPolicyId:B}}"},{"guid":"{{OtherPolicyId:B}}","defaultState":"enabled"}]}
            """;
        RegionPolicyStateResult result = RegionPolicyJsonParser.Parse(json, TargetPolicyId);
        Check(result.IsEnabled is null, "不得用其他对象的状态补齐目标策略。");
        Check(result.Diagnostic.Contains("MissingOrAmbiguousDefaultState", StringComparison.Ordinal), "未知状态应保留缺失字段的原因。");
        return Task.CompletedTask;
    }

    private static Task PolicyRejectsAmbiguityAsync()
    {
        string[] inputs =
        [
            $$"""
            {"policies":[{"guid":"{{TargetPolicyId:B}}","defaultState":"enabled"},{"guid":"{{TargetPolicyId:D}}","defaultState":"disabled"}]}
            """,
            $$"""
            {"policies":[{"guid":"{{TargetPolicyId:B}}","defaultState":"enabled","defaultState":"disabled"}]}
            """,
            $$"""
            {"policies":[{"guid":"{{TargetPolicyId:B}}","GUID":"{{OtherPolicyId:B}}","defaultState":"enabled"}]}
            """,
            $$"""
            {"policies":[{"guid":"{{TargetPolicyId:B}}","defaultState":"enabled"}],"POLICIES":[]}
            """
        ];
        foreach (string input in inputs)
        {
            RegionPolicyStateResult result = RegionPolicyJsonParser.Parse(input, TargetPolicyId);
            Check(result.IsEnabled is null && result.Diagnostic.Length > 0, "歧义 JSON 不得判为启用。");
        }

        return Task.CompletedTask;
    }

    private static Task PolicyIgnoresUnrelatedSchemaAsync()
    {
        string json = $$"""
            {"policies":[{"unrelated":"other-schema"},{"guid":"unsupported-identity","defaultState":null},{"guid":"{{TargetPolicyId:B}}","defaultState":"enabled"}]}
            """;
        Check(RegionPolicyJsonParser.Parse(json, TargetPolicyId).IsEnabled == true, "无关政策的结构不应阻止唯一目标读取。");
        return Task.CompletedTask;
    }

    private static Task PolicyRejectsInvalidStructureAsync()
    {
        string[] inputs =
        [
            "broken-json",
            "{}",
            "[]",
            "{\"policies\":null}",
            "{\"policies\":[]}",
            "{\"policies\":[null]}",
            $$"""
            {"policies":[{"guid":"{{TargetPolicyId:B}}","defaultState":true}]}
            """,
            $$"""
            {"policies":[{"guid":"{{TargetPolicyId:B}}","defaultState":"unknown"}]}
            """,
            $$"""
            {"policies":[{"guid":"{{OtherPolicyId:B}}","defaultState":"enabled"}]}
            """
        ];
        foreach (string input in inputs)
        {
            RegionPolicyStateResult result = RegionPolicyJsonParser.Parse(input, TargetPolicyId);
            Check(result.IsEnabled is null && result.Diagnostic.Length > 0, "损坏或缺失的结构必须明确返回未知。");
        }

        return Task.CompletedTask;
    }

    private static async Task ServiceCachesAndUpdatesAsync()
    {
        var platform = new FakeRegionPlatform();
        var service = new RegionService(platform);
        RegionState before = await service.GetStateAsync();
        Check(before.ShouldShowWarning, "非欧盟地区的禁用策略应显示提示。");
        await service.GetStateAsync();
        Check(platform.DeviceReads == 1 && platform.PolicyReads == 1, "确定的状态应在窗口生命周期内复用。");

        RegionPolicyOperationResult operation = await service.EnablePolicyAsync();
        RegionState after = await service.GetStateAsync();
        Check(operation.Status == RegionPolicyOperationStatus.Success, "替身解锁应成功。");
        Check(after.IsPolicyEnabled == true && !after.ShouldShowWarning, "解锁成功后不得沿用旧警告。");
        Check(platform.PolicyReads == 1, "解锁成功的确定结果无需再次读取系统文件。");
    }

    private static async Task ServiceRetriesUnknownStateAsync()
    {
        var platform = new FakeRegionPlatform
        {
            PolicyHandler = _ => Task.FromResult(new RegionPolicyStateResult(null, "policy-first-failure"))
        };
        var service = new RegionService(platform);
        RegionState first = await service.GetStateAsync();
        Check(first.ShouldShowWarning && first.Diagnostic.Contains("policy-first-failure", StringComparison.Ordinal), "未知策略应保留保守提示与本次诊断。");
        platform.PolicyHandler = _ => Task.FromResult(new RegionPolicyStateResult(false, string.Empty));
        RegionState second = await service.GetStateAsync();
        Check(second.IsPolicyEnabled == false && second.Diagnostic.Length == 0, "后续成功不应带入先前诊断。");
        Check(platform.DeviceReads == 1 && platform.PolicyReads == 2, "只应重试未知的一侧。");

        var unknownDevice = new FakeRegionPlatform
        {
            DeviceHandler = _ => Task.FromResult(new DeviceRegionResult(null, "device-first-failure"))
        };
        var deviceService = new RegionService(unknownDevice);
        Check(!(await deviceService.GetStateAsync()).ShouldShowWarning, "未知设备区域不应误报警告。");
        unknownDevice.DeviceHandler = _ => Task.FromResult(new DeviceRegionResult("CN", string.Empty));
        Check((await deviceService.GetStateAsync()).ShouldShowWarning, "区域重新识别成功后应恢复正确提示。");
        Check(unknownDevice.DeviceReads == 2 && unknownDevice.PolicyReads == 1, "未知设备区域不得永久缓存。");
    }

    private static async Task ServiceKeepsOperationDiagnosticsAsync()
    {
        var operations = new Queue<RegionPolicyOperationResult>
        ([
            new RegionPolicyOperationResult(RegionPolicyOperationStatus.Failed, "first-operation"),
            new RegionPolicyOperationResult(RegionPolicyOperationStatus.Cancelled, "second-operation")
        ]);
        var platform = new FakeRegionPlatform
        {
            EnableHandler = _ => Task.FromResult(operations.Dequeue())
        };
        var service = new RegionService(platform);
        await service.GetStateAsync();
        RegionPolicyOperationResult first = await service.EnablePolicyAsync();
        RegionPolicyOperationResult second = await service.EnablePolicyAsync();
        Check(first.Diagnostic == "first-operation" && second.Diagnostic == "second-operation", "诊断应与每次返回结果绑定。");

        // 修改失败可能发生在文件已变化但权限恢复失败之后，下一次查询必须重新确认。
        platform.PolicyHandler = _ => Task.FromResult(new RegionPolicyStateResult(true, string.Empty));
        Check((await service.GetStateAsync()).IsPolicyEnabled == true, "修改失败后不应保留旧的禁用缓存。");
        Check(platform.PolicyReads == 2, "修改失败后应使策略缓存失效。");

        platform.EnableHandler = _ => throw new IOException("unexpected-operation");
        RegionPolicyOperationResult exceptional = await service.EnablePolicyAsync();
        Check(exceptional.Status == RegionPolicyOperationStatus.Failed, "非取消异常必须返回失败结果。");
        Check(exceptional.Diagnostic.Contains("IOException", StringComparison.Ordinal) &&
            exceptional.Diagnostic.Contains("HResult = 0x", StringComparison.Ordinal), "异常诊断应保留本次异常类型与错误码。");
        Check(!exceptional.Diagnostic.Contains("unexpected-operation", StringComparison.Ordinal), "诊断不得包含原始异常消息。");
    }

    private static async Task ServiceSerializesOperationsAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakeRegionPlatform
        {
            DeviceHandler = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new DeviceRegionResult("CN", string.Empty);
            }
        };
        var service = new RegionService(platform);
        Task<RegionState> initialRead = service.GetStateAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<RegionPolicyOperationResult> enable = service.EnablePolicyAsync();
        Task<RegionState> queuedRead = service.GetStateAsync();
        try
        {
            Check(platform.EnableCalls == 0 && !enable.IsCompleted && !queuedRead.IsCompleted, "查询中的串行锁必须覆盖解锁和其他查询。");
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(initialRead, enable, queuedRead).WaitAsync(TimeSpan.FromSeconds(5));
        Check(platform.DeviceReads == 1 && platform.PolicyReads == 1 && platform.EnableCalls == 1, "串行调用不应重复查询已知状态。");
        Check(!(await service.GetStateAsync()).ShouldShowWarning, "最终状态应反映已完成的解锁。");
    }

    private static async Task ServiceReadFailureDiagnosticsAsync()
    {
        var platform = new FakeRegionPlatform
        {
            DeviceHandler = _ => throw new IOException("do-not-leak-device"),
            PolicyHandler = _ => throw new InvalidDataException("do-not-leak-policy")
        };
        var service = new RegionService(platform);
        RegionState failure = await service.GetStateAsync();
        Check(failure.IsNonEuropeanUnion is null && failure.IsPolicyEnabled is null, "平台查询异常应保留为未知状态。");
        Check(failure.Diagnostic.Contains("IOException", StringComparison.Ordinal) &&
            failure.Diagnostic.Contains("InvalidDataException", StringComparison.Ordinal) &&
            failure.Diagnostic.Contains("HResult = 0x", StringComparison.Ordinal), "查询失败诊断应保留稳定类型和错误码。");
        Check(!failure.Diagnostic.Contains("do-not-leak", StringComparison.Ordinal), "查询诊断不得包含原始异常文本。");

        platform.DeviceHandler = _ => Task.FromResult(new DeviceRegionResult("CN", string.Empty));
        platform.PolicyHandler = _ => Task.FromResult(new RegionPolicyStateResult(false, string.Empty));
        RegionState recovered = await service.GetStateAsync();
        Check(recovered.ShouldShowWarning && recovered.Diagnostic.Length == 0, "查询异常不应阻止后续重新检测。");
        Check(platform.DeviceReads == 2 && platform.PolicyReads == 2, "异常结果不得永久缓存。");
    }

    private static async Task ServiceCancelsLockWaitAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakeRegionPlatform
        {
            EnableHandler = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new RegionPolicyOperationResult(RegionPolicyOperationStatus.Success, string.Empty);
            }
        };
        var service = new RegionService(platform);
        Task<RegionPolicyOperationResult> first = service.EnablePolicyAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        Task<RegionState> waiting = service.GetStateAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            await ExpectCancelledAsync(waiting);
            Check(platform.DeviceReads == 0 && platform.PolicyReads == 0, "锁等待取消后不得进入平台读取。");
        }
        finally
        {
            release.TrySetResult();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Check((await service.GetStateAsync()).IsPolicyEnabled == true, "等待者取消不应影响持锁者的结果。");
    }

    private static async Task ServiceCancelsReadAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakeRegionPlatform
        {
            DeviceHandler = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new DeviceRegionResult("CN", string.Empty);
            }
        };
        var service = new RegionService(platform);
        using var cancellation = new CancellationTokenSource();
        Task<RegionState> reading = service.GetStateAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await ExpectCancelledAsync(reading);
        platform.DeviceHandler = _ => Task.FromResult(new DeviceRegionResult("CN", string.Empty));
        RegionState recovered = await service.GetStateAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(recovered.ShouldShowWarning, "取消读取后应释放锁，允许下次重新检测。");
    }

    private static async Task PlatformMapsOperationResultAsync()
    {
        var executor = new FakePowerShellExecutor();
        var platform = new WindowsRegionPlatform(new RegionPolicyPowerShellAdapter(executor));
        (int Code, RegionPolicyOperationStatus Status)[] results =
        [
            (0, RegionPolicyOperationStatus.Success),
            (PowerShellExitCodes.ElevationCancelled, RegionPolicyOperationStatus.Cancelled),
            (2, RegionPolicyOperationStatus.PolicyNotFound),
            (3, RegionPolicyOperationStatus.Failed),
            (1, RegionPolicyOperationStatus.Failed)
        ];
        using var cancellation = new CancellationTokenSource();
        foreach ((int code, RegionPolicyOperationStatus status) in results)
        {
            executor.Result = new PowerShellResult(code, $"output-{code}", $"error-{code}");
            RegionPolicyOperationResult result = await platform.EnablePolicyAsync(cancellation.Token);
            Check(result.Status == status, $"退出码映射错误：{code}");
            Check(result.Diagnostic.Contains($"ExitCode = {code}", StringComparison.Ordinal), "返回结果应包含本次退出码。");
            Check(result.Diagnostic.Contains($"output-{code}", StringComparison.Ordinal) &&
                result.Diagnostic.Contains($"error-{code}", StringComparison.Ordinal), "本次执行的输出和错误应与结果绑定。");
            Check(!executor.LastToken.CanBeCanceled, "页面取消不得传入会终止关键修改进程的执行器。");
            Check(executor.LastScript?.RequiresElevation == true, "区域策略修改仍应复用提权边界。");
        }

        cancellation.Cancel();
        await ExpectCancelledAsync(platform.EnablePolicyAsync(cancellation.Token));
    }

    private static async Task ServiceDefersActiveEnableCancellationAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken platformToken = default;
        var platform = new FakeRegionPlatform
        {
            EnableHandler = async token =>
            {
                platformToken = token;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new RegionPolicyOperationResult(RegionPolicyOperationStatus.Success, string.Empty);
            }
        };
        var service = new RegionService(platform);
        await service.GetStateAsync();
        Check(!service.IsPolicyOperationRunning, "区域查询不应被解释为关键修改。");
        using var cancellation = new CancellationTokenSource();
        Task<RegionPolicyOperationResult> operation = service.EnablePolicyAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<RegionState> queuedRead = service.GetStateAsync();
        cancellation.Cancel();
        try
        {
            Check(!platformToken.CanBeCanceled, "页面取消不得传播到已开始的权限修改。");
            Check(service.IsPolicyOperationRunning, "页面取消后仍应报告权限修改正在恢复。");
            Check(!operation.IsCompleted && !queuedRead.IsCompleted, "页面取消不得提前释放关键操作的串行锁。");
        }
        finally
        {
            release.TrySetResult();
        }

        await ExpectCancelledAsync(operation);
        RegionState state = await queuedRead.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!service.IsPolicyOperationRunning, "平台恢复结束后应清除关键修改状态。");
        Check(state.IsPolicyEnabled == true && !state.ShouldShowWarning, "旧页面取消后仍应缓存完整观测的成功结果。");
        Check(platform.PolicyReads == 1 && platform.EnableCalls == 1, "页面取消不得触发重复检测或解锁。");
    }

    private static async Task ServiceCancelsBeforeEnableAsync()
    {
        var platform = new FakeRegionPlatform();
        var service = new RegionService(platform);
        await service.GetStateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectCancelledAsync(service.EnablePolicyAsync(cancellation.Token));
        Check(platform.EnableCalls == 0 && !service.IsPolicyOperationRunning, "修改前取消不应进入平台或留下运行标志。");
        Check((await service.GetStateAsync()).IsPolicyEnabled == false, "未开始的修改不应失效已知策略状态。");
        Check(platform.PolicyReads == 1, "未开始的修改无需重新读取系统策略。");
    }

    private static async Task ExpectCancelledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("调用方取消应向上传递 OperationCanceledException。");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeRegionPlatform : IRegionPlatform
    {
        internal int DeviceReads { get; private set; }
        internal int PolicyReads { get; private set; }
        internal int EnableCalls { get; private set; }
        internal Func<CancellationToken, Task<DeviceRegionResult>> DeviceHandler { get; set; } =
            _ => Task.FromResult(new DeviceRegionResult("CN", string.Empty));
        internal Func<CancellationToken, Task<RegionPolicyStateResult>> PolicyHandler { get; set; } =
            _ => Task.FromResult(new RegionPolicyStateResult(false, string.Empty));
        internal Func<CancellationToken, Task<RegionPolicyOperationResult>> EnableHandler { get; set; } =
            _ => Task.FromResult(new RegionPolicyOperationResult(RegionPolicyOperationStatus.Success, string.Empty));

        /// <summary>只执行测试配置的设备读取，不访问注册表。</summary>
        public Task<DeviceRegionResult> ReadDeviceRegionAsync(CancellationToken cancellationToken = default)
        {
            DeviceReads++;
            return DeviceHandler(cancellationToken);
        }

        /// <summary>只执行测试配置的策略查询，不读取系统文件。</summary>
        public Task<RegionPolicyStateResult> ReadPolicyStateAsync(CancellationToken cancellationToken = default)
        {
            PolicyReads++;
            return PolicyHandler(cancellationToken);
        }

        /// <summary>只执行测试配置的解锁结果，不启动真实提权进程。</summary>
        public Task<RegionPolicyOperationResult> EnablePolicyAsync(CancellationToken cancellationToken = default)
        {
            EnableCalls++;
            return EnableHandler(cancellationToken);
        }
    }

    private sealed class FakePowerShellExecutor : IPowerShellExecutor
    {
        internal PowerShellResult Result { get; set; } = new(0, string.Empty, string.Empty);
        internal PowerShellScript? LastScript { get; private set; }
        internal CancellationToken LastToken { get; private set; }

        /// <summary>捕获实际生成脚本和取消令牌，返回替身结果而不执行 PowerShell。</summary>
        public Task<PowerShellResult> ExecuteAsync(PowerShellScript script, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastScript = script;
            LastToken = cancellationToken;
            return Task.FromResult(Result);
        }
    }
}
