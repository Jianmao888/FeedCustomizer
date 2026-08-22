using Microsoft.UI.Xaml;
using FeedCustomizer.Core.Tools;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }
        private static Mutex? _mutex;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            args.Handled = true;
            if (MainWindow is MainWindow window)
            {
                try
                {
                    await window.ShowStartupFailureDialogAsync("启动失败", args.Exception.ToString());
                }
                catch
                {
                    // The window may already be closing; keep the process alive for diagnostics.
                }
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 检查是否已有实例在运行
            if (!IsSingleInstance())
            {
                // 已有实例在运行，激活已有窗口并退出
                ActivateExistingWindow();
                Environment.Exit(0);
                return;
            }

            MainWindow = new MainWindow();
            MainWindow.Activate();

            // 先显示带 Splash 的窗口，再异步加载主页面，避免首帧卡顿。
            _ = InitializeAppAfterSplashAsync();
        }

        private static async Task InitializeAppAfterSplashAsync()
        {
            // 让窗口先完成首帧渲染，确保 Splash 遮罩可见。
            await Task.Delay(50);

            if (MainWindow is not MainWindow window)
            {
                return;
            }

            try
            {
                // Make provider files available before the page exposes the toggle.
                await ResourcesCopier.EnsureResourcesReadyAsync();
            }
            catch (Exception ex)
            {
                window.NotifyInitialContentReady();
                await window.FinishLoadingAndHideSplashAsync();
                await window.ShowStartupFailureDialogAsync("启动失败", ex.ToString());
                return;
            }

            window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    window.AppWindow.SetIcon("Assets/AppIcon.ico");
                }
                catch
                {
                    // The packaged icon is optional during unpackaged development.
                }

                window.ApplyMaterial();

                try
                {
                    var loader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
                    window.AppWindow.Title = loader.GetString("Title/Title");
                }
                catch
                {
                    // 资源加载失败时保留 XAML 中的窗口标题。
                }

                window.StartLoadingContent();
            });

            // Keep the startup overlay visible until MainPage initialization is
            // complete. Dialogs are shown only after this fade-out finishes.
            await window.WaitForInitialContentReadyAsync();
            await window.FinishLoadingAndHideSplashAsync();
        }

        /// <summary>
        /// 检查应用程序是否已在运行（使用全局互斥体实现单实例）
        /// </summary>
        private static bool IsSingleInstance()
        {
            // 使用程序集名称创建唯一的互斥体名称
            string mutexName = "Global\\FeedCustomizer_SingleInstance_Mutex";

            try
            {
                // 尝试创建互斥体，如果已存在则返回false
                _mutex = new Mutex(true, mutexName, out bool createdNew);
                return createdNew;
            }
            catch
            {
                // 如果创建失败，允许应用继续运行（降级策略）
                return true;
            }
        }

        /// <summary>
        /// 激活已存在的窗口实例
        /// </summary>
        private static void ActivateExistingWindow()
        {
            try
            {
                // 获取窗口标题
                var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
                var windowTitle = resourceLoader.GetString("Title/Title");
                // 通过窗口标题查找并激活已有窗口
                var windowHandle = FindWindow(null, windowTitle); // 替换为你的实际窗口标题
                if (windowHandle != IntPtr.Zero)
                {
                    // 如果窗口被最小化，先恢复
                    if (IsIconic(windowHandle))
                    {
                        ShowWindow(windowHandle, SW_RESTORE);
                    }
                    // 将窗口置前
                    SetForegroundWindow(windowHandle);
                }
            }
            catch
            {
                // 如果激活失败，静默处理
            }
        }

        // P/Invoke 导入 Win32 API 函数
        [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;
    }
}
