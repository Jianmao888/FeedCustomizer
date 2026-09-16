using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Infrastructure.PowerShell;
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
        /// 具体进程处理由 PowerShell 基础设施统一完成。
        /// </summary>
        public const int ElevationCancelledExitCode = PowerShellExitCodes.ElevationCancelled;

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
            // 注册表写入及 finally 恢复必须处于同一个提权脚本，避免 C# 与 PowerShell 两处维护状态机。
            return await PowerShellInfrastructure.DeveloperMode.RegisterPackageAsync(manifestPath);
        }
    }
}
