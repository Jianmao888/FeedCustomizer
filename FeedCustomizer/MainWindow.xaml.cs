using FeedCustomizer.Core.Interface;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.Dialogs;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:验证平台兼容性", Justification = "<挂起>")]
    public partial class MainWindow : Window
    {
        public static MainWindow? Instance { get; private set; }
        private readonly ApplicationDataContainer _localSettings = ApplicationData.Current.LocalSettings;
        private const int GwlWndProc = -4;
        private const uint WmGetMinMaxInfo = 0x0024;

        private readonly SemaphoreSlim _dialogGate = new(1, 1);
        private readonly TaskCompletionSource<bool> _initialContentReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _splashHidden =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WindowProcDelegate? _windowProcDelegate;
        private IntPtr _originalWindowProc;
        private int _minimumWindowWidth;
        private int _minimumWindowHeight;

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = AppThemeManager.CurrentTheme;
                root.Loaded += Root_Loaded;
            }

            // 获取窗口信息
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 根据缩放比例确定窗口大小
            uint dpi = GetDpiForWindow(hWnd);
            double scale = dpi / 96.0;

            int width = (int)(560 * scale);
            int height = (int)(800 * scale);
            int X = (int)(520 * scale);
            int Y = (int)(150 * scale);

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

        public void ApplyMaterial()
        {
            AppThemeManager.ApplyMaterial();
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
                bool sound = _localSettings.Values["EnableSound"] is bool value ? value : true;
                ElementSoundPlayer.State = sound ? ElementSoundPlayerState.On : ElementSoundPlayerState.Off;
            };
            SplashFadeOut.Begin();
        }

        public async Task ShowMessageDialogAsync(string title, string content, string closeButtonText)
        {
            await WaitForSplashHiddenAsync();

            if (!DispatcherQueue.HasThreadAccess)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await ShowMessageDialogAsync(title, content, closeButtonText);
                        completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }))
                {
                    completion.TrySetException(new InvalidOperationException("无法切换到窗口 UI 线程。"));
                }
                await completion.Task;
                return;
            }

            await _dialogGate.WaitAsync();
            try
            {
                var xamlRoot = GetCurrentPage()?.XamlRoot ?? (Content as FrameworkElement)?.XamlRoot;
                if (xamlRoot is null)
                {
                    return;
                }

                var dialog = new MessageDialog
                {
                    XamlRoot = xamlRoot
                };
                dialog.Configure(title, content, closeButtonText);
                await dialog.ShowAsync();
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        public async Task ShowStartupFailureDialogAsync(string title, string details)
        {
            await WaitForSplashHiddenAsync();

            if (!DispatcherQueue.HasThreadAccess)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await ShowStartupFailureDialogAsync(title, details);
                        completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }))
                {
                    completion.TrySetException(new InvalidOperationException("无法切换到窗口 UI 线程。"));
                }
                await completion.Task;
                return;
            }

            await _dialogGate.WaitAsync();
            try
            {
                var xamlRoot = GetCurrentPage()?.XamlRoot ?? (Content as FrameworkElement)?.XamlRoot;
                if (xamlRoot is null)
                {
                    return;
                }

                var dialog = new StartupFailureDialog
                {
                    XamlRoot = xamlRoot
                };
                dialog.Configure(title, details);
                await dialog.ShowAsync();
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        public async Task ShowWebIconFetchErrorDialogAsync(string details)
        {
            await WaitForSplashHiddenAsync();

            if (!DispatcherQueue.HasThreadAccess)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await ShowWebIconFetchErrorDialogAsync(details);
                        completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }))
                {
                    completion.TrySetException(new InvalidOperationException("无法切换到窗口 UI 线程。"));
                }
                await completion.Task;
                return;
            }

            await _dialogGate.WaitAsync();
            try
            {
                var xamlRoot = GetCurrentPage()?.XamlRoot ?? (Content as FrameworkElement)?.XamlRoot;
                if (xamlRoot is null)
                {
                    return;
                }

                var dialog = new WebIconFetchErrorDialog
                {
                    XamlRoot = xamlRoot
                };
                dialog.Configure(details);
                await dialog.ShowAsync();
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        public async Task<bool> OpenExternalLinkAsync(string url)
        {
            await WaitForSplashHiddenAsync();

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var xamlRoot = GetCurrentPage()?.XamlRoot ?? (Content as FrameworkElement)?.XamlRoot;
            if (xamlRoot is null)
            {
                return false;
            }

            await _dialogGate.WaitAsync();
            try
            {
                var dialog = new ExternalOpenDialog
                {
                    XamlRoot = xamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return false;
                }

                return await Launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open external link: {ex}");
                return false;
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        public async Task<bool> OpenExternalFileAsync(StorageFile file)
        {
            await WaitForSplashHiddenAsync();

            var xamlRoot = GetCurrentPage()?.XamlRoot ?? (Content as FrameworkElement)?.XamlRoot;
            if (xamlRoot is null)
            {
                return false;
            }

            await _dialogGate.WaitAsync();
            try
            {
                var dialog = new ExternalOpenDialog
                {
                    XamlRoot = xamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return false;
                }

                return await Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open external file: {ex}");
                return false;
            }
            finally
            {
                _dialogGate.Release();
            }
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
            // 在这里可以阻止窗口关闭
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
            // 如果你的页面在 Frame 中
            if (rootFrame is Frame frame && frame.Content is Page page)
            {
                return page;
            }
            return null;
        }

        private void SetMinimumWindowSize(IntPtr hWnd, int minimumWidth, int minimumHeight)
        {
            _minimumWindowWidth = minimumWidth;
            _minimumWindowHeight = minimumHeight;
            _windowProcDelegate = WindowProc;
            _originalWindowProc = SetWindowLongPtr(
                hWnd,
                GwlWndProc,
                Marshal.GetFunctionPointerForDelegate(_windowProcDelegate));
        }

        private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmGetMinMaxInfo)
            {
                var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                minMaxInfo.ptMinTrackSize.X = _minimumWindowWidth;
                minMaxInfo.ptMinTrackSize.Y = _minimumWindowHeight;
                Marshal.StructureToPtr(minMaxInfo, lParam, false);
                return IntPtr.Zero;
            }

            return CallWindowProc(_originalWindowProc, hWnd, message, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public Point ptReserved;
            public Point ptMaxSize;
            public Point ptMaxPosition;
            public Point ptMinTrackSize;
            public Point ptMaxTrackSize;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WindowProcDelegate(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static partial IntPtr SetWindowLongPtr(
            IntPtr hWnd,
            int index,
            IntPtr newLong);

        [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static partial IntPtr CallWindowProc(
            IntPtr windowProc,
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint GetDpiForWindow(IntPtr hwnd);
    }
}
