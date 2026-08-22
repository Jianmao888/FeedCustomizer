using System;
using Windows.Storage;

namespace FeedCustomizer.Core.Tools
{
    public static class SettingsLoader
    {

        private static ApplicationDataContainer LocalSettings = ApplicationData.Current.LocalSettings;

        public static Object GetSettingsOption(string SettingName)
        {
            return LocalSettings.Values[SettingName];
        }

        public static void SetSettingsOption(string SettingName, object Value)
        {
            LocalSettings.Values[SettingName] = Value;
        }

        public static bool ContainsKey(string SettingName)
        {
            return LocalSettings.Values.ContainsKey(SettingName);
        }
    }
}
