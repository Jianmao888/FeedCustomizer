using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
        public static bool IsNonEuropeanUnionRegion()
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
}
