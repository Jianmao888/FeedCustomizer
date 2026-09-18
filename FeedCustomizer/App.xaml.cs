using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Tools;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.UI;

namespace FeedCustomizer
{
    public partial class App : Application
    {
        private static readonly IAppLog Log = AppLog.For<App>();

        /// <summary>应用唯一主窗口。使用具体类型以暴露启动协调和窗口服务能力。</summary>
        public static MainWindow? MainWindow { get; private set; }

        public App()
        {
            // 日志先于 XAML 初始化建立，确保资源加载或全局异常同样能够落盘诊断。
            AppLog.Initialize();
            RegisterGlobalExceptionHandlers();

            try
            {
                InitializeComponent();
                SystemInfoLogWriter.WriteStartupEnvironment();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "应用对象初始化失败");
                AppLog.CloseAndFlush();
                throw;
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Log.Information("应用启动激活，参数长度={ArgumentsLength}", args.Arguments?.Length ?? 0);

            var mainInstance = AppInstance.FindOrRegisterForKey("main-instance");
            if (!mainInstance.IsCurrent)
            {
                Log.Information("检测到已有主实例，正在转发激活请求");
                mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                    .GetAwaiter().GetResult();
                AppLog.CloseAndFlush();
                Environment.Exit(0);
                return;
            }

            mainInstance.Activated += (_, _) =>
            {
                Log.Debug("收到辅助实例转发的激活请求");
                MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
                    BringWindowToFront(hwnd);
                });
            };

            AppThemeManager.LoadSettings();
            MainWindow = new MainWindow();
            AppThemeManager.SetupTitleBar();
            MainWindow.Activate();
            InitializeMainWindow(MainWindow);
            Log.Information("主窗口已激活，启动内容开始加载");
        }

        private void RegisterGlobalExceptionHandlers()
        {
            UnhandledException += OnXamlUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private static void OnXamlUnhandledException(
            object sender,
            Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            _ = sender;
            Log.Fatal(args.Exception, "WinUI 捕获到未处理异常");
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            _ = sender;
            // 只记录而不调用 SetObserved，日志能力不能改变运行时原有的异常处理语义。
            Log.Error(args.Exception, "捕获到未观察的任务异常");
        }

        private static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
        {
            _ = sender;
            if (args.ExceptionObject is Exception exception)
            {
                Log.Fatal(exception, "AppDomain 捕获到未处理异常，进程即将终止={IsTerminating}", args.IsTerminating);
                return;
            }

            Log.Error("AppDomain 捕获到非 Exception 类型的未处理错误，进程即将终止={IsTerminating}", args.IsTerminating);
        }

        private static void OnProcessExit(object? sender, EventArgs args)
        {
            _ = sender;
            _ = args;
            AppLog.CloseAndFlush();
        }

        /// <summary>
        /// 完成窗口激活后的轻量 UI 配置，并将后续启动流程交给主窗口协调。
        /// App 不再等待页面回传信号，避免启动完成条件分散在 App、窗口和页面中。
        /// </summary>
        private static void InitializeMainWindow(MainWindow window)
        {
            try
            {
                window.AppWindow.SetIcon("Assets/AppIcon.ico");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "设置窗口图标失败，继续使用系统默认图标");
            }

            AppThemeManager.ApplyMaterial();
            try
            {
                window.AppWindow.Title = new ResourceLoader().GetString("Title/Title");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取窗口标题资源失败");
            }

            window.StartLoadingContent();
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr hWnd);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        private static void BringWindowToFront(IntPtr hwnd)
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, 9);
            }

            SetForegroundWindow(hwnd);
        }
    }

    public static class AppThemeManager
    {
        private static readonly IAppLog Log = AppLog.For(nameof(AppThemeManager));

        public static ElementTheme CurrentTheme = ElementTheme.Default;
        public static BackgroundMaterial CurrentMaterial = BackgroundMaterial.Mica;

        public static void LoadSettings()
        {
            try
            {
                CurrentTheme = SettingsLoader.GetAppTheme() switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取主题设置失败，回退为系统默认主题");
                CurrentTheme = ElementTheme.Default;
            }

            try
            {
                CurrentMaterial = SettingsLoader.GetAppMaterial() switch
                {
                    "MicaAlt" => BackgroundMaterial.MicaAlt,
                    "Acrylic" => BackgroundMaterial.Acrylic,
                    _ => BackgroundMaterial.Mica
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取背景材质设置失败，回退为 Mica");
                CurrentMaterial = BackgroundMaterial.Mica;
            }

            ElementSoundPlayer.State = ElementSoundPlayerState.Off;
        }

        public static void ApplyMaterial()
        {
            if (App.MainWindow is null)
            {
                return;
            }

            try
            {
                if (App.MainWindow.SystemBackdrop is MicaBackdrop mica)
                {
                    if (CurrentMaterial == BackgroundMaterial.Mica && mica.Kind == MicaKind.Base)
                    {
                        return;
                    }

                    if (CurrentMaterial == BackgroundMaterial.MicaAlt && mica.Kind == MicaKind.BaseAlt)
                    {
                        return;
                    }
                }
                else if (App.MainWindow.SystemBackdrop is DesktopAcrylicBackdrop &&
                         CurrentMaterial == BackgroundMaterial.Acrylic)
                {
                    return;
                }

                App.MainWindow.SystemBackdrop = CurrentMaterial switch
                {
                    BackgroundMaterial.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                    BackgroundMaterial.Acrylic => new DesktopAcrylicBackdrop(),
                    _ => new MicaBackdrop { Kind = MicaKind.Base }
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "应用窗口背景材质失败，已回退为无系统背景");
                App.MainWindow.SystemBackdrop = null;
            }
        }

        public static void SetupTitleBar()
        {
            if (App.MainWindow is null)
            {
                return;
            }

            try
            {
                if (!AppWindowTitleBar.IsCustomizationSupported())
                {
                    return;
                }

                var titleBar = App.MainWindow.AppWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                UpdateTitleBarColors();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "设置自定义标题栏失败");
            }
        }

        public static void UpdateTitleBarColors()
        {
            if (App.MainWindow is null || !AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            try
            {
                var titleBar = App.MainWindow.AppWindow.TitleBar;
                bool dark = GetIsDarkTheme();
                var foreground = dark ? Colors.White : Colors.Black;
                var inactive = dark ? Color.FromArgb(255, 128, 128, 128) : Color.FromArgb(255, 160, 160, 160);
                var hover = dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonInactiveForegroundColor = inactive;
                titleBar.ButtonHoverBackgroundColor = hover;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(30, hover.R, hover.G, hover.B);
                titleBar.ButtonPressedForegroundColor = foreground;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "更新标题栏颜色失败");
            }
        }

        public static void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateTitleBarColors();

        public static bool GetIsDarkTheme()
        {
            if (App.MainWindow?.Content is FrameworkElement root && root.ActualTheme != ElementTheme.Default)
            {
                return root.ActualTheme == ElementTheme.Dark;
            }

            return CurrentTheme == ElementTheme.Default ? Application.Current.RequestedTheme == ApplicationTheme.Dark : CurrentTheme == ElementTheme.Dark;
        }
    }

    public enum BackgroundMaterial
    {
        Mica,
        MicaAlt,
        Acrylic
    }
}
