using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell;

/// <summary>
/// 实际注册目录的文件边界。所有读取、发布、校验和清理都在包外 PowerShell 中进行，
/// 不用应用进程的 C# 文件视图判断真实 LocalAppData，以免受 MSIX 重定向影响。
/// </summary>
internal sealed class ProviderDeploymentPowerShellAdapter(IPowerShellExecutor executor)
{
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
    /// 发布经过验证的候选；旧程序不备份。每个文件先写临时文件再替换，版本清单最后写入。
    /// 协调器只在本方法成功后注册，失败则保持未注册状态，下一次重试可覆盖不完整文件。
    /// </summary>
    internal async Task PublishAsync(string candidate, ProviderDeploymentPlan plan)
    {
        string paths = string.Join(",", plan.CopyPaths.Select(PowerShellLiteral.Quote));
        string arguments = $"$candidate = {PowerShellLiteral.Quote(candidate)}\n" +
            $"$scope = {PowerShellLiteral.Quote(plan.Scope.ToString())}\n$copyPaths = @({paths})\n";
        await RunAsync("PublishProviderDeployment.ps1", arguments + """
            [xml]$version = Get-Content -LiteralPath (Resolve-Child $candidate '.deployment-version.xml') -Raw
            if ($version.DeploymentVersion.Schema -ne '1') { throw 'Unsupported deployment schema' }
            if ($scope -ne 'Full' -and $scope -ne 'Configuration') { throw 'Unsupported deployment scope' }
            $expectedFiles = @{}
            foreach ($file in $version.DeploymentVersion.File) {
                if ($expectedFiles.ContainsKey($file.Path)) { throw 'Duplicate expected path' }
                $expectedFiles[$file.Path] = [string]$file.Sha256
            }
            $copySet = @{}
            foreach ($relative in $copyPaths) {
                if (-not $expectedFiles.ContainsKey($relative) -or $copySet.ContainsKey($relative)) {
                    throw 'Invalid deployment copy path'
                }
                if ($scope -eq 'Configuration' -and $relative -ne 'AppxManifest.xml') {
                    $imageRoot = Resolve-Child $target 'Images'
                    $destination = Resolve-Child $target $relative
                    if (-not $destination.StartsWith($imageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'Configuration deployment contains a program file'
                    }
                }
                $copySet[$relative] = $true
                $source = Resolve-Child $candidate $relative
                if ((Get-ContentHash $source) -ne $expectedFiles[$relative]) { throw "Candidate changed: $source" }
            }
            if ($scope -eq 'Full' -and $copySet.Count -ne $expectedFiles.Count) {
                throw 'Full deployment is missing candidate files'
            }
            if ($scope -eq 'Configuration') {
                $deployedVersion = Resolve-Child $target '.deployment-version.xml'
                if (-not (Test-Path -LiteralPath $deployedVersion)) { throw 'Registered version metadata is missing' }
                [xml]$registered = Get-Content -LiteralPath $deployedVersion -Raw
                if ($registered.DeploymentVersion.Schema -ne '1' -or
                    $registered.DeploymentVersion.Template -ne $version.DeploymentVersion.Template) {
                    throw 'Registered template changed before configuration deployment'
                }
                # 卸载后再次核实不复制的文件，防止依赖已消失或被外部修改的程序文件。
                foreach ($file in $version.DeploymentVersion.File) {
                    if ($copySet.ContainsKey($file.Path)) { continue }
                    $path = Resolve-Child $target $file.Path
                    if (-not (Test-Path -LiteralPath $path) -or (Get-ContentHash $path) -ne $file.Sha256) {
                        throw "Unchanged file mismatch: $path"
                    }
                }
                $runtime = Resolve-Child $target 'FeedProvider'
                foreach ($path in Get-SafeFiles $runtime) {
                    $relative = $path.Substring($target.Length + 1)
                    if (-not $expectedFiles.ContainsKey($relative)) { throw "Unexpected runtime file: $path" }
                }
            }
            function Copy-Atomic([string]$source, [string]$destination) {
                $temporary = $destination + '.deployment-tmp'
                Assert-NotLink $temporary
                $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
                try {
                    # 占用只做三次有限重试；失败交给协调器，不清空部署目录。
                    for ($attempt = 0; $attempt -lt 3; $attempt++) {
                        try {
                            Copy-Item -LiteralPath $source -Destination $temporary -Force
                            Move-Item -LiteralPath $temporary -Destination $destination -Force
                            return
                        } catch {
                            if ($attempt -eq 2) { throw }
                            Start-Sleep -Milliseconds 150
                        }
                    }
                } finally {
                    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
                }
            }
            foreach ($relative in $copyPaths) {
                Copy-Atomic (Resolve-Child $candidate $relative) (Resolve-Child $target $relative)
            }
            # 只有 FeedProvider 子目录完全属于程序产物；Images 中的用户文件由引用清理负责。
            if ($scope -eq 'Full') {
                $runtime = Resolve-Child $target 'FeedProvider'
                foreach ($path in Get-SafeFiles $runtime) {
                    $relative = $path.Substring($target.Length + 1)
                    if (-not $expectedFiles.ContainsKey($relative)) { Remove-Item -LiteralPath $path -Force }
                }
            }
            # 发布后再次校验；目录内其他历史文件不被视为版本成功证据。
            foreach ($file in $version.DeploymentVersion.File) {
                $path = Resolve-Child $target $file.Path
                if ((Get-ContentHash $path) -ne $file.Sha256) { throw "Published file mismatch: $path" }
            }
            Copy-Atomic (Resolve-Child $candidate '.deployment-version.xml') (Resolve-Child $target '.deployment-version.xml')
            """);
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
