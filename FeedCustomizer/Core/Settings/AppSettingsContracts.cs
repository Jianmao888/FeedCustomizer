namespace FeedCustomizer.Core.Settings;

/// <summary>
/// 应用设置的语义入口。读取缺失或类型损坏的设置时写回既有默认值，
/// 存储失败向调用方传播，避免把未持久化的默认值伪装成成功读取。
/// </summary>
public interface IAppSettings
{
    /// <summary>读取主题设置，必要时持久化并返回默认主题。</summary>
    string GetAppTheme();

    /// <summary>保存主题设置，存储失败时抛出原始异常。</summary>
    void SetAppTheme(string value);

    /// <summary>读取背景材质设置，必要时持久化并返回默认材质。</summary>
    string GetAppMaterial();

    /// <summary>保存背景材质设置，存储失败时抛出原始异常。</summary>
    void SetAppMaterial(string value);

    /// <summary>读取首次运行标志，必要时持久化并返回既有默认值。</summary>
    bool GetIsFirstRun();

    /// <summary>保存首次运行标志，存储失败时抛出原始异常。</summary>
    void SetIsFirstRun(bool value);

    /// <summary>读取自动开启开发者模式设置，必要时持久化并返回既有默认值。</summary>
    bool GetAutoEnableDeveloperMode();

    /// <summary>保存自动开启开发者模式设置，存储失败时抛出原始异常。</summary>
    void SetAutoEnableDeveloperMode(bool value);
}

/// <summary>
/// 设置持久化边界。键不存在时返回空值；实现只负责读写，
/// 类型判断与默认值恢复由应用设置服务负责，读写失败不在此处降级。
/// </summary>
internal interface ISettingsStore
{
    /// <summary>读取指定键的原始值，缺失时返回空值，读取失败时抛出原始异常。</summary>
    object? Read(string key);

    /// <summary>写入指定键的原始值，写入失败时抛出原始异常。</summary>
    void Write(string key, object value);
}
