using FeedCustomizer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AboutPage : Page
    {
        AboutViewModel AboutViewModel { get; set; } = new();
        public AboutPage()
        {
            InitializeComponent();
        }

        private async void OpenSourceLink_Click(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            if (App.MainWindow is MainWindow window)
            {
                await window.OpenExternalLinkAsync(AboutViewModel.OpenSourceLink);
            }
        }

        private async void DeveloperStoreLink_Click(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            if (App.MainWindow is MainWindow window)
            {
                await window.OpenExternalLinkAsync(AboutViewModel.DeveloperStoreLink);
            }
        }
    }
}
