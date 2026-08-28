using Windows.Storage;
using SettingDefaults = FeedCustomizer.Core.Constants.Constants.SettingDefaults;

namespace FeedCustomizer.Core.Tools
{
    public static class SettingsLoader
    {
        private static readonly ApplicationDataContainer LocalSettings = ApplicationData.Current.LocalSettings;

        public static object GetSettingsOption(string settingName) =>
            LocalSettings.Values[settingName];

        public static void SetSettingsOption(string settingName, object value) =>
            LocalSettings.Values[settingName] = value;

        public static bool ContainsKey(string settingName) =>
            LocalSettings.Values.ContainsKey(settingName);

        public static string GetAppTheme()
        {
            if (LocalSettings.Values["AppTheme"] is string value)
            {
                return value;
            }

            SetAppTheme(SettingDefaults.AppTheme);
            return SettingDefaults.AppTheme;
        }

        public static void SetAppTheme(string value) =>
            LocalSettings.Values["AppTheme"] = value;

        public static string GetAppMaterial()
        {
            if (LocalSettings.Values["AppMaterial"] is string value)
            {
                return value;
            }

            SetAppMaterial(SettingDefaults.AppMaterial);
            return SettingDefaults.AppMaterial;
        }

        public static void SetAppMaterial(string value) =>
            LocalSettings.Values["AppMaterial"] = value;

        public static bool GetEnableSound()
        {
            if (LocalSettings.Values["EnableSound"] is bool value)
            {
                return value;
            }

            SetEnableSound(SettingDefaults.EnableSound);
            return SettingDefaults.EnableSound;
        }

        public static void SetEnableSound(bool value) =>
            LocalSettings.Values["EnableSound"] = value;

        public static bool GetIsFirstRun()
        {
            if (LocalSettings.Values["IsFirstRun"] is bool value)
            {
                return value;
            }

            SetIsFirstRun(SettingDefaults.IsFirstRun);
            return SettingDefaults.IsFirstRun;
        }

        public static void SetIsFirstRun(bool value) =>
            LocalSettings.Values["IsFirstRun"] = value;

        public static bool GetAutoEnableDeveloperMode()
        {
            if (LocalSettings.Values["AutoEnableDeveloperMode"] is bool value)
            {
                return value;
            }

            SetAutoEnableDeveloperMode(SettingDefaults.AutoEnableDeveloperMode);
            return SettingDefaults.AutoEnableDeveloperMode;
        }

        public static void SetAutoEnableDeveloperMode(bool value) =>
            LocalSettings.Values["AutoEnableDeveloperMode"] = value;
    }
}
