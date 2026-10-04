using System;
using SettingDefaults = FeedCustomizer.Core.Constants.Constants.SettingDefaults;

namespace FeedCustomizer.Core.Settings;

/// <summary>
/// 将应用设置键映射为强语义操作，并统一恢复缺失或类型损坏的默认值。
/// 服务不缓存设置，所有调用都读取同一存储，避免页面持有不同的配置副本。
/// </summary>
internal sealed class AppSettingsService : IAppSettings
{
    private const string AppThemeKey = "AppTheme";
    private const string AppMaterialKey = "AppMaterial";
    private const string IsFirstRunKey = "IsFirstRun";
    private const string AutoEnableDeveloperModeKey = "AutoEnableDeveloperMode";
    private readonly ISettingsStore _store;

    /// <summary>使用由组合入口提供的存储，避免设置服务直接绑定 Windows 运行时。</summary>
    internal AppSettingsService(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>读取主题设置，缺失或类型损坏时写回既有默认主题。</summary>
    public string GetAppTheme() => GetOrCreateDefault(AppThemeKey, SettingDefaults.AppTheme);

    /// <summary>保存主题字符串，保留既有设置对自定义值的接受语义。</summary>
    public void SetAppTheme(string value) => _store.Write(AppThemeKey, value);

    /// <summary>读取背景材质设置，缺失或类型损坏时写回既有默认材质。</summary>
    public string GetAppMaterial() => GetOrCreateDefault(AppMaterialKey, SettingDefaults.AppMaterial);

    /// <summary>保存背景材质字符串，保留既有设置对自定义值的接受语义。</summary>
    public void SetAppMaterial(string value) => _store.Write(AppMaterialKey, value);

    /// <summary>读取首次运行标志，缺失或类型损坏时写回既有默认值。</summary>
    public bool GetIsFirstRun() => GetOrCreateDefault(IsFirstRunKey, SettingDefaults.IsFirstRun);

    /// <summary>保存首次运行标志，存储失败向调用方传播。</summary>
    public void SetIsFirstRun(bool value) => _store.Write(IsFirstRunKey, value);

    /// <summary>读取自动开启开发者模式设置，缺失或类型损坏时写回既有默认值。</summary>
    public bool GetAutoEnableDeveloperMode() =>
        GetOrCreateDefault(AutoEnableDeveloperModeKey, SettingDefaults.AutoEnableDeveloperMode);

    /// <summary>保存自动开启开发者模式设置，存储失败向调用方传播。</summary>
    public void SetAutoEnableDeveloperMode(bool value) => _store.Write(AutoEnableDeveloperModeKey, value);

    private T GetOrCreateDefault<T>(string key, T defaultValue) where T : notnull
    {
        if (_store.Read(key) is T value)
        {
            return value;
        }

        // 既有值只按类型判断，不增加枚举校验；必须先写回成功再返回默认值，保留原始失败语义。
        _store.Write(key, defaultValue);
        return defaultValue;
    }
}
