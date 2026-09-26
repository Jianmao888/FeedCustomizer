using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell;

/// <summary>
/// 实际注册目录的文件边界。所有读取、发布、校验和清理都在包外 PowerShell 中进行，
/// 不用应用进程的 C# 文件视图判断真实 LocalAppData，以免受 MSIX 重定向影响。
/// </summary>
internal sealed class ProviderDeploymentPowerShellAdapter(IPowerShellExecutor executor)
{
    private const int PublishFailureExitCode = 20;
    private const int RegisterFailureExitCode = 21;

    // 根目录在包外进程内计算，调用方只能提供候选路径，不能指定任意递归删除目标。
    private const string Prelude = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $target = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FeedCustomProvider'
        # 直接使用 .NET 哈希，避免依赖宿主继承的 PSModulePath 是否包含 Get-FileHash 模块。
        function Get-ContentHash([string]$path) {
            $algorithm = [Security.Cryptography.SHA256]::Create()
            $stream = [IO.File]::OpenRead($path)
            try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $algorithm.Dispose() }
        }
        function Assert-NotLink([string]$path) {
            if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Reparse point is not allowed: $path"
            }
        }
        function Resolve-Child([string]$root, [string]$relative) {
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or [string]::IsNullOrWhiteSpace($relative)) { throw 'Invalid relative path' }
            $root = [IO.Path]::GetFullPath($root).TrimEnd('\')
            $path = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative))
            if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escaped deployment root' }
            Assert-NotLink $root
            $current = $root
            foreach ($part in $path.Substring($root.Length + 1).Split('\')) {
                $current = Join-Path $current $part
                Assert-NotLink $current
            }
            return $path
        }
        function Get-SafeFiles([string]$folder) {
            Assert-NotLink $folder
            if (-not (Test-Path -LiteralPath $folder)) { return }
            foreach ($item in Get-ChildItem -LiteralPath $folder -Force) {
                Assert-NotLink $item.FullName
                if ($item.PSIsContainer) { Get-SafeFiles $item.FullName } else { $item.FullName }
            }
        }
        Assert-NotLink $target
        """;

    /// <summary>在包外检查注册版本的元数据和实际文件；应用进程只接收差异，不直接读取注册目录。</summary>
    internal async Task<DeployedFileInspection> InspectAsync(string versionPath)
    {
        var result = await RunAsync("InspectProviderFiles.ps1", $"$versionPath = {PowerShellLiteral.Quote(versionPath)}\n" + """
            [xml]$expected = Get-Content -LiteralPath $versionPath -Raw
            if ($expected.DeploymentVersion.Schema -ne '1') { throw 'Unsupported expected deployment schema' }
            $deployedPath = Resolve-Child $target '.deployment-version.xml'
            $deployed = New-Object System.Xml.XmlDocument
            $metadataValid = Test-Path -LiteralPath $deployedPath
            if ($metadataValid) {
                # 仅损坏的版本 XML 可退回完整发布；读取权限或 IO 错误必须向上报告。
                try { $deployed.Load($deployedPath) }
                catch [System.Xml.XmlException] { $metadataValid = $false }
            }
            if ($metadataValid) {
                $metadataValid = $deployed.DeploymentVersion.Schema -eq '1' -and
                    -not [string]::IsNullOrWhiteSpace($deployed.DeploymentVersion.Template)
            }
            $templateMatches = $metadataValid -and
                $deployed.DeploymentVersion.Template -eq $expected.DeploymentVersion.Template
            $metadataChanged = New-Object 'System.Collections.Generic.List[string]'
            $contentChanged = New-Object 'System.Collections.Generic.List[string]'
            $unexpectedRuntime = $false
            if ($templateMatches) {
                $expectedFiles = @{}
                foreach ($file in $expected.DeploymentVersion.File) {
                    if ($expectedFiles.ContainsKey($file.Path)) { throw 'Duplicate expected path' }
                    $expectedFiles[$file.Path] = [string]$file.Sha256
                }
                $deployedFiles = @{}
                foreach ($file in $deployed.DeploymentVersion.File) {
                    $path = [string]$file.Path
                    $hash = [string]$file.Sha256
                    if ([string]::IsNullOrWhiteSpace($path) -or [string]::IsNullOrWhiteSpace($hash) -or
                        $deployedFiles.ContainsKey($path)) {
                        $metadataValid = $false
                        break
                    }
                    $deployedFiles[$path] = $hash
                }
                if ($metadataValid) {
                    foreach ($file in $expected.DeploymentVersion.File) {
                        if (-not $deployedFiles.ContainsKey($file.Path) -or $deployedFiles[$file.Path] -ne $file.Sha256) {
                            $metadataChanged.Add([string]$file.Path)
                        }
                        $path = Resolve-Child $target $file.Path
                        if (-not (Test-Path -LiteralPath $path) -or (Get-ContentHash $path) -ne $file.Sha256) {
                            $contentChanged.Add([string]$file.Path)
                        }
                    }
                    foreach ($path in $deployedFiles.Keys) {
                        if (-not $expectedFiles.ContainsKey($path)) { $metadataChanged.Add([string]$path) }
                    }
                    $runtime = Resolve-Child $target 'FeedProvider'
                    foreach ($path in Get-SafeFiles $runtime) {
                        $relative = $path.Substring($target.Length + 1)
                        if (-not $expectedFiles.ContainsKey($relative)) { $unexpectedRuntime = $true; break }
                    }
                }
            }
            [pscustomobject]@{
                MetadataValid = [bool]$metadataValid
                TemplateMatches = [bool]$templateMatches
                UnexpectedRuntime = [bool]$unexpectedRuntime
                MetadataChanged = @($metadataChanged.ToArray())
                ContentChanged = @($contentChanged.ToArray())
            } | ConvertTo-Json -Compress -Depth 3
            """);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement root = document.RootElement;
        return new DeployedFileInspection(
            root.GetProperty("MetadataValid").GetBoolean(),
            root.GetProperty("TemplateMatches").GetBoolean(),
            root.GetProperty("UnexpectedRuntime").GetBoolean(),
            ReadPaths(root, "MetadataChanged"),
            ReadPaths(root, "ContentChanged"));
    }

    private static string[] ReadPaths(JsonElement root, string propertyName)
    {
        return root.GetProperty(propertyName).EnumerateArray()
            .Select(item => item.GetString() ?? throw new InvalidDataException("注册版本检查包含空文件路径。"))
            .ToArray();
    }

    /// <summary>
    /// 发布 C# 已验证的候选，复用规划阶段的注册版本检查，不重复计算文件哈希。
    /// 每个文件先写临时文件再替换，复制成功后最后写入版本清单；旧程序不备份。
    /// 发布片段也供组合注册复用，确保单独执行与组合执行遵循同一套文件约束。
    /// </summary>
    /// <returns>包含退出码、诊断与发布耗时的结果；文件操作失败不伪装为成功，由调用方决定补偿。</returns>
    internal Task<ProviderPublicationAttempt> PublishAsync(
        string candidate,
        ProviderDeploymentPlan plan,
        CancellationToken token = default)
    {
        return ExecutePublicationAsync(candidate, plan, manifest: null, token);
    }

    /// <summary>文件发布和普通注册共用一次包外进程，分别记录耗时；提权重试仍由平台实现单独执行。</summary>
    internal Task<ProviderPublicationAttempt> PublishAndRegisterAsync(
        string candidate,
        ProviderDeploymentPlan plan,
        string manifest,
        CancellationToken token)
    {
        return ExecutePublicationAsync(candidate, plan, manifest, token);
    }

    /// <summary>单独发布和组合发布共用片段及诊断边界，区别仅在是否追加普通注册步骤。</summary>
    private async Task<ProviderPublicationAttempt> ExecutePublicationAsync(
        string candidate,
        ProviderDeploymentPlan plan,
        string? manifest,
        CancellationToken token)
    {
        var steps = new List<PowerShellScriptStep>
        {
            CreateTimedStep("Publishing", CreatePublishBody(candidate, plan), PublishFailureExitCode)
        };
        if (manifest is not null)
        {
            steps.Add(CreateTimedStep("Registering", AppxPackagePowerShellScript.CreateRegisterBody(manifest), RegisterFailureExitCode));
        }

        PowerShellScript script = PowerShellScriptComposer.Compose(
            manifest is null ? "PublishProviderDeployment.ps1" : "PublishAndRegisterProvider.ps1",
            Prelude,
            steps,
            TimeSpan.FromMinutes(4));

        Stopwatch stopwatch = Stopwatch.StartNew();
        PowerShellResult result = await executor.ExecuteAsync(script, token);
        DeploymentStage stage = manifest is not null && (result.ExitCode is 0 or RegisterFailureExitCode)
            ? DeploymentStage.Registering
            : DeploymentStage.Publishing;

        // 超时和进程被终止时无法知道脚本是否已经进入注册，只报告最后可确认的阶段。
        return new ProviderPublicationAttempt(stage, result, stopwatch.ElapsedMilliseconds,
            ReadStepDuration(result.Output, "Publishing"), ReadStepDuration(result.Output, "Registering"));
    }

    /// <summary>仅包装现有片段以记录耗时，finally 中输出诊断后仍由组合器处理原异常。</summary>
    private static PowerShellScriptStep CreateTimedStep(string name, string body, int failureExitCode)
    {
        string content = $$"""
            $providerStepTimer = [Diagnostics.Stopwatch]::StartNew()
            try
            {
                {{body}}
            }
            finally
            {
                $providerStepTimer.Stop()
                Write-Output ('ProviderStepTiming:{{name}}:' + $providerStepTimer.ElapsedMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture))
            }
            """;
        return new PowerShellScriptStep(name, content, failureExitCode);
    }

    /// <summary>未执行的步骤、启动失败或超时可能没有计时输出，缺失值不能解释为零耗时。</summary>
    private static long? ReadStepDuration(string output, string name)
    {
        string prefix = $"ProviderStepTiming:{name}:";
        foreach (string line in output.Split('\n'))
        {
            string value = line.Trim();
            if (value.StartsWith(prefix, StringComparison.Ordinal) &&
                long.TryParse(value[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long elapsed) &&
                elapsed >= 0)
            {
                return elapsed;
            }
        }

        return null;
    }

    private static string CreatePublishBody(string candidate, ProviderDeploymentPlan plan)
    {
        string paths = string.Join(",", plan.CopyPaths.Select(PowerShellLiteral.Quote));
        string arguments = $"$candidate = {PowerShellLiteral.Quote(candidate)}\n" +
            $"$scope = {PowerShellLiteral.Quote(plan.Scope.ToString())}\n$copyPaths = @({paths})\n";
        return arguments + """
            [xml]$version = Get-Content -LiteralPath (Resolve-Child $candidate '.deployment-version.xml') -Raw
            if ($version.DeploymentVersion.Schema -ne '1')
            {
                throw 'Unsupported deployment schema'
            }
            if ($scope -ne 'Full' -and $scope -ne 'Configuration')
            {
                throw 'Unsupported deployment scope'
            }
            $expectedFiles = @{}
            foreach ($file in $version.DeploymentVersion.File)
            {
                if ($expectedFiles.ContainsKey($file.Path))
                {
                    throw 'Duplicate expected path'
                }
                $expectedFiles[$file.Path] = $true
            }
            $copySet = @{}
            foreach ($relative in $copyPaths)
            {
                if (-not $expectedFiles.ContainsKey($relative) -or $copySet.ContainsKey($relative))
                {
                    throw 'Invalid deployment copy path'
                }
                if ($scope -eq 'Configuration' -and $relative -ne 'AppxManifest.xml')
                {
                    $imageRoot = Resolve-Child $target 'Images'
                    $destination = Resolve-Child $target $relative
                    if (-not $destination.StartsWith($imageRoot + '\', [StringComparison]::OrdinalIgnoreCase))
                    {
                        throw 'Configuration deployment contains a program file'
                    }
                }
                $copySet[$relative] = $true
            }
            if ($scope -eq 'Full' -and $copySet.Count -ne $expectedFiles.Count)
            {
                throw 'Full deployment is missing candidate files'
            }
            # 协调器的锁覆盖规划、候选校验到发布结束；此处只检查复制约束，不重复读取文件计算哈希。
            function Copy-Atomic([string]$source, [string]$destination)
            {
                $temporary = $destination + '.deployment-tmp'
                Assert-NotLink $temporary
                $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
                try
                {
                    # 占用只做三次有限重试；失败交给协调器，不清空部署目录。
                    for ($attempt = 0; $attempt -lt 3; $attempt++)
                    {
                        try
                        {
                            Copy-Item -LiteralPath $source -Destination $temporary -Force
                            Move-Item -LiteralPath $temporary -Destination $destination -Force
                            return
                        }
                        catch
                        {
                            if ($attempt -eq 2)
                            {
                                throw
                            }
                            Start-Sleep -Milliseconds 150
                        }
                    }
                }
                finally
                {
                    if (Test-Path -LiteralPath $temporary)
                    {
                        Remove-Item -LiteralPath $temporary -Force
                    }
                }
            }
            foreach ($relative in $copyPaths)
            {
                Copy-Atomic (Resolve-Child $candidate $relative) (Resolve-Child $target $relative)
            }
            # 只有 FeedProvider 子目录完全属于程序产物；Images 中的用户文件由引用清理负责。
            if ($scope -eq 'Full')
            {
                $runtime = Resolve-Child $target 'FeedProvider'
                foreach ($path in Get-SafeFiles $runtime)
                {
                    $relative = $path.Substring($target.Length + 1)
                    if (-not $expectedFiles.ContainsKey($relative))
                    {
                        Remove-Item -LiteralPath $path -Force
                    }
                }
            }
            # 文件操作成功即作为发布依据；失败会抛出异常，版本清单不会更新，组合器也不会继续注册。
            Copy-Atomic (Resolve-Child $candidate '.deployment-version.xml') (Resolve-Child $target '.deployment-version.xml')
            """;
    }

    /// <summary>注册副本仅可作为图片保留引用读取，绝不反向作为用户配置来源。</summary>
    internal async Task<string> ReadManifestAsync()
    {
        var result = await RunAsync("ReadDeployedManifest.ps1", """
            $manifest = Resolve-Child $target 'AppxManifest.xml'
            if (Test-Path -LiteralPath $manifest) { Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 }
            """);
        return result.Output;
    }

    /// <summary>真实目录清理使用与私有目录同一份引用集合，只清理 Images 下的普通文件。</summary>
    internal async Task<int> CleanImagesAsync(IEnumerable<string> retainedNames, DateTime cutoffUtc)
    {
        string names = string.Join(",", retainedNames.Select(PowerShellLiteral.Quote));
        var result = await RunAsync("CleanDeployedImages.ps1", $"$keep = @({names})\n$cutoff = [DateTime]::Parse({PowerShellLiteral.Quote(cutoffUtc.ToString("O"))}).ToUniversalTime()\n" + """
            $folder = Resolve-Child $target 'Images'
            $deleted = 0
            if (Test-Path -LiteralPath $folder) {
                foreach ($file in Get-ChildItem -LiteralPath $folder -File -Force) {
                    $path = Resolve-Child $folder $file.Name
                    if ($keep -notcontains $file.Name -and $file.LastWriteTimeUtc -lt $cutoff) {
                        Remove-Item -LiteralPath $path -Force
                        $deleted++
                    }
                }
            }
            $deleted
            """);
        return int.Parse(result.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<PowerShellResult> RunAsync(string name, string body)
    {
        var result = await executor.ExecuteAsync(new PowerShellScript(name, Prelude + Environment.NewLine + body));
        if (result.ExitCode != 0)
            throw new IOException($"{name}: ExitCode={result.ExitCode}; Error={result.Error}; Output={result.Output}");
        return result;
    }
}

/// <summary>复合脚本的执行结果及各片段耗时；缺失计时表示未确认，未确认中断按发布阶段恢复。</summary>
internal sealed record ProviderPublicationAttempt(
    DeploymentStage Stage,
    PowerShellResult Result,
    long ElapsedMilliseconds,
    long? PublicationElapsedMilliseconds,
    long? RegistrationElapsedMilliseconds);
