using System;
using System.Collections.Generic;
using System.IO;

namespace FeedCustomizer.Core.Documents;

/// <summary>
/// 随包文档的唯一目录描述。这里保存受控相对路径，避免 UI 或用户语言输入参与任意路径拼接。
/// </summary>
internal static class ApplicationDocumentCatalog
{
    // 新增随包文档时提升架构版本，确保旧安装在更新后会原子替换完整目录。
    internal const int SchemaVersion = 3;

    private static readonly IReadOnlyDictionary<ApplicationDocumentKind, DocumentDescriptor> Descriptors =
        new Dictionary<ApplicationDocumentKind, DocumentDescriptor>
        {
            [ApplicationDocumentKind.Help] = new(
                "Help",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en-US"] = "en-US.html",
                    ["zh-CN"] = "zh-CN.html",
                },
                "en-US"),
            [ApplicationDocumentKind.OpenSourceLicenses] = new(
                "OpenSourceLicenses",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en-US"] = "en-US.html",
                    ["zh-CN"] = "zh-CN.html",
                },
                "en-US"),
            [ApplicationDocumentKind.Privacy] = new(
                "Privacy",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en-US"] = "en-US.html",
                    ["zh-CN"] = "zh-CN.html",
                },
                "en-US"),
        };

    /// <summary>
    /// 按语言选择受控入口。当前文档没有对应翻译时固定回退英文，不用未验证的语言标签生成文件名。
    /// </summary>
    internal static string? GetRelativeEntry(ApplicationDocumentKind kind, string languageTag)
    {
        if (!Descriptors.TryGetValue(kind, out DocumentDescriptor? descriptor))
        {
            return null;
        }

        string selectedLanguage = descriptor.Entries.ContainsKey(languageTag)
            ? languageTag
            : descriptor.FallbackLanguage;
        return Path.Combine(descriptor.Folder, descriptor.Entries[selectedLanguage]);
    }

    /// <summary>同步后的最小有效性要求；缺少英文帮助会使整个候选被拒绝。</summary>
    internal static IReadOnlyList<string> RequiredEntries { get; } =
    [
        Path.Combine("Help", "en-US.html"),
        Path.Combine("Help", "winui3.png"),
        Path.Combine("OpenSourceLicenses", "en-US.html"),
        Path.Combine("OpenSourceLicenses", "zh-CN.html"),
        Path.Combine("Privacy", "en-US.html"),
        Path.Combine("Privacy", "zh-CN.html"),
    ];

    private sealed record DocumentDescriptor(
        string Folder,
        IReadOnlyDictionary<string, string> Entries,
        string FallbackLanguage);
}
