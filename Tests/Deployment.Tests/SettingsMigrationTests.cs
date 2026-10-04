using FeedCustomizer.Core.Settings;
using SettingDefaults = FeedCustomizer.Core.Constants.Constants.SettingDefaults;

/// <summary>通过存储替身验证设置迁移不改变已有值、默认恢复及失败传播语义。</summary>
internal static class SettingsMigrationTests
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> Cases { get; } =
    [
        ("设置迁移保留已有值且不重复写入", ExistingValuesArePreservedAsync),
        ("设置缺失时写回各键默认值且重复读取不重写", MissingValuesAreInitializedAsync),
        ("设置类型损坏时按原类型规则恢复默认值", InvalidTypesAreReplacedAsync),
        ("设置修改原样写入原有键并立即可读", ExplicitValuesAreStoredAsync),
        ("设置读取失败原样传播且不写回默认值", ReadFailuresArePropagatedAsync),
        ("设置默认值写回失败不能伪装读取成功", DefaultWriteFailuresArePropagatedAsync),
        ("设置显式写入失败原样传播", ExplicitWriteFailuresArePropagatedAsync),
    ];

    private static Task ExistingValuesArePreservedAsync()
    {
        var store = new MemorySettingsStore();
        store.Values["AppTheme"] = "CustomTheme";
        store.Values["AppMaterial"] = string.Empty;
        store.Values["IsFirstRun"] = false;
        store.Values["AutoEnableDeveloperMode"] = true;
        var settings = new AppSettingsService(store);

        Require(settings.GetAppTheme() == "CustomTheme");
        Require(settings.GetAppMaterial() == string.Empty);
        Require(!settings.GetIsFirstRun());
        Require(settings.GetAutoEnableDeveloperMode());
        Require(store.WriteAttempts == 0);
        return Task.CompletedTask;
    }

    private static Task MissingValuesAreInitializedAsync()
    {
        var store = new MemorySettingsStore();
        var settings = new AppSettingsService(store);

        RequireDefaults(settings);
        Require(store.WriteAttempts == 4);
        Require(Equals(store.Values["AppTheme"], SettingDefaults.AppTheme));
        Require(Equals(store.Values["AppMaterial"], SettingDefaults.AppMaterial));
        Require(Equals(store.Values["IsFirstRun"], SettingDefaults.IsFirstRun));
        Require(Equals(store.Values["AutoEnableDeveloperMode"], SettingDefaults.AutoEnableDeveloperMode));

        RequireDefaults(settings);
        Require(store.WriteAttempts == 4);
        return Task.CompletedTask;
    }

    private static Task InvalidTypesAreReplacedAsync()
    {
        var store = new MemorySettingsStore();
        store.Values["AppTheme"] = true;
        store.Values["AppMaterial"] = 1;
        store.Values["IsFirstRun"] = "true";
        store.Values["AutoEnableDeveloperMode"] = 1;
        var settings = new AppSettingsService(store);

        RequireDefaults(settings);
        Require(store.WriteAttempts == 4);
        Require(store.Values["AppTheme"] is string);
        Require(store.Values["AppMaterial"] is string);
        Require(store.Values["IsFirstRun"] is bool);
        Require(store.Values["AutoEnableDeveloperMode"] is bool);
        return Task.CompletedTask;
    }

    private static Task ExplicitValuesAreStoredAsync()
    {
        var store = new MemorySettingsStore();
        var settings = new AppSettingsService(store);

        settings.SetAppTheme("Dark");
        settings.SetAppMaterial("Acrylic");
        settings.SetIsFirstRun(false);
        settings.SetAutoEnableDeveloperMode(true);

        Require(store.Values.Count == 4);
        Require(Equals(store.Values["AppTheme"], "Dark"));
        Require(Equals(store.Values["AppMaterial"], "Acrylic"));
        Require(Equals(store.Values["IsFirstRun"], false));
        Require(Equals(store.Values["AutoEnableDeveloperMode"], true));
        Require(settings.GetAppTheme() == "Dark");
        Require(settings.GetAppMaterial() == "Acrylic");
        Require(!settings.GetIsFirstRun());
        Require(settings.GetAutoEnableDeveloperMode());
        Require(store.WriteAttempts == 4);
        return Task.CompletedTask;
    }

    private static Task ReadFailuresArePropagatedAsync()
    {
        var failure = new IOException("设置读取测试失败。");
        var store = new MemorySettingsStore { ReadFailure = failure };
        var settings = new AppSettingsService(store);

        foreach (Action read in CreateReadActions(settings))
        {
            RequireSameFailure(read, failure);
        }

        Require(store.WriteAttempts == 0);
        Require(store.Values.Count == 0);
        return Task.CompletedTask;
    }

    private static Task DefaultWriteFailuresArePropagatedAsync()
    {
        var failure = new UnauthorizedAccessException("设置默认值写入测试失败。");
        var store = new MemorySettingsStore { WriteFailure = failure };
        var settings = new AppSettingsService(store);

        foreach (Action read in CreateReadActions(settings))
        {
            RequireSameFailure(read, failure);
        }

        Require(store.WriteAttempts == 4);
        Require(store.Values.Count == 0);
        return Task.CompletedTask;
    }

    private static Task ExplicitWriteFailuresArePropagatedAsync()
    {
        var failure = new IOException("设置显式写入测试失败。");
        var store = new MemorySettingsStore { WriteFailure = failure };
        var settings = new AppSettingsService(store);
        Action[] writes =
        [
            () => settings.SetAppTheme("Light"),
            () => settings.SetAppMaterial("MicaAlt"),
            () => settings.SetIsFirstRun(false),
            () => settings.SetAutoEnableDeveloperMode(true),
        ];

        foreach (Action write in writes)
        {
            RequireSameFailure(write, failure);
        }

        Require(store.WriteAttempts == 4);
        Require(store.Values.Count == 0);
        return Task.CompletedTask;
    }

    private static Action[] CreateReadActions(IAppSettings settings) =>
    [
        () => settings.GetAppTheme(),
        () => settings.GetAppMaterial(),
        () => settings.GetIsFirstRun(),
        () => settings.GetAutoEnableDeveloperMode(),
    ];

    private static void RequireDefaults(IAppSettings settings)
    {
        Require(settings.GetAppTheme() == SettingDefaults.AppTheme);
        Require(settings.GetAppMaterial() == SettingDefaults.AppMaterial);
        Require(settings.GetIsFirstRun() == SettingDefaults.IsFirstRun);
        Require(settings.GetAutoEnableDeveloperMode() == SettingDefaults.AutoEnableDeveloperMode);
    }

    private static void RequireSameFailure(Action action, Exception expected)
    {
        try
        {
            action();
        }
        catch (Exception actual) when (ReferenceEquals(actual, expected))
        {
            return;
        }

        throw new InvalidOperationException("设置存储异常未原样传播。");
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("设置迁移行为验证失败。");
        }
    }

    /// <summary>只模拟原始读写及确定的失败，避免测试触碰真实用户设置。</summary>
    private sealed class MemorySettingsStore : ISettingsStore
    {
        internal Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        internal int WriteAttempts { get; private set; }

        internal Exception? ReadFailure { get; init; }

        internal Exception? WriteFailure { get; init; }

        /// <summary>返回原始值，或传播测试指定的读取失败。</summary>
        public object? Read(string key)
        {
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return Values.TryGetValue(key, out object? value) ? value : null;
        }

        /// <summary>记录写入尝试，失败时不修改替身中已有的数据。</summary>
        public void Write(string key, object value)
        {
            WriteAttempts++;
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            Values[key] = value;
        }
    }
}
