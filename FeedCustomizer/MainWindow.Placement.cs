using FeedCustomizer.Core.Infrastructure.Windowing;
using FeedCustomizer.Core.Windowing;
using Microsoft.UI.Windowing;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace FeedCustomizer;

public partial class MainWindow
{
    private WindowPlacementStore? _windowPlacementStore;
    private WindowPlacementState? _normalWindowPlacement;
    private IntPtr _windowHandle;
    private bool _lastNonMinimizedWasMaximized;

    /// <summary>
    /// 在窗口激活前恢复或计算初始矩形。先移动到目标显示器再读取 DPI，
    /// 因此不会把创建窗口时所在显示器的缩放比例错误地用于另一台显示器。
    /// </summary>
    private void InitializeWindowPlacement(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        SetMinimumWindowSize(windowHandle);

        WindowPlacementState? savedState = null;
        try
        {
            _windowPlacementStore = new WindowPlacementStore();
            savedState = _windowPlacementStore.Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            // 布局属于可再生偏好；损坏或不可读时回退居中，不能阻止主窗口创建。
            Log.Warning(ex, "读取窗口布局失败，正在使用默认居中布局");
        }

        try
        {
            if (savedState is null)
            {
                ApplyDefaultPlacement();
            }
            else
            {
                ApplySavedPlacement(savedState);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or OverflowException or COMException)
        {
            Log.Warning(ex, "应用窗口布局失败，正在使用安全回退位置");
            try
            {
                ApplyEmergencyPlacement();
            }
            catch (Exception fallbackException) when (fallbackException is ArgumentException or InvalidOperationException or COMException)
            {
                // 连安全位置也无法应用时保留 Windows 的默认位置；窗口仍应继续创建并保持可诊断。
                Log.Error(fallbackException, "应用窗口安全回退位置失败，保留 Windows 默认布局");
            }
        }

        // 初始 Move/Resize 完成后才订阅，避免把应用自己的恢复动作误记为用户调整。
        AppWindow.Changed += OnAppWindowChanged;
    }

    private void ApplyDefaultPlacement()
    {
        DisplayArea displayArea = DisplayArea.Primary;
        WindowRectangle workArea = ToRectangle(displayArea.WorkArea);

        // 窗口尚未激活，临时移动不会闪烁；GetDpiForWindow 随后返回主显示器的缩放比例。
        AppWindow.Move(new PointInt32 { X = workArea.X, Y = workArea.Y });
        int dpi = GetSafeWindowDpi(_windowHandle);
        WindowRectangle placement = WindowPlacementPolicy.CreateCenteredDefault(workArea, dpi);
        ApplyNormalRectangle(placement, dpi, wasMaximized: false);
        Log.Information(
            "未找到有效窗口布局，已在主显示器居中，DPI={Dpi}，窗口大小={Width}x{Height}",
            dpi,
            placement.Width,
            placement.Height);
    }

    private void ApplySavedPlacement(WindowPlacementState savedState)
    {
        WindowPlacementPolicy.ValidateState(savedState);
        var savedRectangle = new RectInt32
        {
            X = savedState.X,
            Y = savedState.Y,
            Width = savedState.Width,
            Height = savedState.Height,
        };
        DisplayArea displayArea = DisplayArea.GetFromRect(savedRectangle, DisplayAreaFallback.Nearest)
            ?? DisplayArea.Primary;
        WindowRectangle workArea = ToRectangle(displayArea.WorkArea);

        // 先进入目标显示器再获取 DPI。此时窗口尚不可见，所以不会产生用户可见的二次移动。
        AppWindow.Move(new PointInt32 { X = workArea.X, Y = workArea.Y });
        int targetDpi = GetSafeWindowDpi(_windowHandle);
        WindowRectangle placement = WindowPlacementPolicy.Restore(savedState, workArea, targetDpi);
        ApplyNormalRectangle(placement, targetDpi, savedState.WasMaximized);

        if (savedState.WasMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // 先应用正常矩形再最大化，以便用户还原窗口时回到上次的正常位置和大小。
            presenter.Maximize();
        }

        Log.Information(
            "窗口布局已恢复，目标DPI={Dpi}，最大化={WasMaximized}，窗口大小={Width}x{Height}",
            targetDpi,
            savedState.WasMaximized,
            placement.Width,
            placement.Height);
    }

    private void ApplyEmergencyPlacement()
    {
        int dpi = GetSafeWindowDpi(_windowHandle);
        int width = WindowPlacementPolicy.DipToPixels(WindowPlacementPolicy.DefaultWidthDip, dpi);
        int height = WindowPlacementPolicy.DipToPixels(WindowPlacementPolicy.DefaultHeightDip, dpi);
        var placement = new WindowRectangle(0, 0, width, height);
        ApplyNormalRectangle(placement, dpi, wasMaximized: false);
    }

    private void ApplyNormalRectangle(WindowRectangle placement, int dpi, bool wasMaximized)
    {
        AppWindow.MoveAndResize(new RectInt32
        {
            X = placement.X,
            Y = placement.Y,
            Width = placement.Width,
            Height = placement.Height,
        });
        _normalWindowPlacement = new WindowPlacementState(
            WindowPlacementPolicy.CurrentSchemaVersion,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            dpi,
            wasMaximized);
        _lastNonMinimizedWasMaximized = wasMaximized;
    }

    /// <summary>
    /// 只在内存中跟踪最近正常矩形。最大化和最小化产生的系统矩形不得覆盖用户可恢复的大小。
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        try
        {
            if (sender.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                _lastNonMinimizedWasMaximized = true;
                return;
            }

            if (presenter.State == OverlappedPresenterState.Minimized)
            {
                // 最小化只是临时可见性状态，既不覆盖正常矩形，也不作为下一次启动状态。
                return;
            }

            _lastNonMinimizedWasMaximized = false;
            if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
            {
                CaptureRestoredPlacement(sender);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // WinRT 事件处理器不能把异常抛回系统窗口循环；失败只影响本次内存快照。
            Log.Warning(ex, "跟踪窗口布局变化失败");
        }
    }

    private void CaptureRestoredPlacement(AppWindow appWindow)
    {
        _normalWindowPlacement = new WindowPlacementState(
            WindowPlacementPolicy.CurrentSchemaVersion,
            appWindow.Position.X,
            appWindow.Position.Y,
            appWindow.Size.Width,
            appWindow.Size.Height,
            GetSafeWindowDpi(_windowHandle),
            WasMaximized: false);
    }

    /// <summary>
    /// 在关闭已获页面许可后保存一次。保存失败是非致命降级，下一次启动回退默认或旧记录。
    /// </summary>
    private void SaveWindowPlacement()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Restored)
            {
                CaptureRestoredPlacement(AppWindow);
            }

            if (_normalWindowPlacement is null || _windowPlacementStore is null)
            {
                Log.Warning("窗口布局存储未初始化，本次关闭不保存布局");
                return;
            }

            WindowPlacementState state = _normalWindowPlacement with
            {
                // 最大化会保存并恢复；最小化则沿用最小化之前的非最小化状态。
                WasMaximized = AppWindow.Presenter is OverlappedPresenter currentPresenter
                    ? currentPresenter.State == OverlappedPresenterState.Maximized ||
                      (currentPresenter.State == OverlappedPresenterState.Minimized &&
                       _lastNonMinimizedWasMaximized)
                    : false,
            };
            _windowPlacementStore.Write(state);
            Log.Information("窗口布局已保存，最大化={WasMaximized}", state.WasMaximized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            Log.Warning(ex, "保存窗口布局失败，继续关闭应用");
        }
    }

    private static WindowRectangle ToRectangle(RectInt32 rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}
