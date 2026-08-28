using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly bool _showFirstRunDialog = MainPageModel.CheckFirstRunDialog();
        private readonly MainPageModel MainPageViewModel = new();
        private bool _startupFailureShown;
        private bool _isChangingProviderState;

        public MainPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
            if (App.MainWindow is MainWindow)
            {
                DialogService.SetFirstRunDialogPending(_showFirstRunDialog);
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.NavigationMode == NavigationMode.Back)
            {
                MainPageViewModel.RefreshAfterNavigation();
            }
        }
        private void AddFeedButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainPageViewModel.IsLoading.Value) return;
            if (!MainPageViewModel.CanAddFeed()) return;
            MainPageViewModel.SaveListToDataService();
            Frame.Navigate(typeof(AddFeedPage));
        }

        private async void OnGetHelpButtonClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            var file = await MainPageViewModel.GetHelpFileAsync();
            if (file is not null && App.MainWindow is MainWindow window)
            {
                await window.ExternalLaunch.OpenFileAsync(file);
            }
        }

        private void OnAboutButtonClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            MainPageViewModel.SaveListToDataService();
            Frame.Navigate(typeof(SettingsPage));
        }

        private void OnUnlockRegionPolicyButtonClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            MainPageViewModel.SaveListToDataService();
            Frame.Navigate(typeof(SettingsPage), Constants.RegionPolicy.NavigationParameter);
        }

        private void OnAnyControlActivated()
        {
            MainPageViewModel.DisableAllControl();
        }

        private void OnEditFeedButtonClicked(object sender, RoutedEventArgs _)
        {
            if (MainPageViewModel.IsLoading.Value) return;
            OnAnyControlActivated();
            MainPageViewModel.SaveListToDataService();
            if (sender is FrameworkElement element && element.DataContext is FeedViewModel FeedViewModel)
            {
                Frame.Navigate(typeof(AddFeedPage), FeedViewModel);
            }
        }

        private void OnDeleteFeedButtonClicked(object sender, RoutedEventArgs _)
        {
            if (MainPageViewModel.IsLoading.Value) return;
            OnAnyControlActivated();
            if (sender is FrameworkElement element && element.DataContext is FeedViewModel FeedViewModel)
            {
                MainPageViewModel.DeleteFeed(FeedViewModel);
            }
        }

        private async void OnApplyButtonClicked(object sender, RoutedEventArgs e)
        {
            _ = e;
            _ = sender;
            if (MainPageViewModel.IsLoading.Value) return;

            if (App.MainWindow is not MainWindow window)
            {
                return;
            }

            OnAnyControlActivated();
            window.SetLoadingOverlayVisible(true);
            try
            {
                await MainPageViewModel.SaveFeeds();
            }
            finally
            {
                window.SetLoadingOverlayVisible(false);
            }
        }

        private async void OnCancelChangesButtonClicked(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            if (MainPageViewModel.IsLoading.Value || App.MainWindow is not MainWindow window)
            {
                return;
            }

            OnAnyControlActivated();
            window.SetLoadingOverlayVisible(true);
            try
            {
                await MainPageViewModel.CancelPendingChanges();
            }
            finally
            {
                window.SetLoadingOverlayVisible(false);
            }
        }

        private async void OnFeedProviderToggleChanged(object sender, RoutedEventArgs e)
        {
            _ = e;
            if (_isChangingProviderState ||
                MainPageViewModel.IsLoading.Value ||
                sender is not ToggleSwitch toggleSwitch ||
                App.MainWindow is not MainWindow window)
            {
                return;
            }

            _isChangingProviderState = true;
            MainPageViewModel.IsFeedProviderEnabled.Value = toggleSwitch.IsOn;
            OnAnyControlActivated();
            window.SetLoadingOverlayVisible(true);
            try
            {
                await MainPageViewModel.EnableOrDisableFeedProvider();
            }
            finally
            {
                window.SetLoadingOverlayVisible(false);
                _isChangingProviderState = false;
            }
        }

        /// <summary>
        /// Called when the Page is navigated to.
        /// </summary>
        /// <param name="e"></param>
        public async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            if (App.MainWindow is not MainWindow window)
            {
                return;
            }

            Exception? initializationException = null;
            try
            {
                await MainPageViewModel.InitializationTask;
            }
            catch (Exception ex)
            {
                initializationException = ex;
            }

            window.NotifyInitialContentReady();

            // 数据已加载完成，主页即将展示。后台清理孤儿图片，避免阻塞启动流程。
            _ = MainPageViewModel.CleanUpUnusedImagesAsync();

            await window.WaitForSplashHiddenAsync();

            if (_showFirstRunDialog)
            {
                await DialogService.ShowFirstRunAsync();
                MainPageModel.SetFirstRunFalg();
            }

            if (initializationException is not null && !_startupFailureShown)
            {
                _startupFailureShown = true;
                await DialogService.ShowStartupFailureAsync(
                    "启动失败",
                    initializationException.ToString());
            }
        }
    }
}
