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
        private int _minimumWindowWidth;
        private int _minimumWindowHeight;

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
