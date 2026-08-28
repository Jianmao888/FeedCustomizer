using FeedCustomizer.Core.Tools;
using FeedCustomizer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.ComponentModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// “添加/编辑源”页面：只保留与 UI 强相关的代码（导航、遮罩、对话框、图片预览与图标模式的可视化切换），
    /// 业务逻辑统一放在 FeedViewModel 中。
    /// </summary>
    public sealed partial class AddFeedPage : Page
    {
        /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
        public FeedViewModel ViewModel { get; private set; } = new();

        public AddFeedPage()
        {
            InitializeComponent();
            AttachViewModel(ViewModel);
            UpdateIconModeVisibility();
            UpdateImagePreviews();
        }

        /// <summary>
        /// 页面导航到时，若携带了 FeedViewModel 参数则切换为编辑模式。
        /// </summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is FeedViewModel feedViewModel)
            {
                AttachViewModel(feedViewModel);
                UpdateIconModeVisibility();
                UpdateImagePreviews();
            }
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);

            // 离开页面时撤销未提交的更改。
            ViewModel.CancelChanges();
        }

        /// <summary>
        /// 切换当前视图模型并重新订阅其 UI 请求事件。
        /// </summary>
        private void AttachViewModel(FeedViewModel viewModel)
        {
            // 先解除旧实例的订阅，再绑定新实例（首次调用时旧实例尚未订阅，解除为无害操作）。
            ViewModel.SaveCompleted -= OnSaveCompleted;
            ViewModel.LoadingOverlayRequested -= OnLoadingOverlayRequested;
            ViewModel.WebIconFetchErrorRequested -= OnWebIconFetchErrorRequested;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

            ViewModel = viewModel;

            ViewModel.SaveCompleted += OnSaveCompleted;
            ViewModel.LoadingOverlayRequested += OnLoadingOverlayRequested;
            ViewModel.WebIconFetchErrorRequested += OnWebIconFetchErrorRequested;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        /// <summary>
        /// 视图模型属性变化时，刷新仅与 UI 相关的展示状态。
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            _ = sender;

            if (e.PropertyName is nameof(FeedViewModel.IsWebsiteIconMode))
            {
                UpdateIconModeVisibility();
            }
            else if (e.PropertyName is nameof(FeedViewModel.PreviewImage)
                     or nameof(FeedViewModel.UsesDefaultImage))
            {
                UpdateImagePreviews();
            }
        }

        /// <summary>
        /// 根据图标模式切换两组控件的可见性（纯 UI 逻辑）。
        /// </summary>
        private void UpdateIconModeVisibility()
        {
            bool isWebsiteIconMode = ViewModel.IsWebsiteIconMode;
            WebsiteImageControls.Visibility = isWebsiteIconMode
                ? Visibility.Visible
                : Visibility.Collapsed;
            CustomImageControls.Visibility = isWebsiteIconMode
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>
        /// 根据是否使用默认图片切换占位图标与预览图的可见性（纯 UI 逻辑）。
        /// 图片内容本身通过 x:Bind 绑定到 ViewModel.PreviewImage。
        /// </summary>
        private void UpdateImagePreviews()
        {
            Visibility placeholderVisibility = ViewModel.UsesDefaultImage
                ? Visibility.Visible
                : Visibility.Collapsed;
            Visibility imageVisibility = ViewModel.UsesDefaultImage
                ? Visibility.Collapsed
                : Visibility.Visible;

            WebsiteDefaultIcon.Visibility = placeholderVisibility;
            CustomDefaultIcon.Visibility = placeholderVisibility;
            WebsitePreviewImage.Visibility = imageVisibility;
            PreviewImage.Visibility = imageVisibility;
        }

        /// <summary>
        /// 保存完成后返回主页；无返回栈时补导航，避免停留在当前页。
        /// </summary>
        private void OnSaveCompleted(object? sender, EventArgs e)
        {
            _ = sender;
            _ = e;

            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            else
            {
                Frame.Navigate(typeof(MainPage));
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

        /// <summary>
        /// 处理视图模型发出的“网页图标获取失败”对话框请求。
        /// </summary>
        private async void OnWebIconFetchErrorRequested(object? sender, string details)
        {
            _ = sender;

            await DialogService.ShowWebIconFetchErrorAsync(details);
        }

        /// <summary>
        /// 取消：直接返回上一页（导航属于 UI 生命周期）。
        /// </summary>
        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            Frame.GoBack();
        }
    }
}
