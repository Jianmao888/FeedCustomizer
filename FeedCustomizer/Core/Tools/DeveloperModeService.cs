using FeedCustomizer.Core.Models;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Tools
{
    /// <summary>
    /// 负责读取开发者模式状态，并在需要时通过单次管理员提权临时开启
    /// 开发者模式以注册源提供程序，注册完成后恢复原值。
    /// </summary>
    internal static class DeveloperModeService
    {
        private const string AppModelUnlockKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
        private const string AllowDevelopmentValueName = "AllowDevelopmentWithoutDevLicense";

        /// <summary>
        /// 用户取消 UAC 时 Process.Start 抛出的 Win32 错误码（ERROR_CANCELLED）。
        /// 保留以兼容现有调用方，实际定义收敛到 ElevatedScriptRunner。
        /// </summary>
        public const int ElevationCancelledExitCode = ElevatedScriptRunner.ElevationCancelledExitCode;

        public static bool IsDeveloperModeEnabled()
        {
            try
            {
                using RegistryKey? key = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry64).OpenSubKey(AppModelUnlockKeyPath);

                return key?.GetValue(AllowDevelopmentValueName) is int value && value != 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取开发者模式状态失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 以管理员权限运行一个临时脚本：记录开发者模式原值 → 置 1 →
        /// 注册应用包 → 无论成功失败都恢复原值。整个流程只弹一次 UAC。
        /// </summary>
        public static async Task<PowerShellResult> RegisterWithTemporaryDeveloperModeAsync(string manifestPath)
        {
            string escapedManifestPath = manifestPath.Replace("'", "''");
            string script = BuildScript(escapedManifestPath);
            return await ElevatedScriptRunner.RunAsync("RegisterFeedProvider.ps1", script);
        }

        private static string BuildScript(string escapedManifestPath)
        {
            string[] lines =
            [
                "$ErrorActionPreference = 'Stop'",
                "$keyPath = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock'",
                "$valueName = 'AllowDevelopmentWithoutDevLicense'",
                string.Empty,
                "$key = Get-Item -Path $keyPath -ErrorAction SilentlyContinue",
                "if ($null -eq $key) { New-Item -Path $keyPath -Force | Out-Null; $key = Get-Item -Path $keyPath }",
                "$original = $key.GetValue($valueName, $null)",
                string.Empty,
                "try {",
                "    Set-ItemProperty -Path $keyPath -Name $valueName -Value 1 -Type DWord",
                "    $output = Add-AppxPackage -Register -ForceApplicationShutdown -ErrorAction Stop '" + escapedManifestPath + "' 2>&1",
                "    $output | Out-String | Out-File -FilePath $outputPath -Encoding UTF8",
                "    exit 0",
                "}",
                "catch {",
                "    $_ | Out-String | Out-File -FilePath $errorPath -Encoding UTF8",
                "    exit 1",
                "}",
                "finally {",
                "    if ($null -eq $original) {",
                "        Remove-ItemProperty -Path $keyPath -Name $valueName -ErrorAction SilentlyContinue",
                "    } else {",
                "        Set-ItemProperty -Path $keyPath -Name $valueName -Value $original -Type DWord",
                "    }",
                "}"
            ];

            return string.Join(Environment.NewLine, lines);
        }
    }
}
