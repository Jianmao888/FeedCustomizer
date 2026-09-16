using FeedCustomizer.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>统一按版本清单校验实际部署，避免 C# 把重定向副本误认为真实注册副本。</summary>
    internal async Task<bool> IsCurrentAsync(string versionPath)
    {
        var result = await RunAsync("InspectProviderFiles.ps1", $"$versionPath = {PowerShellLiteral.Quote(versionPath)}\n" + """
            $deployedVersion = Resolve-Child $target '.deployment-version.xml'
            if (-not (Test-Path -LiteralPath $deployedVersion)) { 'False'; exit 0 }
            [xml]$expected = Get-Content -LiteralPath $versionPath -Raw
            $deployed = New-Object System.Xml.XmlDocument
            # 版本元数据损坏可以从当前候选重建；权限/IO 错误则继续向上报告，不能一律当作过期。
            try { $deployed.Load($deployedVersion) }
            catch [System.Xml.XmlException] { 'False'; exit 0 }
            if ($deployed.DeploymentVersion.Schema -ne '1' -or $deployed.DeploymentVersion.Template -ne $expected.DeploymentVersion.Template) { 'False'; exit 0 }
            foreach ($file in $expected.DeploymentVersion.File) {
                $path = Resolve-Child $target $file.Path
                if (-not (Test-Path -LiteralPath $path) -or (Get-ContentHash $path) -ne $file.Sha256) { 'False'; exit 0 }
            }
            'True'
            """);
        return string.Equals(result.Output.Trim(), "True", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 发布经过验证的候选；旧程序不备份。每个文件先写临时文件再替换，版本清单最后写入。
    /// 协调器只在本方法成功后注册，失败则保持未注册状态，下一次重试可覆盖不完整文件。
    /// </summary>
    internal async Task PublishAsync(string candidate)
    {
        await RunAsync("PublishProviderDeployment.ps1", $"$candidate = {PowerShellLiteral.Quote(candidate)}\n" + """
            [xml]$version = Get-Content -LiteralPath (Resolve-Child $candidate '.deployment-version.xml') -Raw
            if ($version.DeploymentVersion.Schema -ne '1') { throw 'Unsupported deployment schema' }
            # 先校验整个候选，避免复制到一半才发现缺文件或文件内容不匹配。
            foreach ($file in $version.DeploymentVersion.File) {
                $source = Resolve-Child $candidate $file.Path
                if ((Get-ContentHash $source) -ne $file.Sha256) { throw "Candidate changed: $source" }
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
            foreach ($file in $version.DeploymentVersion.File) {
                Copy-Atomic (Resolve-Child $candidate $file.Path) (Resolve-Child $target $file.Path)
            }
            # 只有 FeedProvider 子目录完全属于程序产物；Images 中的用户文件由引用清理负责。
            $runtime = Resolve-Child $target 'FeedProvider'
            foreach ($path in Get-SafeFiles $runtime) {
                $relative = $path.Substring($target.Length + 1)
                if (-not ($version.DeploymentVersion.File | Where-Object { $_.Path -eq $relative })) { Remove-Item -LiteralPath $path -Force }
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
