using System;
using System.IO;

namespace FeedCustomizer.Core.Windowing;

/// <summary>
/// 持久化的正常窗口矩形。坐标和尺寸来自 AppWindow 的物理像素，DPI 用于跨缩放比例恢复视觉尺寸。
/// </summary>
internal sealed record WindowPlacementState(
    int SchemaVersion,
    int X,
    int Y,
    int Width,
    int Height,
    int Dpi,
    bool WasMaximized);

/// <summary>不依赖 WinUI 的物理像素矩形，用于测试居中和屏幕可见性规则。</summary>
internal readonly record struct WindowRectangle(int X, int Y, int Width, int Height);

/// <summary>
/// 窗口布局纯规则。所有计算均使用物理像素，但默认值和最小值以 DIP 表达，
/// 从而在不同显示缩放比例下保持相同的视觉大小。
/// </summary>
internal static class WindowPlacementPolicy
{
    internal const int CurrentSchemaVersion = 1;
    internal const int DefaultWidthDip = 560;
    internal const int DefaultHeightDip = 800;
    internal const int MinimumWidthDip = 560;
    internal const int MinimumHeightDip = 500;
    internal const int DefaultDpi = 96;

    /// <summary>
    /// 在工作区内计算首次启动位置。某个方向放不下时固定到该方向起点，
    /// 保留原始窗口尺寸，允许右侧或底部超出显示器。
    /// </summary>
    internal static WindowRectangle CreateCenteredDefault(WindowRectangle workArea, int dpi)
    {
        ValidateWorkArea(workArea);
        int width = DipToPixels(DefaultWidthDip, dpi);
        int height = DipToPixels(DefaultHeightDip, dpi);
        int x = AddChecked(workArea.X, Math.Max(0, (workArea.Width - width) / 2));
        int y = AddChecked(workArea.Y, Math.Max(0, (workArea.Height - height) / 2));
        return new WindowRectangle(x, y, width, height);
    }

    /// <summary>
    /// 将保存矩形换算到目标 DPI，并把它校正到目标工作区。
    /// 只保证左上角仍在工作区内，不限制右边缘和下边缘，以完整保留用户保存的尺寸。
    /// </summary>
    internal static WindowRectangle Restore(
        WindowPlacementState state,
        WindowRectangle workArea,
        int targetDpi)
    {
        ValidateState(state);
        ValidateWorkArea(workArea);

        int minimumWidth = DipToPixels(MinimumWidthDip, targetDpi);
        int minimumHeight = DipToPixels(MinimumHeightDip, targetDpi);
        int width = Math.Max(minimumWidth, ScalePixels(state.Width, state.Dpi, targetDpi));
        int height = Math.Max(minimumHeight, ScalePixels(state.Height, state.Dpi, targetDpi));

        // 最大坐标保留在工作区最后一个像素；这是恢复时唯一的位置修正，
        // 不因窗口右下方超出显示器而进一步移动或缩小窗口。
        int maximumX = AddChecked(workArea.X, workArea.Width - 1);
        int maximumY = AddChecked(workArea.Y, workArea.Height - 1);
        int x = Math.Clamp(state.X, workArea.X, maximumX);
        int y = Math.Clamp(state.Y, workArea.Y, maximumY);
        return new WindowRectangle(x, y, width, height);
    }

    /// <summary>验证状态是否可用于选择目标显示器；损坏状态必须回退默认布局。</summary>
    internal static void ValidateState(WindowPlacementState state)
    {
        if (state.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"不支持的窗口布局版本：{state.SchemaVersion}");
        }

        if (state.Width <= 0 || state.Height <= 0 || state.Dpi <= 0)
        {
            throw new InvalidDataException("窗口布局包含无效的尺寸或 DPI。");
        }
    }

    /// <summary>把 DIP 转换为物理像素；使用四舍五入避免非整数缩放长期积累误差。</summary>
    internal static int DipToPixels(int dip, int dpi)
    {
        if (dip <= 0 || dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dip), "DIP 和 DPI 必须为正数。");
        }

        return CheckedRound(dip * (double)dpi / DefaultDpi);
    }

    private static int ScalePixels(int pixels, int sourceDpi, int targetDpi)
    {
        if (pixels <= 0 || sourceDpi <= 0 || targetDpi <= 0)
        {
            throw new InvalidDataException("窗口布局无法进行 DPI 换算。");
        }

        return CheckedRound(pixels * (double)targetDpi / sourceDpi);
    }

    private static int CheckedRound(double value)
    {
        if (!double.IsFinite(value) || value > int.MaxValue)
        {
            throw new InvalidDataException("窗口布局换算结果超出系统坐标范围。");
        }

        return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static int AddChecked(int left, int right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("窗口工作区坐标超出系统范围。", ex);
        }
    }

    private static void ValidateWorkArea(WindowRectangle workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new InvalidDataException("显示器工作区无效。");
        }
    }
}
