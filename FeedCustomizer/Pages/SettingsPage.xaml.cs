using FeedCustomizer.Core.Tools;
using FeedCustomizer.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using Windows.ApplicationModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        public AboutViewModel AboutViewModel { get; } = new();
        private bool _isInitializing = true;

        public SettingsPage()
        {
            this.InitializeComponent();
            this.Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadUI();
            LoadAppInfo();
            _isInitializing = false;
        }

        private void LoadUI()
        {
            RbTheme.SelectedIndex = SettingsLoader.GetAppTheme() switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            RbMaterial.SelectedIndex = SettingsLoader.GetAppMaterial() switch
            {
                "MicaAlt" => 1,
                "Acrylic" => 2,
                _ => 0
            };

            SoundToggle.IsOn = SettingsLoader.GetEnableSound();
        }

        public void LoadAppInfo()
        {
            try
            {
                TxtAppName.Text = Package.Current.DisplayName;
                TxtVersion.Text = AboutViewModel.GetCurrentVersion();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadAppInfo 错误: {ex.Message}");
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

            SettingsLoader.SetAppTheme(value);
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
            SettingsLoader.SetAppMaterial(value);
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
            SettingsLoader.SetEnableSound(isOn);
            ElementSoundPlayer.State = isOn ? ElementSoundPlayerState.On : ElementSoundPlayerState.Off;
        }

        private async void OpenSourceLink_Click(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            if (App.MainWindow is MainWindow window)
            {
                await window.ExternalLaunch.OpenLinkAsync(AboutViewModel.OpenSourceLink);
            }
        }

        private async void DeveloperLink_Click(object sender, RoutedEventArgs e)
        {
            _ = e;
            if (sender is HyperlinkButton hb)
            {
                var link = hb.Tag as string;
                if (string.IsNullOrEmpty(link))
                {
                    link = AboutViewModel.DeveloperStoreLink;
                }

                if (App.MainWindow is MainWindow window)
                {
                    await window.ExternalLaunch.OpenLinkAsync(link);
                }
            }
        }
    }
}
