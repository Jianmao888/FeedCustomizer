using FeedCustomizer.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Tools
{
    /// <summary>
    /// 读取 Windows 设备设置区域（OOBE 阶段选择的国家或地区），
    /// 并判断该区域是否为欧盟成员国。
    /// </summary>
    public static partial class DeviceRegionTool
    {
        private const string DeviceRegionKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\DeviceRegion";
        private const string DeviceRegionValueName = "DeviceRegion";

        private const int HkeyLocalMachine = unchecked((int)0x80000002);
        private const int KeyRead = 0x20019;
        private const int KeyWow64_64Key = 0x0100;
        private const int GeoIso2 = 0x0004;

        /// <summary>
        /// 欧盟 27 个成员国的 ISO 3166-1 alpha-2 代码。
        /// </summary>
        private static readonly HashSet<string> EuropeanUnionCountryCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
            "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
            "PL", "PT", "RO", "SK", "SI", "ES", "SE"
        };

        /// <summary>
        /// 判断当前设备设置区域是否为非欧盟成员国。仅当能够读取并识别出区域代码时
        /// 才返回 true，读取失败时返回 false 以避免误报。
        /// </summary>
        public async static Task<bool> IsNonEuropeanUnionRegion()
        {
            int? geoId = GetDeviceRegionGeoId();
            if (geoId is null)
            {
                return false;
            }

            string? iso2 = GetIso2CountryCode(geoId.Value);
            if (string.IsNullOrEmpty(iso2))
            {
                return false;
            }

            return !EuropeanUnionCountryCodes.Contains(iso2);
        }

        private static int? GetDeviceRegionGeoId()
        {
            IntPtr key = IntPtr.Zero;
            try
            {
                int result = RegOpenKeyEx(HkeyLocalMachine, DeviceRegionKeyPath, 0, KeyRead | KeyWow64_64Key, out key);
                if (result != 0 || key == IntPtr.Zero)
                {
                    return null;
                }

                uint dataSize = sizeof(int);
                int value = 0;
                result = RegQueryValueEx(key, DeviceRegionValueName, IntPtr.Zero, out _, ref value, ref dataSize);
                return result == 0 ? value : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read DeviceRegion registry value: {ex.Message}");
                return null;
            }
            finally
            {
                if (key != IntPtr.Zero)
                {
                    _ = RegCloseKey(key);
                }
            }
        }

        private static unsafe string? GetIso2CountryCode(int geoId)
        {
            int required = GetGeoInfo(geoId, GeoIso2, null, 0, 0);
            if (required <= 0)
            {
                return null;
            }

            var buffer = new char[required];
            fixed (char* geoData = buffer)
            {
                if (GetGeoInfo(geoId, GeoIso2, geoData, buffer.Length, 0) <= 0)
                {
                    return null;
                }
            }

            int terminatorIndex = Array.IndexOf(buffer, '\0');
            int length = terminatorIndex >= 0 ? terminatorIndex : buffer.Length;
            return new string(buffer, 0, length);
        }

        [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial int RegOpenKeyEx(int hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

        [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial int RegQueryValueEx(IntPtr hKey, string lpValueName, IntPtr lpReserved, out int lpType, ref int lpData, ref uint lpcbData);

        [LibraryImport("advapi32.dll")]
        private static partial int RegCloseKey(IntPtr hKey);

        [LibraryImport("kernel32.dll", EntryPoint = "GetGeoInfoW")]
        private static unsafe partial int GetGeoInfo(int location, int geoType, char* geoData, int dataLength, int langId);
    }

    public enum RegionPolicyOperationResult
    {
        Success,
        Cancelled,
        PolicyNotFound,
        Failed
    }

    /// <summary>
    /// 修改系统集成服务区域策略文件，解除第三方 Widgets 源在非欧盟地区的显示限制。
    /// </summary>
    public static class RegionPolicyService
    {

        private const string PolicyFileName = "IntegratedServicesRegionPolicySet.json";
        private const string WidgetsThirdPartyFeedGuid = "{16d2b50e-fa7c-4bb1-ab17-01d766530b3b}";

        /// <summary>
        /// 最近一次执行的诊断信息，供上层展示或排查。
        /// </summary>
        public static string? LastDiagnostics { get; private set; }

        /// <summary>
        /// 将 “Third party feed is shown in Widgets.” 策略的 defaultState 改为 enabled。
        /// </summary>
        public static async Task<RegionPolicyOperationResult> EnableThirdPartyWidgetFeedAsync()
        {
            string script = BuildScript(
                PolicyFileName,
                WidgetsThirdPartyFeedGuid,
                Path.Combine(AppDataPaths.PackageLocalLogPath, "RegionPolicyError"));

            Debug.WriteLine("[RegionPolicyService] 开始执行提权脚本：");
            Debug.WriteLine(script);

            PowerShellResult result = await ElevatedScriptRunner.RunAsync(
                "EnableThirdPartyWidgetFeed.ps1",
                script);

            LastDiagnostics =
                $"ExitCode = {result.ExitCode}" + Environment.NewLine +
                $"Output = {result.Output}" + Environment.NewLine +
                $"Error = {result.Error}";

            Debug.WriteLine(result.ToString());

            Debug.WriteLine("[RegionPolicyService] 脚本执行结束：");
            Debug.WriteLine(LastDiagnostics);

            if (result.ExitCode == 0)
            {
                return RegionPolicyOperationResult.Success;
            }

            if (result.ExitCode == ElevatedScriptRunner.ElevationCancelledExitCode)
            {
                return RegionPolicyOperationResult.Cancelled;
            }

            if (result.ExitCode == 2)
            {
                return RegionPolicyOperationResult.PolicyNotFound;
            }

            return RegionPolicyOperationResult.Failed;
        }

        /// <summary>
        /// 在管理员 PowerShell 中对目标策略做定位替换，只改动 defaultState，
        /// 保留 $schema、$comment 与文件其余部分的原始格式。写回使用 UTF-8 无 BOM。
        /// !!!脚本里面绝对不能有中文，否则会有奇怪的Bug
        /// </summary>
        private static string BuildScript(string policyFileName, string targetGuid, string errorPath)
        {
            string[] lines =
            [
                @"$ErrorActionPreference = 'Stop'",
                @$"$policyFileName = '{policyFileName}'",
                @$"$targetGuid = '{targetGuid}'",
                @$"$errorPath = '{errorPath}'",
                @"$policyPath = [System.IO.Path]::Combine([System.Environment]::SystemDirectory, $policyFileName)",
                @"$script:diagnostics = New-Object System.Collections.Generic.List[string]",
                @"function Add-Diagnostics([string]$message) {",
                @"    [void]$script:diagnostics.Add($message)",
                @"}",
                @"function Write-ErrorOutput {",
                @"    $null = New-Item -ItemType Directory -Path (Split-Path $errorPath -Parent) -Force",
                @"    ($script:diagnostics -join [Environment]::NewLine) | Out-File -FilePath $errorPath -Encoding UTF8",
                @"}",
                @"Add-Diagnostics (""policyPath = "" + $policyPath)",
                @"Add-Diagnostics (""Now User = "" + [System.Security.Principal.WindowsIdentity]::GetCurrent().Name)",
                @"if (-not (Test-Path -LiteralPath $policyPath)) {",
                @"    Add-Diagnostics (""File not found: "" + $policyPath)",
                @"    Write-ErrorOutput",
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
                @"        Write-Host ""Not found GUID: $targetGuid"" -ForegroundColor Red",
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
                @"    Write-ErrorOutput",
                @"    exit 1",
                @"}",
                @"finally {",
                @"    if ($aclChanged) {",
                @"        try {",
                @"            if (Test-Path -LiteralPath $aclBackup) {",
                @"                (icacls $policyPath /restore $aclBackup 2>&1) | ForEach-Object { Add-Diagnostics (""icacls restore: "" + $_.ToString()) }",
                @"            }",
                @"            if (-not [string]::IsNullOrEmpty($owner)) {",
                @"                (icacls $policyPath /setowner $owner 2>&1) | ForEach-Object { Add-Diagnostics (""icacls setowner: "" + $_.ToString()) }",
                @"            }",
                @"            Remove-Item -LiteralPath $aclBackup -ErrorAction SilentlyContinue",
                @"        }",
                @"        catch {",
                @"            Add-Diagnostics (""Failed to restore ACL: "" + $_.Exception.Message)",
                @"        }",
                @"    }",
                @"}",
            ];

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// 非提权地检查系统策略文件中目标策略的 defaultState 是否为 enabled。
        /// 该方法仅做只读检测，失败时返回 false（即视为未解限）。
        /// </summary>
        public static async Task<bool> IsThirdPartyWidgetFeedEnabledAsync()
        {
            try
            {
                string policyPath = Path.Combine(Environment.SystemDirectory, PolicyFileName);
                if (!File.Exists(policyPath)) return false;

                string content = await File.ReadAllTextAsync(policyPath);
                string targetGuid = WidgetsThirdPartyFeedGuid;

                // 支持 guid 带或不带大括号的情况，且允许 defaultState 与 guid 之间有一定距离
                string pattern = "\"guid\"\\s*:\\s*\"\\{?" + Regex.Escape(targetGuid) + "\\}?\"[\\s\\S]{0,500}?\"defaultState\"\\s*:\\s*\"(?<state>enabled|disabled)\"";
                var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
                if (!match.Success) return false;

                string state = match.Groups["state"].Value;
                return string.Equals(state, "enabled", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RegionPolicyService: 检测策略状态失败: {ex.Message}");
                return false;
            }
        }
    }
}
