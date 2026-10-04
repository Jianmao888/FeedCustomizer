using FeedCustomizer.Core.Region;
using System;
using System.Text.Json;

namespace FeedCustomizer.Core.Infrastructure.Region;

/// <summary>只解析目标策略所属的 JSON 对象，不跨对象推断状态，也不修改系统文件。</summary>
internal static class RegionPolicyJsonParser
{
    /// <summary>只接受唯一目标及明确状态；缺失、重复属性、重复目标或损坏结构返回未知和诊断。</summary>
    internal static RegionPolicyStateResult Parse(string content, Guid targetPolicyId)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (!TryGetUniqueProperty(document.RootElement, "policies", out JsonElement policies) ||
                policies.ValueKind != JsonValueKind.Array)
            {
                return Unknown("MissingOrAmbiguousPolicies");
            }

            bool? state = null;
            bool found = false;
            foreach (JsonElement policy in policies.EnumerateArray())
            {
                if (policy.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                int guidPropertyCount = 0;
                bool isTarget = false;
                foreach (JsonProperty property in policy.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "guid", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    guidPropertyCount++;
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        Guid.TryParse(property.Value.GetString(), out Guid policyId) &&
                        policyId == targetPolicyId)
                    {
                        isTarget = true;
                    }
                }

                // 无关政策可以使用不同字段结构；只约束可识别为目标的对象，
                // 同时检查其全部身份字段，避免重复 guid 的最后值掩盖目标歧义。
                if (!isTarget)
                {
                    continue;
                }

                if (guidPropertyCount != 1)
                {
                    return Unknown("AmbiguousTargetPolicyGuid");
                }

                if (found)
                {
                    return Unknown("DuplicateTargetPolicy");
                }

                found = true;
                if (!TryGetUniqueProperty(policy, "defaultState", out JsonElement stateElement) ||
                    stateElement.ValueKind != JsonValueKind.String)
                {
                    return Unknown("MissingOrAmbiguousDefaultState");
                }

                string? value = stateElement.GetString();
                if (string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase))
                {
                    state = true;
                }
                else if (string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase))
                {
                    state = false;
                }
                else
                {
                    return Unknown("UnsupportedDefaultState");
                }
            }

            return found ? new RegionPolicyStateResult(state, string.Empty) : Unknown("TargetPolicyNotFound");
        }
        catch (JsonException ex)
        {
            // 不把系统配置原文带入诊断，只保留便于定位结构错误的行与字节位置。
            return Unknown($"InvalidJson; Line = {ex.LineNumber}; BytePosition = {ex.BytePositionInLine}");
        }
    }

    private static bool TryGetUniqueProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool found = false;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // JsonElement 的便捷查找会接受重复属性的最后一个值；这里必须拒绝歧义，
            // 否则同一目标可能同时宣称 enabled 与 disabled。
            if (found)
            {
                return false;
            }

            found = true;
            value = property.Value;
        }

        return found;
    }

    private static RegionPolicyStateResult Unknown(string error)
    {
        return new RegionPolicyStateResult(null, $"Operation = ReadRegionPolicy; Error = {error}");
    }
}
