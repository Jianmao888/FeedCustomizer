using FeedCustomizer.Core.Constants;
using Windows.ApplicationModel;

namespace FeedCustomizer.ViewModels
{
    public class AboutViewModel
    {
        public string Developer => Package.Current.PublisherDisplayName;
        public string DeveloperStoreLink { get; set; } = Constants.DeveloperStoreLink;
        public string OpenSourceLink { get; set; } = Constants.OpenSourceLink;
        //public static string Version => GetCurrentVersion();

        public string GetCurrentVersion()
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
    }
}
