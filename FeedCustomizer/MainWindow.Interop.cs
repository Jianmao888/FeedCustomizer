using FeedCustomizer.Core.Windowing;
using System;
using System.Runtime.InteropServices;

namespace FeedCustomizer
{
    public partial class MainWindow
    {
        private const int GwlWndProc = -4;
        private const uint WmGetMinMaxInfo = 0x0024;

        private WindowProcDelegate? _windowProcDelegate;
        private IntPtr _originalWindowProc;

        private void SetMinimumWindowSize(IntPtr hWnd)
        {
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
                int dpi = GetSafeWindowDpi(hWnd);
                var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                // 每次按窗口当前显示器 DPI 计算，跨不同缩放比例显示器后最小视觉尺寸仍保持一致。
                minMaxInfo.ptMinTrackSize.X = WindowPlacementPolicy.DipToPixels(
                    WindowPlacementPolicy.MinimumWidthDip,
                    dpi);
                minMaxInfo.ptMinTrackSize.Y = WindowPlacementPolicy.DipToPixels(
                    WindowPlacementPolicy.MinimumHeightDip,
                    dpi);
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

        /// <summary>Win32 在句柄无效时可能返回 0；回退 96 DPI 保证启动和窗口消息都不因换算失败中断。</summary>
        private static int GetSafeWindowDpi(IntPtr hwnd)
        {
            uint dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? WindowPlacementPolicy.DefaultDpi : checked((int)dpi);
        }
    }
}
