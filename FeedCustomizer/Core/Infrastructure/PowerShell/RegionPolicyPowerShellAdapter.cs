using FeedCustomizer.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// Windows 集成服务区域策略的提权 PowerShell 适配器。
    /// </summary>
    internal sealed class RegionPolicyPowerShellAdapter(IPowerShellExecutor executor)
    {
        /// <summary>
        /// 以管理员权限修改目标区域策略，并把稳定退出码和诊断交还给业务层分类。
        /// </summary>
        internal Task<PowerShellResult> EnablePolicyAsync(
            string policyFileName,
            string targetGuid,
            CancellationToken cancellationToken = default)
        {
            string script = BuildEnablePolicyScript(policyFileName, targetGuid);
            return executor.ExecuteAsync(
                new PowerShellScript(
                    "EnableThirdPartyWidgetFeed.ps1",
                    script,
                    RequiresElevation: true),
                cancellationToken);
        }

        /// <summary>
        /// 脚本必须只包含 ASCII 内容，避免 Windows PowerShell 读取临时脚本时受代码页影响。
        /// </summary>
        private static string BuildEnablePolicyScript(
            string policyFileName,
            string targetGuid)
        {
            string[] lines =
            [
                @"$ErrorActionPreference = 'Stop'",
                $"$policyFileName = {PowerShellLiteral.Quote(policyFileName)}",
                $"$targetGuid = {PowerShellLiteral.Quote(targetGuid)}",
                @"$policyPath = [System.IO.Path]::Combine([System.Environment]::SystemDirectory, $policyFileName)",
                @"$policyDirectory = [System.IO.Path]::GetDirectoryName($policyPath)",
                @"$script:diagnostics = New-Object System.Collections.Generic.List[string]",
                @"function Add-Diagnostics([string]$message) {",
                @"    [void]$script:diagnostics.Add($message)",
                @"}",
                @"function Write-DiagnosticsOutput {",
                @"    $diagnosticsText = $script:diagnostics -join [Environment]::NewLine",
                @"    $diagnosticsText | Out-File -FilePath $errorPath -Encoding UTF8",
                @"}",
                @"Add-Diagnostics (""policyPath = "" + $policyPath)",
                @"if (-not (Test-Path -LiteralPath $policyPath)) {",
                @"    Add-Diagnostics (""File not found: "" + $policyPath)",
                @"    Write-DiagnosticsOutput",
                @"    exit 3",
                @"}",
                @"$aclBackup = Join-Path $env:TEMP ('FeedCustomizer_acl_' + [guid]::NewGuid().ToString('N') + '.txt')",
                @"Add-Diagnostics (""aclBackup = "" + $aclBackup)",
                @"$owner = $null",
                @"try {",
                @"    $acl = Get-Acl -LiteralPath $policyPath",
                @"    $owner = $acl.Owner",
                @"    Add-Diagnostics (""owner = "" + $owner)",
                @"}",
                @"catch {",
                @"    Add-Diagnostics (""Failed to read the original owner: "" + $_.Exception.Message)",
                @"}",
                @"$aclChanged = $false",
                @"try {",
                @"    (icacls $policyPath /save $aclBackup /c 2>&1) | ForEach-Object { Add-Diagnostics (""icacls save: "" + $_.ToString()) }",
                @"    $aclChanged = $true",
                @"    (takeown /f $policyPath /a 2>&1) | ForEach-Object { Add-Diagnostics (""takeown: "" + $_.ToString()) }",
                @"    (icacls $policyPath /grant 'Administrators:(F)' 2>&1) | ForEach-Object { Add-Diagnostics (""icacls grant: "" + $_.ToString()) }",
                @"    (attrib -R $policyPath 2>&1) | ForEach-Object { Add-Diagnostics (""attrib: "" + $_.ToString()) }",
                @"    $content = [System.IO.File]::ReadAllText($policyPath)",
                @"    Add-Diagnostics (""contentLength = "" + $content.Length)",
                @"    $jsonContent = Get-Content $policyPath -Raw | ConvertFrom-Json",
                @"    $policy = $jsonContent.policies | Where-Object { $_.guid -eq $targetGuid }",
                @"    if ($policy) {",
                @"        Add-Diagnostics (""Find policy: "" + $($policy.'$comment'))",
                @"        Add-Diagnostics (""Before edit defaultState: "" + $($policy.defaultState))",
                @"        $policy.defaultState = ""enabled""",
                @"        Add-Diagnostics (""After edit defaultState: "" + $($policy.defaultState))",
                @"    } else {",
                @"        Add-Diagnostics (""Target policy was not found: "" + $targetGuid)",
                @"        Write-DiagnosticsOutput",
                @"        exit 2",
                @"    }",
                @"    $jsonOutput = $jsonContent | ConvertTo-Json -Depth 10",
                @"    [System.IO.File]::WriteAllText($policyPath, $jsonOutput, [System.Text.UTF8Encoding]::new($true))",
                @"    ""The regional restriction policy for third-party Widgets feed has been set to enabled."" | Out-File -FilePath $outputPath -Encoding UTF8",
                @"    exit 0",
                @"}",
                @"catch {",
                @"    Add-Diagnostics (""Error type: "" + $_.Exception.GetType().FullName)",
                @"    Add-Diagnostics (""Error message: "" + $_.Exception.Message)",
                @"    Add-Diagnostics (""Error Stack: "" + $_.ScriptStackTrace)",
                @"    Add-Diagnostics (""Error details: "" + ($_ | Out-String))",
                @"    Write-DiagnosticsOutput",
                @"    exit 1",
                @"}",
                @"finally {",
                @"    if ($aclChanged) {",
                @"        try {",
                @"            if (-not [string]::IsNullOrEmpty($owner)) {",
                @"                (icacls $policyPath /setowner $owner 2>&1) | ForEach-Object { Add-Diagnostics (""icacls setowner: "" + $_.ToString()) }",
                @"            }",
                @"            if (Test-Path -LiteralPath $aclBackup) {",
                @"                (icacls $policyDirectory /restore $aclBackup 2>&1) | ForEach-Object { Add-Diagnostics (""icacls restore: "" + $_.ToString()) }",
                @"            }",
                @"            Remove-Item -LiteralPath $aclBackup -ErrorAction SilentlyContinue",
                @"        }",
                @"        catch {",
                @"            Add-Diagnostics (""Failed to restore ACL: "" + $_.Exception.Message)",
                @"            Write-DiagnosticsOutput",
                @"            throw",
                @"        }",
                @"    }",
                @"}"
            ];

            return string.Join(Environment.NewLine, lines);
        }
    }
}
