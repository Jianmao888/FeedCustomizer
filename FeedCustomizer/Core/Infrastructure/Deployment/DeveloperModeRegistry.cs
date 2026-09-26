using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Infrastructure.Logging;
using Microsoft.Win32;
using System;
using System.Runtime.Versioning;

namespace FeedCustomizer.Core.Infrastructure.Deployment;

/// <summary>通过注册表 API 读取开发者模式开关，仅作注册路径预判，不保证系统策略允许注册。</summary>
internal static class DeveloperModeRegistry
{
    private static readonly IAppLog Log = AppLog.For(nameof(DeveloperModeRegistry));
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
    private const string ValueName = "AllowDevelopmentWithoutDevLicense";

    /// <summary>每次注册前读取当前状态；缺失设置视为关闭，读取失败则降级为普通注册。</summary>
    [SupportedOSPlatform("windows")]
    internal static DeveloperModeState ReadState()
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(KeyPath);
            object? value = key?.GetValue(ValueName);
            DeveloperModeState state = ParseValue(value);
            if (state == DeveloperModeState.Unknown)
            {
                Log.Warning("开发者模式注册表值类型异常，将尝试普通注册，值类型={ValueType}", value?.GetType().FullName);
            }

            return state;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取开发者模式状态失败，将尝试普通注册");
            return DeveloperModeState.Unknown;
        }
    }

    /// <summary>异常类型不能解释为关闭，避免因损坏设置不必要地触发提权。</summary>
    internal static DeveloperModeState ParseValue(object? value)
    {
        return value switch
        {
            null => DeveloperModeState.Disabled,
            int number => number == 0 ? DeveloperModeState.Disabled : DeveloperModeState.Enabled,
            _ => DeveloperModeState.Unknown
        };
    }
}
