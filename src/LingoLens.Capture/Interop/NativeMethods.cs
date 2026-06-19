using System.Runtime.InteropServices;
using LingoLens.Core;

namespace LingoLens.Capture.Interop;

/// <summary>Thin Win32 P/Invoke surface for window/monitor geometry and DPI.</summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly RectI ToRectI() => new(Left, Top, Width, Height);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>MDT_EFFECTIVE_DPI for GetDpiForMonitor.</summary>
    internal const int MDT_EFFECTIVE_DPI = 0;

    internal const int MONITORINFOF_PRIMARY = 0x1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    /// <summary>Returns the per-monitor effective DPI (X axis), or 96 on failure.</summary>
    [DllImport("shcore.dll", SetLastError = true)]
    internal static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>Resolves the bounding rectangle (virtual-desktop pixels) of a monitor.</summary>
    internal static bool TryGetMonitorBounds(nint hMonitor, out RectI bounds, out string deviceName, out bool isPrimary)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (GetMonitorInfo(hMonitor, ref info))
        {
            bounds = info.rcMonitor.ToRectI();
            deviceName = info.szDevice ?? string.Empty;
            isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
            return true;
        }

        bounds = RectI.Empty;
        deviceName = string.Empty;
        isPrimary = false;
        return false;
    }

    /// <summary>Best-effort effective DPI for a monitor (defaults to 96).</summary>
    internal static double GetMonitorDpi(nint hMonitor)
    {
        try
        {
            if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                return dpiX;
        }
        catch (DllNotFoundException)
        {
            // shcore.dll missing on very old SKUs — fall through to default.
        }

        return 96d;
    }

    /// <summary>Best-effort effective DPI for a window (defaults to 96).</summary>
    internal static double GetWindowDpi(nint hWnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hWnd);
            if (dpi > 0)
                return dpi;
        }
        catch (EntryPointNotFoundException)
        {
            // GetDpiForWindow requires 1607+. Fall back via the owning monitor.
        }

        nint mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        return mon != 0 ? GetMonitorDpi(mon) : 96d;
    }

    /// <summary>Resolves the monitor that contains a virtual-desktop point.</summary>
    internal static nint MonitorFromVirtualPoint(int x, int y) =>
        MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
}
