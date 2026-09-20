using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Documents;
using FeedCustomizer.Core.Feedback;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.ViewModels;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
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
        private static readonly IAppLog Log = AppLog.For<MainPage>();

        /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
        public MainPageViewModel ViewModel { get; }

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
            MainWindow window = App.MainWindow
                ?? throw new InvalidOperationException("主窗口尚未创建，无法构造主页文档服务。");
            ViewModel = new MainPageViewModel(
                window.Documents,
                new ResourceLoader().GetString("LanguageTag"));
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;

            // 订阅视图模型发出的 UI 请求，让视图模型不依赖具体控件。
            ViewModel.NavigationRequested += OnNavigationRequested;
            ViewModel.LoadingOverlayRequested += OnLoadingOverlayRequested;
            ViewModel.DocumentOpenRequested += OnDocumentOpenRequested;
            ViewModel.DocumentPreparationFailed += OnDocumentPreparationFailed;
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
        /// 处理视图模型发出的应用文档打开请求。文件解析和版本自愈已经在服务层完成，
        /// 页面只负责 Windows 文件对象与外部打开确认框的 UI 适配。
        /// </summary>
        private async void OnDocumentOpenRequested(
            object? sender,
            ApplicationDocumentOpenRequestedEventArgs e)
        {
            _ = sender;

            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(e.FilePath);
                if (App.MainWindow is MainWindow window)
                {
                    await window.ExternalLaunch.OpenFileAsync(file);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "把应用文档交给 Windows 打开失败，类型={DocumentKind}", e.Kind);
                try
                {
                    await ShowDocumentFailureAsync(ex.Message);
                }
                catch (Exception dialogException)
                {
                    Log.Error(dialogException, "显示应用文档打开错误对话框失败");
                }
            }
        }

        /// <summary>文档服务返回结构化失败后，由 UI 层加载本地化文本并展示。</summary>
        private async void OnDocumentPreparationFailed(
            object? sender,
            ApplicationDocumentResult result)
        {
            _ = sender;

            try
            {
                await ShowDocumentFailureAsync(result.Diagnostic);
            }
            catch (Exception ex)
            {
                // 事件处理器必须观察展示异常，避免错误处理自身成为未处理异常。
                Log.Error(ex, "显示应用文档错误对话框失败");
            }
        }

        private static Task ShowDocumentFailureAsync(string diagnostic)
        {
            var resources = new ResourceLoader();
            string content = resources.GetString("DocumentOpenFailureMessage");
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                content += Environment.NewLine + Environment.NewLine + diagnostic;
            }

            return DialogService.ShowMessageAsync(
                resources.GetString("DocumentOpenFailureTitle"),
                content,
                resources.GetString("DialogOK"));
        }

        /// <summary>
        /// 将 Provider 注册失败转换为用户可理解的对话框。此处是基础设施诊断进入 UI 的唯一入口，
        /// 因此部署协调器不依赖窗口、资源加载器或 DialogService。
        /// </summary>
        private async void OnProviderRegistrationFailed(object? sender, ProviderRegistrationResult result)
        {
            _ = sender;

            try
            {
                if (result.Status == ProviderRegistrationStatus.DeveloperModeConfirmationRequired &&
                    result.Compensation != Core.Deployment.DeploymentCompensation.Failed)
                {
                    var resourceLoader = new ResourceLoader();
                    // 启动遮罩与首次运行说明可能仍在收尾，统一由 DialogService 排队，避免对话框竞争。
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
                Log.Error(ex, "显示 Provider 注册错误对话框失败");
            }
        }

        /// <summary>
        /// 按结果类别选择简短提示或可复制诊断对话框。
        /// </summary>
        private static async Task ShowProviderRegistrationFailureAsync(ProviderRegistrationResult result)
        {
            var resourceLoader = new ResourceLoader();
            string title = resourceLoader.GetString("EnableProviderFail");

            if (result.Status == ProviderRegistrationStatus.ElevationCancelled &&
                result.Compensation != Core.Deployment.DeploymentCompensation.Failed)
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
                $"Stage: {result.Stage}",
                $"ConfigurationSaved: {result.ConfigurationSaved}",
                $"Compensation: {result.Compensation}",
                $"CompensationError: {result.CompensationError}",
                $"Error: {result.Error}",
                $"Output: {result.Output}");
            string content = resourceLoader.GetString("SomethingErrorsOccurred");
            if (result.ConfigurationSaved)
            {
                content += Environment.NewLine + resourceLoader.GetString("ProviderConfigurationSaved");
            }

            if (result.Compensation == Core.Deployment.DeploymentCompensation.ProviderDisabled)
            {
                content += Environment.NewLine + resourceLoader.GetString("ProviderDeploymentStopped");
            }
            else if (result.Compensation == Core.Deployment.DeploymentCompensation.Failed)
            {
                content += Environment.NewLine + resourceLoader.GetString("ProviderDeploymentRecoveryFailed");
            }

            await DialogService.ShowErrorAsync(
                title,
                $"{content}{Environment.NewLine}{Environment.NewLine}{details}",
                FeedbackSource.ProviderRegistration);
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
                var resourceLoader = new ResourceLoader();
                await DialogService.ShowStartupFailureAsync(
                    resourceLoader.GetString("StartupFailureDialog/Title"),
                    initializationException.ToString());
            }
        }
    }
}
