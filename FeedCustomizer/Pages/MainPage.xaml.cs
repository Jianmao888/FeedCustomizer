using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.ViewModels;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// 主页：只保留与 UI 强相关的代码（导航、加载遮罩、对话框、开关重入保护），
    /// 业务逻辑统一放在 MainPageViewModel 中。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
        public MainPageViewModel ViewModel { get; } = new();

        /// <summary>供窗口启动协调器等待资源同步判定。</summary>
        public Task<bool> ResourceSynchronizationRequiredTask => ViewModel.ResourceSynchronizationRequiredTask;

        /// <summary>供窗口启动协调器等待业务初始化完成。</summary>
        public Task InitializationTask => ViewModel.InitializationTask;

        /// <summary>是否需要在启动完成后展示首次运行安全说明。</summary>
        private readonly bool _showFirstRunDialog = SettingsLoader.GetIsFirstRun();

        /// <summary>启动失败对话框是否已展示过，避免重复弹出。</summary>
        private bool _startupFailureShown;

        /// <summary>防止源提供程序开关在业务逻辑回写状态时重入。</summary>
        private bool _isChangingProviderState;

        public MainPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;

            // 订阅视图模型发出的 UI 请求，让视图模型不依赖具体控件。
            ViewModel.NavigationRequested += OnNavigationRequested;
            ViewModel.LoadingOverlayRequested += OnLoadingOverlayRequested;
            ViewModel.HelpFileReady += OnHelpFileReady;
            ViewModel.ProviderRegistrationFailed += OnProviderRegistrationFailed;

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
                ViewModel.RefreshAfterNavigation();
            }
        }

        /// <summary>
        /// 处理视图模型发出的页面导航请求。
        /// </summary>
        private void OnNavigationRequested(object? sender, MainPageNavigationRequestedEventArgs e)
        {
            _ = sender;

            Type pageType = e.Target switch
            {
                MainPageNavigationTarget.AddFeed => typeof(AddFeedPage),
                MainPageNavigationTarget.Settings => typeof(SettingsPage),
                MainPageNavigationTarget.RegionPolicySettings => typeof(SettingsPage),
                _ => throw new ArgumentOutOfRangeException(nameof(e.Target), e.Target, null),
            };

            Frame.Navigate(pageType, e.Parameter);
        }

        /// <summary>
        /// 处理视图模型发出的加载遮罩显隐请求。
        /// </summary>
        private void OnLoadingOverlayRequested(object? sender, bool isVisible)
        {
            _ = sender;

            if (App.MainWindow is MainWindow window)
            {
                window.SetLoadingOverlayVisible(isVisible);
            }
        }

        /// <summary>
        /// 处理视图模型发出的帮助文档打开请求。
        /// </summary>
        private async void OnHelpFileReady(object? sender, StorageFile file)
        {
            _ = sender;

            if (App.MainWindow is MainWindow window)
            {
                await window.ExternalLaunch.OpenFileAsync(file);
            }
        }

        /// <summary>
        /// 将 Provider 注册失败转换为用户可理解的对话框。此处是基础设施诊断进入 UI 的唯一入口，
        /// 因此 PackageInstaller 不再依赖窗口、资源加载器或 DialogService。
        /// </summary>
        private async void OnProviderRegistrationFailed(object? sender, ProviderRegistrationResult result)
        {
            _ = sender;

            try
            {
                if (result.Status == ProviderRegistrationStatus.DeveloperModeConfirmationRequired)
                {
                    var resourceLoader = new ResourceLoader();
                    bool confirmed = await DialogService.ShowAfterStartupConfirmAsync(
                        resourceLoader.GetString("EnableProviderFail"),
                        resourceLoader.GetString("DeveloperModeDisabled"),
                        resourceLoader.GetString("EnableAutoDeveloperMode"),
                        resourceLoader.GetString("DialogCancel"));
                    if (!confirmed)
                    {
                        return;
                    }

                    result = await ViewModel.RetryProviderRegistrationWithAutoDeveloperModeAsync();
                    if (result.Succeeded)
                    {
                        return;
                    }
                }

                await ShowProviderRegistrationFailureAsync(result);
            }
            catch (Exception ex)
            {
                // 事件处理器不能让展示失败的异常脱离 UI 同步上下文。
                Debug.WriteLine($"显示 Provider 注册错误失败：{ex}");
            }
        }

        /// <summary>
        /// 按结果类别选择简短提示或可复制诊断对话框。
        /// </summary>
        private static async Task ShowProviderRegistrationFailureAsync(ProviderRegistrationResult result)
        {
            var resourceLoader = new ResourceLoader();
            string title = resourceLoader.GetString("EnableProviderFail");

            if (result.Status == ProviderRegistrationStatus.ElevationCancelled)
            {
                await DialogService.ShowMessageAsync(
                    title,
                    resourceLoader.GetString("DeveloperModeElevationCancelled"),
                    resourceLoader.GetString("DialogOK"));
                return;
            }

            string details = string.Join(
                Environment.NewLine,
                $"ExitCode: {(result.ExitCode?.ToString() ?? "n/a")}",
                $"SourceManifest: {result.ManifestPath}",
                $"Error: {result.Error}",
                $"Output: {result.Output}");
            string content = resourceLoader.GetString("SomethingErrorsOccurred");
            await DialogService.ShowStartupFailureAsync(
                title,
                $"{content}{Environment.NewLine}{Environment.NewLine}{details}");
        }

        /// <summary>
        /// 编辑源：只从 UI 元素中取出数据上下文，其余逻辑交给视图模型。
        /// </summary>
        private void OnEditFeedButtonClicked(object sender, RoutedEventArgs _)
        {
            if (sender is FrameworkElement { DataContext: FeedViewModel feed })
            {
                ViewModel.EditFeed(feed);
            }
        }

        /// <summary>
        /// 删除源：只从 UI 元素中取出数据上下文，其余逻辑交给视图模型。
        /// </summary>
        private void OnDeleteFeedButtonClicked(object sender, RoutedEventArgs _)
        {
            if (sender is FrameworkElement { DataContext: FeedViewModel feed })
            {
                ViewModel.DeleteFeed(feed);
            }
        }

        /// <summary>
        /// 源提供程序开关。ToggleSwitch 没有 Command 属性，且需要遮罩与重入保护，
        /// 因此保留在代码后置中，实际业务逻辑仍由视图模型执行。
        /// </summary>
        private async void OnFeedProviderToggleChanged(object sender, RoutedEventArgs e)
        {
            _ = e;

            if (_isChangingProviderState ||
                ViewModel.IsLoading ||
                sender is not ToggleSwitch toggleSwitch ||
                App.MainWindow is not MainWindow window)
            {
                return;
            }

            _isChangingProviderState = true;

            // 让视图模型状态与开关保持一致（TwoWay 绑定通常已完成，这里做防御性同步）。
            ViewModel.IsFeedProviderEnabled = toggleSwitch.IsOn;

            window.SetLoadingOverlayVisible(true);
            try
            {
                await ViewModel.EnableOrDisableFeedProviderAsync();
            }
            finally
            {
                window.SetLoadingOverlayVisible(false);
                _isChangingProviderState = false;
            }
        }

        /// <summary>
        /// 由窗口启动协调器在启动视觉状态结束后调用，只处理页面专属的首次运行和失败提示。
        /// </summary>
        public async Task ShowStartupCompletionDialogsAsync(Exception? initializationException)
        {
            // 数据已加载完成，主页即将展示。后台清理孤儿图片，避免阻塞启动流程。
            if (initializationException is null)
            {
                _ = ViewModel.CleanUpUnusedImagesAsync();
            }

            if (_showFirstRunDialog)
            {
                await DialogService.ShowFirstRunAsync();
                SettingsLoader.SetIsFirstRun(false);
            }

            if (initializationException is not null && !_startupFailureShown)
            {
                _startupFailureShown = true;
                await DialogService.ShowStartupFailureAsync(
                    // TODO 稍后将这里改为本地字符串
                    "启动失败",
                    initializationException.ToString());
            }
        }
    }
}
