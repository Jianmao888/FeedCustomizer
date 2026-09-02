using FeedCustomizer.Core.Constants;
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
    /// 设置页：只保留与 UI 强相关的代码（导航参数处理、控件焦点、打开外部链接），
    /// 业务逻辑统一放在 SettingsViewModel 中。
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
        public SettingsViewModel ViewModel { get; } = new();

        /// <summary>导航到设置页时是否请求聚焦“解除地区限制”按钮。</summary>
        private bool _focusRegionPolicyButton;

        public SettingsPage()
        {
            InitializeComponent();

            // 订阅视图模型发出的 UI 请求，让视图模型不依赖具体控件。
            ViewModel.OpenLinkRequested += OnOpenLinkRequested;
            ViewModel.LoadingOverlayRequested += OnLoadingOverlayRequested;
            Loaded += SettingsPage_Loaded;
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
