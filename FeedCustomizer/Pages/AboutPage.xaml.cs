using FeedCustomizer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using Windows.ApplicationModel;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AboutPage : Page
    {
        public AboutViewModel AboutViewModel { get; } = new();
        private readonly ApplicationDataContainer _localSettings = ApplicationData.Current.LocalSettings;
        private bool _isInitializing = true;

        public AboutPage()
        {
            InitializeComponent();
            Loaded += AboutPage_Loaded;
        }

        private void AboutPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            LoadAppInfo();
            _isInitializing = false;
        }

        private void LoadSettings()
        {
            string theme = _localSettings.Values["AppTheme"] as string ?? "System";
            RbTheme.SelectedIndex = theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            string material = _localSettings.Values["AppMaterial"] as string ?? "Mica";
            RbMaterial.SelectedIndex = material switch
            {
                "MicaAlt" => 1,
                "Acrylic" => 2,
                _ => 0
            };

            bool sound = _localSettings.Values["EnableSound"] is bool value ? value : true;
            _localSettings.Values["EnableSound"] ??= true;
            SoundToggle.IsOn = sound;
        }

        private void LoadAppInfo()
        {
            try
            {
                TxtAppName.Text = Package.Current.DisplayName;
                TxtVersion.Text = AboutViewModel.GetCurrentVersion();
                TxtCopyrightPrefix.Text = AboutViewModel.GetCopyrightPrefix();
                ImgAppIcon.Source = new BitmapImage(Package.Current.Logo);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadAppInfo 错误: {ex.Message}");
            }
        }

        private void RbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            string value = RbTheme.SelectedIndex switch
            {
                1 => "Light",
                2 => "Dark",
                _ => "System"
            };
            var theme = RbTheme.SelectedIndex switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            _localSettings.Values["AppTheme"] = value;
            AppThemeManager.CurrentTheme = theme;
            if (App.MainWindow?.Content is FrameworkElement root)
                root.RequestedTheme = theme;
            AppThemeManager.UpdateTitleBarColors();
        }

        private void RbMaterial_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            string value = RbMaterial.SelectedIndex switch
            {
                1 => "MicaAlt",
                2 => "Acrylic",
                _ => "Mica"
            };
            _localSettings.Values["AppMaterial"] = value;
            AppThemeManager.CurrentMaterial = value switch
            {
                "MicaAlt" => BackgroundMaterial.MicaAlt,
                "Acrylic" => BackgroundMaterial.Acrylic,
                _ => BackgroundMaterial.Mica
            };
            AppThemeManager.ApplyMaterial();
        }

        private void SoundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            bool isOn = SoundToggle.IsOn;
            _localSettings.Values["EnableSound"] = isOn;
            ElementSoundPlayer.State = isOn ? ElementSoundPlayerState.On : ElementSoundPlayerState.Off;
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
