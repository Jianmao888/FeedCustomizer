using FeedCustomizer.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// 通过一次 UAC 提权临时开启开发者模式、注册 AppX 清单并恢复原值。
    /// </summary>
    internal sealed class DeveloperModePowerShellAdapter(IPowerShellExecutor executor)
    {
        /// <summary>
        /// 在单次提权会话中临时开启开发者模式并注册清单；脚本 finally 块恢复注册前状态，
        /// 以免一次注册请求永久改变用户的系统设置。
        /// </summary>
        internal Task<PowerShellResult> RegisterPackageAsync(
            string manifestPath,
            CancellationToken cancellationToken = default)
        {
            string[] lines =
            [
                "$ErrorActionPreference = 'Stop'",
                "$keyPath = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock'",
                "$valueName = 'AllowDevelopmentWithoutDevLicense'",
                "$original = $null",
                "$originalRead = $false",
                "try {",
                "    $key = Get-Item -Path $keyPath -ErrorAction SilentlyContinue",
                "    if ($null -eq $key) { New-Item -Path $keyPath -Force | Out-Null; $key = Get-Item -Path $keyPath -ErrorAction Stop }",
                "    $original = $key.GetValue($valueName, $null)",
                "    $originalRead = $true",
                "    Set-ItemProperty -Path $keyPath -Name $valueName -Value 1 -Type DWord",
                $"    $output = Add-AppxPackage -Register -ForceApplicationShutdown -ErrorAction Stop {PowerShellLiteral.Quote(manifestPath)} 2>&1",
                "    $output | Out-String | Out-File -FilePath $outputPath -Encoding UTF8",
                "    exit 0",
                "}",
                "catch {",
                "    ('Developer mode operation failure type: ' + $_.Exception.GetType().FullName) | Out-File -FilePath $errorPath -Encoding UTF8",
                "    ('Developer mode operation failure message: ' + $_.Exception.Message) | Out-File -FilePath $errorPath -Encoding UTF8 -Append",
                "    ('Developer mode operation failure stack: ' + $_.ScriptStackTrace) | Out-File -FilePath $errorPath -Encoding UTF8 -Append",
                "    ($_ | Out-String) | Out-File -FilePath $errorPath -Encoding UTF8 -Append",
                "    exit 1",
                "}",
                "finally {",
                "    if ($originalRead) {",
                "        try {",
                "            if ($null -eq $original) {",
                "                Remove-ItemProperty -Path $keyPath -Name $valueName -ErrorAction SilentlyContinue",
                "            } else {",
                "                Set-ItemProperty -Path $keyPath -Name $valueName -Value $original -Type DWord -ErrorAction Stop",
                "            }",
                "        }",
                "        catch {",
                "            ('Developer mode restore failure type: ' + $_.Exception.GetType().FullName) | Out-File -FilePath $errorPath -Encoding UTF8 -Append",
                "            ('Developer mode restore failure message: ' + $_.Exception.Message) | Out-File -FilePath $errorPath -Encoding UTF8 -Append",
                "            ('Developer mode restore failure stack: ' + $_.ScriptStackTrace) | Out-File -FilePath $errorPath -Encoding UTF8 -Append",
                "            throw",
                "        }",
                "    }",
                "}"
            ];

            return executor.ExecuteAsync(
                new PowerShellScript(
                    "RegisterProviderWithDeveloperMode.ps1",
                    string.Join(Environment.NewLine, lines),
                    RequiresElevation: true),
                cancellationToken);
        }
    }
}
