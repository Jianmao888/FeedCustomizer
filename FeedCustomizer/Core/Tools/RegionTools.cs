using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Infrastructure.PowerShell;
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
        private static readonly IAppLog Log = AppLog.For(nameof(DeviceRegionTool));
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
                Log.Warning(ex, "读取 Windows 设备区域注册表值失败");
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
        private static readonly IAppLog Log = AppLog.For(nameof(RegionPolicyService));
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
            // 业务层只按适配器定义的稳定退出码分类，不解析本地化的 PowerShell 错误文本。
            PowerShellResult result = await PowerShellInfrastructure.RegionPolicy.EnablePolicyAsync(
                PolicyFileName,
                WidgetsThirdPartyFeedGuid,
                Path.Combine(AppDataPaths.PackageLocalLogPath, "RegionPolicyError"));

            LastDiagnostics =
                $"ExitCode = {result.ExitCode}" + Environment.NewLine +
                $"Output = {result.Output}" + Environment.NewLine +
                $"Error = {result.Error}";

            Log.Information(
                "地区策略脚本执行结束，退出码={ExitCode}，输出长度={OutputLength}，错误长度={ErrorLength}",
                result.ExitCode,
                result.Output.Length,
                result.Error.Length);

            // 原始诊断可能含当前账户或系统文件信息，只保留在附加调试器中，不写入持久化日志。
            Debug.WriteLine(result.ToString());
            Debug.WriteLine("[RegionPolicyService] 脚本执行结束：");
            Debug.WriteLine(LastDiagnostics);

            if (result.ExitCode == 0)
            {
                return RegionPolicyOperationResult.Success;
            }

            if (result.ExitCode == PowerShellExitCodes.ElevationCancelled)
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
                Log.Warning(ex, "检测第三方小组件源地区策略状态失败");
                return false;
            }
        }
    }
}
