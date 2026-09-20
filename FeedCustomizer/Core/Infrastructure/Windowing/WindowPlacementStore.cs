using FeedCustomizer.Core.Windowing;
using System.IO;
using Windows.Storage;

namespace FeedCustomizer.Core.Infrastructure.Windowing;

/// <summary>
/// 窗口布局的 MSIX 私有设置存储。复合值保证一组几何字段作为一个整体替换，
/// 并避免为简单基础类型引入依赖反射的序列化逻辑。
/// </summary>
internal sealed class WindowPlacementStore
{
    private const string SettingKey = "WindowPlacementV1";
    private readonly ApplicationDataContainer _localSettings;

    internal WindowPlacementStore()
        : this(ApplicationData.Current.LocalSettings)
    {
    }

    internal WindowPlacementStore(ApplicationDataContainer localSettings)
    {
        _localSettings = localSettings;
    }

    /// <summary>
    /// 读取已保存状态。键不存在表示首次运行；键存在但字段损坏时抛出结构化异常，由窗口层记录并回退。
    /// </summary>
    internal WindowPlacementState? Read()
    {
        if (!_localSettings.Values.TryGetValue(SettingKey, out object? stored))
        {
            return null;
        }

        if (stored is not ApplicationDataCompositeValue value ||
            !value.TryGetValue("SchemaVersion", out object? rawSchemaVersion) ||
            rawSchemaVersion is not int schemaVersion ||
            !value.TryGetValue("X", out object? rawX) ||
            rawX is not int x ||
            !value.TryGetValue("Y", out object? rawY) ||
            rawY is not int y ||
            !value.TryGetValue("Width", out object? rawWidth) ||
            rawWidth is not int width ||
            !value.TryGetValue("Height", out object? rawHeight) ||
            rawHeight is not int height ||
            !value.TryGetValue("Dpi", out object? rawDpi) ||
            rawDpi is not int dpi ||
            !value.TryGetValue("WasMaximized", out object? rawWasMaximized) ||
            rawWasMaximized is not bool wasMaximized)
        {
            throw new InvalidDataException("窗口布局设置缺少必要字段或字段类型不正确。");
        }

        var state = new WindowPlacementState(
            schemaVersion,
            x,
            y,
            width,
            height,
            dpi,
            wasMaximized);
        WindowPlacementPolicy.ValidateState(state);
        return state;
    }

    /// <summary>一次性保存经过窗口层确认的正常矩形和最大化状态。</summary>
    internal void Write(WindowPlacementState state)
    {
        WindowPlacementPolicy.ValidateState(state);
        var value = new ApplicationDataCompositeValue
        {
            ["SchemaVersion"] = state.SchemaVersion,
            ["X"] = state.X,
            ["Y"] = state.Y,
            ["Width"] = state.Width,
            ["Height"] = state.Height,
            ["Dpi"] = state.Dpi,
            ["WasMaximized"] = state.WasMaximized,
        };
        _localSettings.Values[SettingKey] = value;
    }
}
