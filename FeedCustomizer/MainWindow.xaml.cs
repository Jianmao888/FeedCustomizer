using FeedCustomizer.Core.Interface;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Tools;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using FeedCustomizer.Pages;
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
        private static readonly IAppLog Log = AppLog.For<MainWindow>();
        private readonly SemaphoreSlim _dialogGate = new(1, 1);
        private readonly TaskCompletionSource<bool> _startupVisualsHidden =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _startupHasBegun;
        private bool _startupUsesLoadingOverlay;

        public ExternalLaunchService ExternalLaunch { get; }

        public MainWindow()
        {
            InitializeComponent();

            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = AppThemeManager.CurrentTheme;
                root.Loaded += Root_Loaded;
            }

            DialogService.Initialize(DispatcherQueue, GetXamlRoot, WaitForStartupVisualsHiddenAsync, _dialogGate);
            ExternalLaunch = new ExternalLaunchService(DispatcherQueue, GetXamlRoot, WaitForStartupVisualsHiddenAsync, _dialogGate);

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
        /// 加载主页并启动唯一的窗口级启动编排。
        /// </summary>
        public void StartLoadingContent()
        {
            if (_startupHasBegun || rootFrame.Content is not null)
            {
                Log.Debug("忽略重复的启动内容加载请求");
                return;
            }

            _startupHasBegun = true;
            Log.Information("开始导航主页并执行启动协调流程");
            rootFrame.Navigated += OnInitialPageNavigated;
            rootFrame.Navigate(typeof(Pages.MainPage));
        }

        private void OnInitialPageNavigated(object sender, NavigationEventArgs e)
        {
            _ = sender;
            rootFrame.Navigated -= OnInitialPageNavigated;

            if (e.Content is MainPage mainPage)
            {
                _ = RunStartupAsync(mainPage);
                return;
            }

            // 理论上不会发生；仍需解除启动遮罩，避免导航异常时窗口永久停在徽标页。
            Log.Error("主页导航完成但未取得 MainPage 实例，正在强制结束启动遮罩");
            _ = CompleteStartupAsync();
        }

        /// <summary>
        /// 等待启动遮罩完全退出。首次运行和启动失败对话框必须在此之后显示。
        /// </summary>
        public Task WaitForStartupVisualsHiddenAsync() => _startupVisualsHidden.Task;

        public void SetLoadingOverlayVisible(bool isVisible)
        {
            LoadingOverlay.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            LoadingProgressRing.IsActive = isVisible;
        }

        /// <summary>
        /// 在窗口层编排主页初始化。资源同步判定一旦完成，就立即把徽标覆盖层切换为
        /// 可反馈进度的加载覆盖层；无论初始化成功还是失败，最终都会释放启动遮罩。
        /// </summary>
        private async Task RunStartupAsync(MainPage mainPage)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Exception? initializationException = null;
            try
            {
                bool synchronizationRequired = await mainPage.ResourceSynchronizationRequiredTask;
                Log.Information("启动资源检查完成，需要同步={SynchronizationRequired}", synchronizationRequired);
                if (synchronizationRequired)
                {
                    ShowStartupLoadingOverlay();
                }

                await mainPage.InitializationTask;
                Log.Information("主页启动初始化完成，耗时毫秒={ElapsedMilliseconds}", stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                initializationException = ex;
                Log.Error(ex, "应用启动初始化失败，阶段耗时毫秒={ElapsedMilliseconds}", stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                await CompleteStartupAsync();
            }

            try
            {
                await mainPage.ShowStartupCompletionDialogsAsync(initializationException);
            }
            catch (Exception ex)
            {
                // 对话框失败不能影响已完成的启动状态；保留诊断以便后续排查。
                Log.Error(ex, "启动完成后的页面交互失败");
            }
            finally
            {
                stopwatch.Stop();
                Log.Information(
                    "启动协调流程结束，成功={Succeeded}，总耗时毫秒={ElapsedMilliseconds}",
                    initializationException is null,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// 资源同步属于长耗时操作。此时保留已加载的主页，并以加载覆盖层替代静态徽标。
        /// </summary>
        private void ShowStartupLoadingOverlay()
        {
            if (SplashOverlay.Visibility == Visibility.Visible)
            {
                SplashOverlay.Visibility = Visibility.Collapsed;
            }

            _startupUsesLoadingOverlay = true;
            SetLoadingOverlayVisible(true);
            Log.Information("资源同步耗时较长，启动视觉已从徽标切换为加载覆盖层");
        }

        /// <summary>
        /// 统一结束启动视觉状态。未发生资源同步时保留淡出；发生同步时直接关闭加载覆盖层。
        /// </summary>
        private Task CompleteStartupAsync()
        {
            if (_startupUsesLoadingOverlay)
            {
                SetLoadingOverlayVisible(false);
                _startupVisualsHidden.TrySetResult(true);
                return Task.CompletedTask;
            }

            if (SplashOverlay.Visibility != Visibility.Visible)
            {
                _startupVisualsHidden.TrySetResult(true);
                return Task.CompletedTask;
            }

            SplashFadeOut.Completed += (_, _) =>
            {
                SplashOverlay.Visibility = Visibility.Collapsed;
                _startupVisualsHidden.TrySetResult(true);
            };
            SplashFadeOut.Begin();
            return Task.CompletedTask;
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
            _ = sender;
            Log.Debug("收到主窗口关闭请求");
            var currentPage = GetCurrentPage();
            if (currentPage is IWindowCloseAware awarePage)
            {
                if (!awarePage.CanClose())
                {
                    // 页面仍有未完成的用户操作时保留窗口，日志生命周期也继续保持打开。
                    args.Cancel = true;
                    Log.Information("当前页面拒绝关闭窗口");
                    return;
                }

                awarePage.OnWindowClosing();
            }

            Log.Information("主窗口即将关闭");
            AppLog.CloseAndFlush();
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
