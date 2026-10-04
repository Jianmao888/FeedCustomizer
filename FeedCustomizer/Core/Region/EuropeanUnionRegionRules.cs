using System;
using System.Collections.Frozen;
using System.Globalization;

namespace FeedCustomizer.Core.Region;

/// <summary>基于已识别国家代码判断欧盟成员身份，不读取 Windows 的设备区域设置。</summary>
internal static class EuropeanUnionRegionRules
{
    private static readonly FrozenSet<string> MemberCountryCodes = new[]
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
        "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
        "PL", "PT", "RO", "SK", "SI", "ES", "SE"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>未识别的区域返回未知，避免将空值、占位代码或无效输入视为非欧盟地区。</summary>
    internal static bool? IsNonEuropeanUnion(string? isoCountryCode)
    {
        if (string.IsNullOrWhiteSpace(isoCountryCode))
        {
            return null;
        }

        string countryCode = isoCountryCode.Trim().ToUpperInvariant();
        if (countryCode.Length != 2 ||
            countryCode[0] is < 'A' or > 'Z' ||
            countryCode[1] is < 'A' or > 'Z')
        {
            return null;
        }

        try
        {
            // Windows 地理接口可能返回占位代码；标准区域数据负责确认代码可识别，
            // 欧盟成员集合只负责分类，不把无法识别的地区变成确定的非欧盟结果。
            var region = new RegionInfo(countryCode);
            if (!string.Equals(region.TwoLetterISORegionName, countryCode, StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return !MemberCountryCodes.Contains(countryCode);
    }
}
