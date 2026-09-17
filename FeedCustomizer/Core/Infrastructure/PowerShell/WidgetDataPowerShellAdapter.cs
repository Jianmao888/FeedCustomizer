using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.WidgetData;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell;

/// <summary>
/// 小组件 WebView Profile 的包外文件和进程适配器。
/// PowerShell 在标准用户上下文运行，以获得未受 MSIX AppData 视图重定向影响的实际路径。
/// </summary>
internal sealed class WidgetDataPowerShellAdapter(IPowerShellExecutor executor) : IWidgetDataResetPlatform
{
    private const int LockedExitCode = 4;

    /// <summary>
    /// 关闭当前用户拥有的 Widgets WebView 进程组，并清除唯一允许的 Profile 目录。
    /// </summary>
    public async Task<WidgetDataClearResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        PowerShellResult result = await executor.ExecuteAsync(
            new PowerShellScript(
                "ClearWidgetWebViewData.ps1",
                BuildClearScript(),
                Timeout: TimeSpan.FromSeconds(20)),
            cancellationToken);

        string diagnostics =
            $"ExitCode = {result.ExitCode}" + Environment.NewLine +
            $"Output = {result.Output}" + Environment.NewLine +
            $"Error = {result.Error}";

        return result.ExitCode switch
        {
            0 => new WidgetDataClearResult(WidgetDataClearStatus.Succeeded, diagnostics),
            LockedExitCode => new WidgetDataClearResult(WidgetDataClearStatus.Locked, diagnostics),
            _ => new WidgetDataClearResult(WidgetDataClearStatus.Failed, diagnostics)
        };
    }

    /// <summary>
    /// 脚本所有目录片段均为常量，并在删除前验证父目录、规范路径及重解析点。
    /// 结束进程后只作有限重试；若小组件立即重新启动并继续持锁，会返回稳定失败码而非无限等待。
    /// </summary>
    private static string BuildClearScript() => """
        $ErrorActionPreference = 'Stop'
        $sharedRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\Windows\SharedWebView\EBWebView')).TrimEnd('\')
        $profileName = 'WV2Profile_widgets'
        $target = [IO.Path]::GetFullPath((Join-Path $sharedRoot $profileName))
        $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $diagnostics = New-Object System.Collections.Generic.List[string]

        function Add-Diagnostic([string]$message)
        {
            [void]$diagnostics.Add($message)
        }

        function Assert-NotReparsePoint([string]$path)
        {
            if (Test-Path -LiteralPath $path)
            {
                $item = Get-Item -LiteralPath $path -Force
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
                {
                    throw "Reparse point is not allowed: $path"
                }
            }
        }

        function Remove-SafeTree([string]$path)
        {
            Assert-NotReparsePoint $path
            foreach ($item in @(Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop))
            {
                Assert-NotReparsePoint $item.FullName
                if ($item.PSIsContainer)
                {
                    Remove-SafeTree $item.FullName
                }
                else
                {
                    Remove-Item -LiteralPath $item.FullName -Force -ErrorAction Stop
                }
            }

            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        }

        function Is-CurrentUserProcess($process)
        {
            try
            {
                $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwner -ErrorAction Stop
                if ($owner.ReturnValue -ne 0)
                {
                    return $false
                }

                return (($owner.Domain + '\' + $owner.User) -ieq $currentUser)
            }
            catch
            {
                Add-Diagnostic ("Could not verify process owner for PID " + $process.ProcessId)
                return $false
            }
        }

        function Stop-WidgetWebViewProcesses
        {
            $webViewPathPattern = '(?i)--user-data-dir(?:=|\s+)"?' + [regex]::Escape($sharedRoot) + '(?:"|\s|$)'
            $allProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop)
            $candidates = New-Object System.Collections.Generic.List[object]

            foreach ($process in $allProcesses)
            {
                if (-not (Is-CurrentUserProcess $process))
                {
                    continue
                }

                if ($process.Name -ieq 'WidgetBoard.exe')
                {
                    $package = Get-AppxPackage -Name 'MicrosoftWindows.Client.WebExperience' -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($null -ne $package)
                    {
                        $expectedPath = [IO.Path]::GetFullPath((Join-Path $package.InstallLocation 'WidgetBoard.exe'))
                        if ($null -ne $process.ExecutablePath -and ([IO.Path]::GetFullPath($process.ExecutablePath) -ieq $expectedPath))
                        {
                            [void]$candidates.Add($process)
                        }
                    }

                    continue
                }

                if ($process.Name -ieq 'msedgewebview2.exe' -and
                    $null -ne $process.CommandLine -and
                    $process.CommandLine -match $webViewPathPattern)
                {
                    [void]$candidates.Add($process)
                }
            }

            $sortedCandidates = $candidates | Sort-Object @{ Expression = {
                if ($_.Name -ieq 'WidgetBoard.exe')
                {
                    return 0
                }

                return 1
            } }, ProcessId

            foreach ($process in $sortedCandidates)
            {
                try
                {
                    Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
                    Add-Diagnostic ("Stopped PID " + $process.ProcessId)
                }
                catch
                {
                    Add-Diagnostic ("Could not stop PID " + $process.ProcessId + ': ' + $_.Exception.GetType().Name)
                }
            }
        }

        try
        {
            if ([IO.Path]::GetDirectoryName($target) -ne $sharedRoot)
            {
                throw 'The target directory escaped the fixed SharedWebView root.'
            }

            Assert-NotReparsePoint $sharedRoot
            Assert-NotReparsePoint $target
            if (-not (Test-Path -LiteralPath $target))
            {
                'Widget data profile does not exist.' | Out-File -FilePath $outputPath -Encoding UTF8
                exit 0
            }

            for ($attempt = 1; $attempt -le 3; $attempt++)
            {
                Stop-WidgetWebViewProcesses
                Start-Sleep -Milliseconds (200 * $attempt)

                $quarantine = Join-Path $sharedRoot ('.WV2Profile_widgets.clearing-' + [guid]::NewGuid().ToString('N'))
                try
                {
                    Move-Item -LiteralPath $target -Destination $quarantine -ErrorAction Stop
                    Add-Diagnostic ("Profile was moved after attempt " + $attempt)
                    Remove-SafeTree $quarantine
                    'Widget data profile was cleared.' | Out-File -FilePath $outputPath -Encoding UTF8
                    exit 0
                }
                catch [System.IO.IOException]
                {
                    Add-Diagnostic ("Profile remained locked after attempt " + $attempt)
                }
            }

            ($diagnostics -join [Environment]::NewLine) | Out-File -FilePath $errorPath -Encoding UTF8
            exit 4
        }
        catch
        {
            Add-Diagnostic ("Failure type: " + $_.Exception.GetType().FullName)
            Add-Diagnostic ("Failure message: " + $_.Exception.Message)
            ($diagnostics -join [Environment]::NewLine) | Out-File -FilePath $errorPath -Encoding UTF8
            exit 1
        }
        """;
}
