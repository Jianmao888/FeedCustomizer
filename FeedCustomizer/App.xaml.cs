using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Runtime.InteropServices;
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
            var mainInstance = AppInstance.FindOrRegisterForKey("main-instance");

            if (!mainInstance.IsCurrent)
            {
                mainInstance.RedirectActivationToAsync(
                    AppInstance.GetCurrent().GetActivatedEventArgs()
                ).GetAwaiter().GetResult();
                Environment.Exit(0);
                return;
            }

            mainInstance.Activated += (_, _) =>
            {
                MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    if (MainWindow is null)
                    {
                        return;
                    }

                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
                    BringWindowToFront(hwnd);
                });
            };

            MainWindow = new MainWindow();
            MainWindow.Activate();

            _ = InitializeAppAfterSplashAsync();
        }

        private static async Task InitializeAppAfterSplashAsync()
        {
            await Task.Delay(50);

            if (MainWindow is not MainWindow window)
            {
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

            window.DispatcherQueue.TryEnqueue(async () =>
            {
                await window.WaitForInitialContentReadyAsync();
                await window.FinishLoadingAndHideSplashAsync();
            });
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static void BringWindowToFront(IntPtr hwnd)
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, 9);
            }

            SetForegroundWindow(hwnd);
        }
    }
}
