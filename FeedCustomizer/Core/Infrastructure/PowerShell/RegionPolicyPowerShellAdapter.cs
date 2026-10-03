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
                // 解析只用于确认目标，原始字节才是写回来源，避免重排系统配置或改变 BOM。
                @"    $originalBytes = [System.IO.File]::ReadAllBytes($policyPath)",
                @"    $bomLength = 0",
                @"    if ($originalBytes.Length -ge 3 -and $originalBytes[0] -eq 0xEF -and $originalBytes[1] -eq 0xBB -and $originalBytes[2] -eq 0xBF)",
                @"    {",
                @"        $bomLength = 3",
                @"    }",
                @"    $encoding = [System.Text.UTF8Encoding]::new($false, $true)",
                @"    $content = $encoding.GetString($originalBytes, $bomLength, $originalBytes.Length - $bomLength)",
                @"    Add-Diagnostics (""contentLength = "" + $content.Length)",
                @"    $jsonContent = $content | ConvertFrom-Json",
                @"    $policies = @($jsonContent.policies | Where-Object { $_.guid -eq $targetGuid })",
                @"    if ($policies.Count -eq 0)",
                @"    {",
                @"        Add-Diagnostics (""Target policy was not found: "" + $targetGuid)",
                @"        Write-DiagnosticsOutput",
                @"        exit 2",
                @"    }",
                @"    if ($policies.Count -ne 1)",
                @"    {",
                @"        throw 'The target policy is not unique.'",
                @"    }",
                @"    $policy = $policies[0]",
                @"    Add-Diagnostics (""Before edit defaultState: "" + $policy.defaultState)",
                @"    if ($policy.defaultState -ceq 'enabled')",
                @"    {",
                @"        'The target policy is already enabled; the file was not changed.' | Out-File -FilePath $outputPath -Encoding UTF8",
                @"        exit 0",
                @"    }",
                @"    if ($policy.defaultState -cne 'disabled')",
                @"    {",
                @"        throw 'The target policy has an unsupported defaultState.'",
                @"    }",
                // 只接受现有的相邻字段结构，匹配不得跨过其他属性或对象；系统改变结构时明确失败。
                @"    $pattern = '""guid""\s*:\s*""' + [regex]::Escape($targetGuid) + '""\s*,\s*""defaultState""\s*:\s*""(?<state>disabled)""'",
                @"    $matches = [regex]::Matches($content, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)",
                @"    if ($matches.Count -ne 1)",
                @"    {",
                @"        throw 'The target defaultState cannot be located unambiguously in the original text.'",
                @"    }",
                @"    $state = $matches[0].Groups['state']",
                // UTF-8 字节偏移不能直接使用字符下标，尤其是目标前面出现非 ASCII 注释时。
                @"    $byteOffset = $bomLength + $encoding.GetByteCount($content.Substring(0, $state.Index))",
                @"    $oldByteCount = $encoding.GetByteCount($state.Value)",
                @"    $replacement = $encoding.GetBytes('enabled')",
                @"    $suffixOffset = $byteOffset + $oldByteCount",
                @"    $suffixLength = $originalBytes.Length - $suffixOffset",
                @"    $updatedBytes = New-Object byte[] ($originalBytes.Length - $oldByteCount + $replacement.Length)",
                @"    [System.Buffer]::BlockCopy($originalBytes, 0, $updatedBytes, 0, $byteOffset)",
                @"    [System.Buffer]::BlockCopy($replacement, 0, $updatedBytes, $byteOffset, $replacement.Length)",
                @"    [System.Buffer]::BlockCopy($originalBytes, $suffixOffset, $updatedBytes, $byteOffset + $replacement.Length, $suffixLength)",
                @"    if ([Convert]::ToBase64String($originalBytes, 0, $byteOffset) -cne [Convert]::ToBase64String($updatedBytes, 0, $byteOffset) -or [Convert]::ToBase64String($originalBytes, $suffixOffset, $suffixLength) -cne [Convert]::ToBase64String($updatedBytes, $byteOffset + $replacement.Length, $suffixLength))",
                @"    {",
                @"        throw 'Content outside the target defaultState changed.'",
                @"    }",
                @"    $updatedContent = $encoding.GetString($updatedBytes, $bomLength, $updatedBytes.Length - $bomLength)",
                @"    $updatedJson = $updatedContent | ConvertFrom-Json",
                @"    $updatedPolicies = @($updatedJson.policies | Where-Object { $_.guid -eq $targetGuid })",
                @"    if ($updatedPolicies.Count -ne 1 -or $updatedPolicies[0].defaultState -cne 'enabled')",
                @"    {",
                @"        throw 'The candidate policy failed validation.'",
                @"    }",
                // 校验成功后才申请写入权限；已启用或无法定位时不接管文件所有权。
                @"    (icacls $policyPath /save $aclBackup /c 2>&1) | ForEach-Object { Add-Diagnostics (""icacls save: "" + $_.ToString()) }",
                @"    $aclChanged = $true",
                @"    (takeown /f $policyPath /a 2>&1) | ForEach-Object { Add-Diagnostics (""takeown: "" + $_.ToString()) }",
                @"    (icacls $policyPath /grant 'Administrators:(F)' 2>&1) | ForEach-Object { Add-Diagnostics (""icacls grant: "" + $_.ToString()) }",
                @"    (attrib -R $policyPath 2>&1) | ForEach-Object { Add-Diagnostics (""attrib: "" + $_.ToString()) }",
                @"    $currentBytes = [System.IO.File]::ReadAllBytes($policyPath)",
                @"    if ([Convert]::ToBase64String($currentBytes) -cne [Convert]::ToBase64String($originalBytes))",
                @"    {",
                @"        throw 'The policy file changed before writing.'",
                @"    }",
                @"    [System.IO.File]::WriteAllBytes($policyPath, $updatedBytes)",
                @"    $writtenBytes = [System.IO.File]::ReadAllBytes($policyPath)",
                @"    if ([Convert]::ToBase64String($writtenBytes) -cne [Convert]::ToBase64String($updatedBytes))",
                @"    {",
                @"        throw 'The written policy failed byte verification.'",
                @"    }",
                @"    Add-Diagnostics 'After edit defaultState: enabled'",
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
                @"    if ($aclChanged)",
                @"    {",
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
