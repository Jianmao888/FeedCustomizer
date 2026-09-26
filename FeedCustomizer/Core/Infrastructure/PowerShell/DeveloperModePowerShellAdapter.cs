using FeedCustomizer.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell;

/// <summary>单次提权临时开启开发者模式、注册 AppX 清单并恢复原值，文件发布不在此权限边界内。</summary>
internal sealed class DeveloperModePowerShellAdapter(IPowerShellExecutor executor)
{
    /// <summary>复用注册片段；临时设置及恢复必须处于同一提权进程，不使用应用预检查值恢复系统状态。</summary>
    internal Task<PowerShellResult> RegisterPackageAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        string body = $$"""
            $keyPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
            $valueName = 'AllowDevelopmentWithoutDevLicense'
            $original = $null
            $originalRead = $false
            $operationFailure = $null
            $restoreFailure = $null
            try
            {
                if (-not (Test-Path -LiteralPath $keyPath))
                {
                    New-Item -Path $keyPath -Force | Out-Null
                }

                $key = Get-Item -LiteralPath $keyPath -ErrorAction Stop
                $original = $key.GetValue($valueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($null -ne $original)
                {
                    $originalKind = $key.GetValueKind($valueName)
                }

                $originalRead = $true
                Set-ItemProperty -LiteralPath $keyPath -Name $valueName -Value 1 -Type DWord -ErrorAction Stop
                # 提权进程无法重定向标准流，注册输出必须写入执行器注入的临时诊断文件。
                & {
                    {{AppxPackagePowerShellScript.CreateRegisterBody(manifestPath)}}
                } | Out-String | Out-File -LiteralPath $outputPath -Encoding UTF8 -Append
            }
            catch
            {
                $operationFailure = $_
            }
            finally
            {
                if ($originalRead)
                {
                    try
                    {
                        if ($null -eq $original)
                        {
                            # 开启操作可能在写入前失败；只有存在该值时才删除，其他恢复异常必须报告。
                            $key = Get-Item -LiteralPath $keyPath -ErrorAction Stop
                            if ($key.GetValueNames() -contains $valueName)
                            {
                                Remove-ItemProperty -LiteralPath $keyPath -Name $valueName -ErrorAction Stop
                            }
                        }
                        else
                        {
                            Set-ItemProperty -LiteralPath $keyPath -Name $valueName -Value $original -Type $originalKind -ErrorAction Stop
                        }
                    }
                    catch
                    {
                        $restoreFailure = $_
                    }
                }
            }

            # 先完成恢复，再合并两种错误交给组合器，恢复失败不能覆盖最初的注册错误。
            $diagnostics = @()
            if ($null -ne $operationFailure)
            {
                $diagnostics += 'Developer mode operation failure type: ' + $operationFailure.Exception.GetType().FullName
                $diagnostics += 'Developer mode operation failure message: ' + $operationFailure.Exception.Message
                $diagnostics += 'Developer mode operation failure stack: ' + $operationFailure.ScriptStackTrace
                $diagnostics += ($operationFailure | Out-String)
            }

            if ($null -ne $restoreFailure)
            {
                $diagnostics += 'Developer mode restore failure type: ' + $restoreFailure.Exception.GetType().FullName
                $diagnostics += 'Developer mode restore failure message: ' + $restoreFailure.Exception.Message
                $diagnostics += 'Developer mode restore failure stack: ' + $restoreFailure.ScriptStackTrace
                $diagnostics += ($restoreFailure | Out-String)
            }

            if ($diagnostics.Count -gt 0)
            {
                throw ($diagnostics -join [Environment]::NewLine)
            }
            """;
        PowerShellScript script = PowerShellScriptComposer.Compose(
            "RegisterProviderWithDeveloperMode.ps1",
            "$ErrorActionPreference = 'Stop'",
            [new PowerShellScriptStep("TemporaryDeveloperModeRegistration", body, 1)],
            TimeSpan.FromMinutes(4)) with
        {
            RequiresElevation = true
        };
        return executor.ExecuteAsync(script, cancellationToken);
    }
}
