using FeedCustomizer.Core.Constants;
using Windows.ApplicationModel;
using System.Linq;

namespace FeedCustomizer.ViewModels
{
    public class AboutViewModel
    {
        private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader resourceLoader = new();

        // 为 XAML 绑定提供应用图标（BitmapImage）
        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage AppLogoImage => new(Package.Current.Logo);

        public static string Developer => Package.Current.PublisherDisplayName;

        public string DeveloperStoreLink { get; set; } =
            (Constants.DeveloperStoreLinks != null && Constants.DeveloperStoreLinks.Length > 0 && !string.IsNullOrEmpty(Constants.DeveloperStoreLinks[0]))
            ? Constants.DeveloperStoreLinks[0]
            : Constants.DeveloperStoreLink;
        // 为 XAML 绑定提供单独的开发者名与链接（支持两个合作者）
        public string Developer1Name => (Constants.DeveloperNames != null && Constants.DeveloperNames.Length > 0) ? Constants.DeveloperNames[0] : Developer;
        public string Developer2Name => (Constants.DeveloperNames != null && Constants.DeveloperNames.Length > 1) ? Constants.DeveloperNames[1] : string.Empty;
        public string Developer1Link => (Constants.DeveloperStoreLinks != null && Constants.DeveloperStoreLinks.Length > 0 && !string.IsNullOrEmpty(Constants.DeveloperStoreLinks[0])) ? Constants.DeveloperStoreLinks[0] : Constants.DeveloperStoreLink;
        public string Developer2Link => (Constants.DeveloperStoreLinks != null && Constants.DeveloperStoreLinks.Length > 1 && !string.IsNullOrEmpty(Constants.DeveloperStoreLinks[1])) ? Constants.DeveloperStoreLinks[1] : string.Empty;
        public string OpenSourceLink { get; set; } = Constants.OpenSourceLink;

        // 合并显示名，供版权与其它位置使用
        public string DeveloperStoreDisplayName => string.Join(" / ", new[] { Developer1Name, Developer2Name }.Where(s => !string.IsNullOrEmpty(s)));

        public static string GetCurrentVersion()
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }

        public string GetCopyrightPrefix()
        {
            // 版权前缀只显示年份，开发者由超链接按钮显示
            return string.Format(
                resourceLoader.GetString("AboutCopyrightPrefix"),
                System.DateTime.Now.Year);
        }
    }
}
