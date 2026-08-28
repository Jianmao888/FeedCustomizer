using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
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
        public SettingsViewModel AboutViewModel { get; } = new();
        private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader _resourceLoader = new();
        private bool _isInitializing = true;
        private bool _focusRegionPolicyButton;

        public SettingsPage()
        {
            this.InitializeComponent();
            this.Loaded += SettingsPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _focusRegionPolicyButton = e.Parameter is string parameter &&
                parameter == Constants.RegionPolicy.NavigationParameter;
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadUI();
            LoadAppInfo();
            _isInitializing = false;

            // 不在页面加载流程中等待 Store 许可证查询，先展示页面，再在后台刷新捐赠者版购买状态
            _ = RefreshDonationStateAsync();

            if (_focusRegionPolicyButton)
            {
                _focusRegionPolicyButton = false;
                FocusRegionPolicyButton();
            }
        }

        private void FocusRegionPolicyButton()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UnlockRegionPolicyButton.StartBringIntoView();
                UnlockRegionPolicyButton.Focus(FocusState.Keyboard);
            });
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
            AutoDeveloperModeToggle.IsOn = SettingsLoader.GetAutoEnableDeveloperMode();
        }

        public void LoadAppInfo()
        {
            try
            {
                TxtAppName.Text = Package.Current.DisplayName;
                TxtVersion.Text = SettingsViewModel.GetCurrentVersion();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadAppInfo 错误: {ex.Message}");
            }
        }

        private async Task RefreshDonationStateAsync()
        {
            IntPtr hwnd = IntPtr.Zero;
            if (App.MainWindow is MainWindow window)
            {
                hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            }

            var checkTask = DonationService.IsDonorEditionPurchasedAsync(hwnd);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var completedTask = await Task.WhenAny(checkTask, timeoutTask);

            bool purchased =
                completedTask == checkTask &&
                checkTask.Status == TaskStatus.RanToCompletion &&
                checkTask.Result;

            if (DispatcherQueue.HasThreadAccess)
            {
                SetDonationPurchasedState(purchased);
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => SetDonationPurchasedState(purchased));
            }
        }

        private void SetDonationPurchasedState(bool purchased)
        {
            DonateButton.Visibility = purchased ? Visibility.Collapsed : Visibility.Visible;
            DonationThanksPanel.Visibility = purchased ? Visibility.Visible : Visibility.Collapsed;
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

        private void AutoDeveloperModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SettingsLoader.SetAutoEnableDeveloperMode(AutoDeveloperModeToggle.IsOn);
        }

        private async void DonateButton_Click(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            if (App.MainWindow is not MainWindow window)
            {
                return;
            }

            bool confirmed = await DialogService.ShowConfirmAsync(
                _resourceLoader.GetString("DonationConfirmTitle"),
                _resourceLoader.GetString("DonationConfirmMessage"),
                _resourceLoader.GetString("DonationConfirmPrimaryButtonText"),
                _resourceLoader.GetString("DonationConfirmCloseButtonText"));

            if (!confirmed)
            {
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var result = await DonationService.PurchaseDonorEditionAsync(hwnd);

            if (result is DonationPurchaseResult.Purchased or DonationPurchaseResult.AlreadyPurchased)
            {
                SetDonationPurchasedState(true);
            }
            else if (result == DonationPurchaseResult.Failed)
            {
                await DialogService.ShowMessageAsync(
                    _resourceLoader.GetString("DonationErrorTitle"),
                    _resourceLoader.GetString("DonationErrorMessage"),
                    _resourceLoader.GetString("DialogOK"));
            }
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

        private async void UnlockRegionPolicyButton_Click(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            bool confirmed = await DialogService.ShowConfirmAsync(
                _resourceLoader.GetString("RegionPolicyWarningTitle"),
                _resourceLoader.GetString("RegionPolicyWarningMessage"),
                _resourceLoader.GetString("RegionPolicyWarningPrimaryButtonText"),
                _resourceLoader.GetString("DonationConfirmCloseButtonText"));

            if (!confirmed)
            {
                return;
            }

            RegionPolicyOperationResult result = await RegionPolicyService.EnableThirdPartyWidgetFeedAsync();

            switch (result)
            {
                case RegionPolicyOperationResult.Success:
                    await DialogService.ShowMessageAsync(
                        _resourceLoader.GetString("RegionPolicySuccessTitle"),
                        _resourceLoader.GetString("RegionPolicySuccessMessage"),
                        _resourceLoader.GetString("DialogOK"));
                    break;

                case RegionPolicyOperationResult.Cancelled:
                    await DialogService.ShowMessageAsync(
                        _resourceLoader.GetString("RegionPolicyWarningTitle"),
                        _resourceLoader.GetString("RegionPolicyCancelledMessage"),
                        _resourceLoader.GetString("DialogOK"));
                    break;

                case RegionPolicyOperationResult.PolicyNotFound:
                    await DialogService.ShowMessageAsync(
                        _resourceLoader.GetString("RegionPolicyFailureTitle"),
                        BuildRegionPolicyFailureMessage(_resourceLoader.GetString("RegionPolicyPolicyNotFoundMessage")),
                        _resourceLoader.GetString("DialogOK"));
                    break;

                default:
                    await DialogService.ShowMessageAsync(
                        _resourceLoader.GetString("RegionPolicyFailureTitle"),
                        BuildRegionPolicyFailureMessage(_resourceLoader.GetString("RegionPolicyFailureMessage")),
                        _resourceLoader.GetString("DialogOK"));
                    break;
            }
        }

        private static string BuildRegionPolicyFailureMessage(string baseMessage)
        {
            string? diagnostics = RegionPolicyService.LastDiagnostics;
            return string.IsNullOrWhiteSpace(diagnostics)
                ? baseMessage
                : baseMessage + Environment.NewLine + Environment.NewLine + diagnostics;
        }
    }
}
