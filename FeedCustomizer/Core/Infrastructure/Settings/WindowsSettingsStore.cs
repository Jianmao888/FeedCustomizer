using FeedCustomizer.Core.Settings;
using System;
using Windows.Storage;

namespace FeedCustomizer.Core.Infrastructure.Settings;

/// <summary>
/// 使用应用私有 LocalSettings 持久化设置。实例由组合入口持有，
/// 不保存全局可变状态，也不在平台边界修改默认值或吞掉读写异常。
/// </summary>
internal sealed class WindowsSettingsStore : ISettingsStore
{
    private readonly ApplicationDataContainer _localSettings;

    /// <summary>绑定当前应用的私有设置容器。</summary>
    internal WindowsSettingsStore()
        : this(ApplicationData.Current.LocalSettings)
    {
    }

    /// <summary>绑定指定设置容器，便于由组合入口明确管理存储实例的生命周期。</summary>
    internal WindowsSettingsStore(ApplicationDataContainer localSettings)
    {
        ArgumentNullException.ThrowIfNull(localSettings);
        _localSettings = localSettings;
    }

    /// <summary>读取原始设置值，缺失时返回空值，WinRT 读取异常向调用方传播。</summary>
    public object? Read(string key)
    {
        return _localSettings.Values.TryGetValue(key, out object? value) ? value : null;
    }

    /// <summary>原样写入设置值，WinRT 写入异常向调用方传播。</summary>
    public void Write(string key, object value)
    {
        _localSettings.Values[key] = value;
    }
}
