using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Documents;
using FeedCustomizer.Core.Feedback;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// 设置页：只保留与 UI 强相关的代码（导航参数处理、控件焦点、打开外部链接），
    /// 业务逻辑统一放在 SettingsViewModel 中。
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        private static readonly IAppLog Log = AppLog.For<SettingsPage>();

        /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
        public SettingsViewModel ViewModel { get; }

        /// <summary>导航到设置页时是否请求聚焦“解除地区限制”按钮。</summary>
        private bool _focusRegionPolicyButton;

        public SettingsPage()
        {
            FeedbackService feedbackService = App.MainWindow?.Feedback
                ?? throw new InvalidOperationException("主窗口反馈服务尚未初始化。");
            MainWindow window = App.MainWindow
                ?? throw new InvalidOperationException("主窗口文档服务尚未初始化。");
            ViewModel = new SettingsViewModel(
                feedbackService,
                window.Documents,
                new ResourceLoader().GetString("LanguageTag"));
            InitializeComponent();

            // 订阅视图模型发出的 UI 请求，让视图模型不依赖具体控件。
            ViewModel.OpenLinkRequested += OnOpenLinkRequested;
            ViewModel.DocumentOpenRequested += OnDocumentOpenRequested;
            ViewModel.DocumentPreparationFailed += OnDocumentPreparationFailed;
            ViewModel.LoadingOverlayRequested += OnLoadingOverlayRequested;
            ViewModel.MessageRequested += OnMessageRequested;
            ViewModel.ErrorRequested += OnErrorRequested;
            Loaded += SettingsPage_Loaded;
        }

        /// <summary>错误样式和反馈按钮由页面交给统一 UI 服务处理。</summary>
        private async void OnErrorRequested(object? sender, SettingsErrorRequestedEventArgs e)
        {
            _ = sender;

            try
            {
                await DialogService.ShowErrorAsync(e.Title, e.Details, e.Source);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "显示设置页可反馈错误失败");
            }
        }

        /// <summary>将视图模型产生的普通结果提示转换为 UI 对话框。</summary>
        private async void OnMessageRequested(object? sender, SettingsMessageRequestedEventArgs e)
        {
            _ = sender;

            try
            {
                await DialogService.ShowMessageAsync(e.Title, e.Message, e.CloseButtonText);
            }
            catch (Exception ex)
            {
                // 事件处理器必须吸收并记录展示异常，不能让通知失败变成未处理的 UI 异常。
                Log.Error(ex, "显示设置页反馈结果失败");
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _focusRegionPolicyButton = e.Parameter is string parameter &&
                parameter == "RegionPolicy";
        }

        /// <summary>
        /// 页面加载完成后启动视图模型初始化，并按需聚焦“解除地区限制”按钮。
        /// </summary>
        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            // 设置项已在 ViewModel 构造函数中读取，这里只启动需要窗口句柄的异步初始化。
            _ = ViewModel.InitializeAsync(GetWindowHandle());

            if (_focusRegionPolicyButton)
            {
                _focusRegionPolicyButton = false;
                FocusRegionPolicyButton();
            }
        }

        /// <summary>将焦点移动到“解除地区限制”按钮（纯 UI 逻辑）。</summary>
        private void FocusRegionPolicyButton()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UnlockRegionPolicyButton.StartBringIntoView();
                UnlockRegionPolicyButton.Focus(FocusState.Keyboard);
            });
        }

        /// <summary>获取主窗口句柄，供 Store 购买与许可证查询使用。</summary>
        private static IntPtr GetWindowHandle()
        {
            return App.MainWindow is MainWindow window
                ? WinRT.Interop.WindowNative.GetWindowHandle(window)
                : IntPtr.Zero;
        }

        /// <summary>处理视图模型发出的打开外部链接请求。</summary>
        private async void OnOpenLinkRequested(object? sender, string url)
        {
            _ = sender;

            if (App.MainWindow is MainWindow window)
            {
                await window.ExternalLaunch.OpenLinkAsync(url);
            }
        }

        /// <summary>使用与帮助文档一致的受控路径和系统关联程序打开随包文档。</summary>
        private async void OnDocumentOpenRequested(
            object? sender,
            ApplicationDocumentOpenRequestedEventArgs e)
        {
            _ = sender;
            await ApplicationDocumentUi.OpenAsync(e);
        }

        /// <summary>文档准备失败时复用帮助文档的本地化错误提示。</summary>
        private async void OnDocumentPreparationFailed(
            object? sender,
            ApplicationDocumentResult result)
        {
            _ = sender;

            try
            {
                await ApplicationDocumentUi.ShowFailureAsync(result.Diagnostic);
            }
            catch (Exception ex)
            {
                // 事件处理器必须观察 UI 展示失败，不能让错误提示本身形成未处理异常。
                Log.Error(ex, "显示设置页应用文档错误对话框失败");
            }
        }

        /// <summary>打开贡献者链接：只从 UI 元素中取出数据对象，其余逻辑交给视图模型。</summary>
        private void OnContributorLinkClicked(object sender, RoutedEventArgs _)
        {
            if (sender is FrameworkElement { Tag: Contributor contributor })
            {
                ViewModel.OpenContributorLink(contributor);
            }
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
    }
}
