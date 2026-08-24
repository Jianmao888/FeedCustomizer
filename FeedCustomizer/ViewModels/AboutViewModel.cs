using FeedCustomizer.Core.Constants;
using Windows.ApplicationModel;

namespace FeedCustomizer.ViewModels
{
    public class AboutViewModel
    {
        private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader resourceLoader = new();

        public string Developer => Package.Current.PublisherDisplayName;
        public string DeveloperStoreDisplayName => "惜忆想睡觉";
        public string DeveloperStoreLink { get; set; } = Constants.DeveloperStoreLink;
        public string OpenSourceLink { get; set; } = Constants.OpenSourceLink;
        //public static string Version => GetCurrentVersion();

        public string GetCurrentVersion()
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }

        public string GetCopyrightPrefix()
        {
            return string.Format(
                resourceLoader.GetString("AboutCopyrightPrefix"),
                System.DateTime.Now.Year,
                Developer);
        }
    }
}
