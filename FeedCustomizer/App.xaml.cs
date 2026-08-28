using FeedCustomizer.Core.Tools;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.UI;

namespace FeedCustomizer
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }

        public App() => InitializeComponent();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var mainInstance = AppInstance.FindOrRegisterForKey("main-instance");
            if (!mainInstance.IsCurrent)
            {
                mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                    .GetAwaiter().GetResult();
                Environment.Exit(0);
                return;
            }

            mainInstance.Activated += (_, _) => MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
                BringWindowToFront(hwnd);
            });

            AppThemeManager.LoadSettings();
            MainWindow = new MainWindow();
            AppThemeManager.SetupTitleBar();
            MainWindow.Activate();
            _ = InitializeAppAfterSplashAsync();
        }

        private static async Task InitializeAppAfterSplashAsync()
        {
            await Task.Delay(50);
            if (MainWindow is not MainWindow window) return;

            window.DispatcherQueue.TryEnqueue(() =>
            {
                try { window.AppWindow.SetIcon("Assets/AppIcon.ico"); } catch { }
                AppThemeManager.ApplyMaterial();
                try { window.AppWindow.Title = new ResourceLoader().GetString("Title/Title"); } catch { }
                window.StartLoadingContent();
            });

            // 主页面会在数据源初始化完成（或失败）后通知这里。
            // 在此之前保持 Splash，避免用户看到尚未准备好的禁用控件。
            await window.WaitForInitialContentReadyAsync();
            await window.FinishLoadingAndHideSplashAsync();
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
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
            SetForegroundWindow(hwnd);
        }
    }

    public static class AppThemeManager
    {
        public static ElementTheme CurrentTheme = ElementTheme.Default;
        public static BackgroundMaterial CurrentMaterial = BackgroundMaterial.Mica;

        public static void LoadSettings()
        {
            try { CurrentTheme = SettingsLoader.GetAppTheme() switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default }; }
            catch { CurrentTheme = ElementTheme.Default; }
            try { CurrentMaterial = SettingsLoader.GetAppMaterial() switch { "MicaAlt" => BackgroundMaterial.MicaAlt, "Acrylic" => BackgroundMaterial.Acrylic, _ => BackgroundMaterial.Mica }; }
            catch { CurrentMaterial = BackgroundMaterial.Mica; }
            ElementSoundPlayer.State = ElementSoundPlayerState.Off;
        }

        public static void ApplyMaterial()
        {
            if (App.MainWindow is null) return;
            try
            {
                if (App.MainWindow.SystemBackdrop is MicaBackdrop mica)
                {
                    if (CurrentMaterial == BackgroundMaterial.Mica && mica.Kind == MicaKind.Base) return;
                    if (CurrentMaterial == BackgroundMaterial.MicaAlt && mica.Kind == MicaKind.BaseAlt) return;
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
            catch (Exception ex) { Debug.WriteLine($"ApplyMaterial failed: {ex.Message}"); App.MainWindow.SystemBackdrop = null; }
        }

        public static void SetupTitleBar()
        {
            if (App.MainWindow is null) return;
            try
            {
                if (!AppWindowTitleBar.IsCustomizationSupported()) return;
                var titleBar = App.MainWindow.AppWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                UpdateTitleBarColors();
            }
            catch (Exception ex) { Debug.WriteLine($"SetupTitleBar failed: {ex.Message}"); }
        }

        public static void UpdateTitleBarColors()
        {
            if (App.MainWindow is null || !AppWindowTitleBar.IsCustomizationSupported()) return;
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
            catch (Exception ex) { Debug.WriteLine($"UpdateTitleBarColors failed: {ex.Message}"); }
        }

        public static void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateTitleBarColors();

        public static bool GetIsDarkTheme()
        {
            if (App.MainWindow?.Content is FrameworkElement root && root.ActualTheme != ElementTheme.Default)
                return root.ActualTheme == ElementTheme.Dark;
            return CurrentTheme == ElementTheme.Default ? Application.Current.RequestedTheme == ApplicationTheme.Dark : CurrentTheme == ElementTheme.Dark;
        }
    }

    public enum BackgroundMaterial { Mica, MicaAlt, Acrylic }
}
