using FeedCustomizer.Core.Interface;
using FeedCustomizer.Core.Tools;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer
{
    /// <summary>
    /// 应用主窗口：只负责窗口自身的初始化、启动流程与 UI 状态。
    /// 弹窗与外部打开分别由 DialogService / ExternalLaunchService 处理。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:验证平台兼容性", Justification = "<挂起>")]
    public partial class MainWindow : Window
    {
        private readonly SemaphoreSlim _dialogGate = new(1, 1);
        private readonly TaskCompletionSource<bool> _initialContentReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _splashHidden =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExternalLaunchService ExternalLaunch { get; }

        public MainWindow()
        {
            InitializeComponent();

            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = AppThemeManager.CurrentTheme;
                root.Loaded += Root_Loaded;
            }

            DialogService.Initialize(DispatcherQueue, GetXamlRoot, WaitForSplashHiddenAsync, _dialogGate);
            ExternalLaunch = new ExternalLaunchService(DispatcherQueue, GetXamlRoot, WaitForSplashHiddenAsync, _dialogGate);

            // 获取窗口信息
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 根据缩放比例确定窗口大小
            uint dpi = GetDpiForWindow(hWnd);
            double scale = dpi / 96.0;

            int width = (int)(560 * scale);
            int height = (int)(800 * scale);
            int X = (int)(520 * scale);
            int Y = (int)(75 * scale);

            SetMinimumWindowSize(hWnd, (int)(560 * scale), (int)(500 * scale));

            // 调整窗口位置和大小，以屏幕像素为单位
            AppWindow.Resize(new SizeInt32(_Width: width, _Height: height));
            AppWindow.Move(new PointInt32(X, Y));

            // 自定义标题栏
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // 订阅窗口关闭事件
            AppWindow.Closing += OnAppWindowClosing;
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (Content is FrameworkElement root)
            {
                root.ActualThemeChanged -= AppThemeManager.OnActualThemeChanged;
                root.ActualThemeChanged += AppThemeManager.OnActualThemeChanged;
            }
        }

        /// <summary>
        /// Loads the initial page after the first window frame has been rendered.
        /// </summary>
        public void StartLoadingContent()
        {
            if (rootFrame.Content is null)
            {
                rootFrame.Navigate(typeof(Pages.MainPage));
            }
        }

        public Task WaitForInitialContentReadyAsync() => _initialContentReady.Task;

        public void NotifyInitialContentReady() => _initialContentReady.TrySetResult(true);

        public Task WaitForSplashHiddenAsync() => _splashHidden.Task;

        public void SetLoadingOverlayVisible(bool isVisible)
        {
            LoadingOverlay.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            LoadingProgressRing.IsActive = isVisible;
        }

        /// <summary>
        /// Fades out the startup overlay after the initial page is ready.
        /// </summary>
        public async Task FinishLoadingAndHideSplashAsync()
        {
            await Task.Delay(500);
            SplashFadeOut.Completed += (_, _) =>
            {
                SplashOverlay.Visibility = Visibility.Collapsed;
                _splashHidden.TrySetResult(true);
            };
            SplashFadeOut.Begin();
        }

        private void AppTitleBar_BackRequested(TitleBar sender, object args)
        {
            _ = sender;
            _ = args;
            if (rootFrame.CanGoBack == true)
            {
                rootFrame.GoBack();
            }
        }

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            Debug.WriteLine("窗口再关闭");
            var currentPage = GetCurrentPage();
            if (currentPage is IWindowCloseAware awarePage)
            {
                if (!awarePage.CanClose())
                {
                    args.Cancel = true; // 阻止窗口关闭
                    return;
                }
                else
                {
                    // 如果不阻止关闭，可以执行清理逻辑
                    awarePage.OnWindowClosing();
                }
            }
        }

        private Page? GetCurrentPage()
        {
            if (rootFrame is Frame frame && frame.Content is Page page)
            {
                return page;
            }
            return null;
        }

        private XamlRoot? GetXamlRoot() =>
            GetCurrentPage()?.XamlRoot ?? (Content as FrameworkElement)?.XamlRoot;
    }
}