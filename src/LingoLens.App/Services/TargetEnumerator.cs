using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LingoLens.Core;
using LingoLens.Core.Capture;

namespace LingoLens.App.Services;

/// <summary>A capturable window candidate for the target picker.</summary>
public sealed record WindowCandidate(nint Handle, string Title, string ProcessName, ImageSource? Icon = null)
{
    public CaptureTarget ToTarget()
    {
        // The window's current visible on-screen rect, so the overlay can be positioned over it. Prefer the
        // DWM extended frame bounds (the actual visible rectangle, matching what Windows Graphics Capture
        // records) over GetWindowRect, which includes the invisible resize border on Windows 10/11.
        RectI bounds = RectI.Empty;
        int rectSize = Marshal.SizeOf<NativeMethods.RECT>();
        if (NativeMethods.DwmGetWindowAttribute(Handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT fr, rectSize) == 0
            && fr.right > fr.left && fr.bottom > fr.top)
            bounds = new RectI(fr.left, fr.top, fr.right - fr.left, fr.bottom - fr.top);
        else if (NativeMethods.GetWindowRect(Handle, out NativeMethods.RECT r) && r.right > r.left && r.bottom > r.top)
            bounds = new RectI(r.left, r.top, r.right - r.left, r.bottom - r.top);
        return CaptureTarget.ForWindow(Handle, Title, bounds);
    }
}

/// <summary>A monitor candidate for the target picker.</summary>
public sealed record MonitorCandidate(nint Handle, string Name, RectI Bounds, bool IsPrimary)
{
    public CaptureTarget ToTarget() => CaptureTarget.ForMonitor(Handle, Name, Bounds);

    // Shown as the selected item / list entry in the monitor picker.
    public override string ToString() => Name;
}

/// <summary>Enumerates top-level windows and monitors for selecting a capture target.</summary>
public sealed class TargetEnumerator
{
    /// <summary>
    /// Enumerates windows on a background thread. Enumeration touches the process list and (best-effort)
    /// each window's icon, which can take tens of milliseconds — running it off the UI thread keeps the
    /// picker opening instantly.
    /// </summary>
    public Task<IReadOnlyList<WindowCandidate>> EnumerateWindowsAsync() => Task.Run(EnumerateWindows);

    public IReadOnlyList<WindowCandidate> EnumerateWindows()
    {
        var list = new List<WindowCandidate>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            // Skip cloaked (e.g. virtual-desktop-hidden) windows.
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED,
                    out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            string process = "";
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                process = p.ProcessName;
            }
            catch { /* process may have exited */ }

            list.Add(new WindowCandidate(hwnd, title, process, TryGetWindowIcon(hwnd)));
            return true;
        }, 0);

        return list
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Best-effort window icon as a frozen <see cref="ImageSource"/> usable on any thread. Prefers the
    /// class icon (an instant lookup) and only falls back to the WM_GETICON message — with a short,
    /// abort-if-hung timeout — so a frozen app can never stall enumeration. Returns null on any failure;
    /// the picker then shows a neutral tile.
    /// </summary>
    private static ImageSource? TryGetWindowIcon(nint hwnd)
    {
        try
        {
            nint hIcon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICONSM);
            if (hIcon == 0) hIcon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICON);
            if (hIcon == 0)
                NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETICON, NativeMethods.ICON_SMALL2,
                    0, NativeMethods.SMTO_ABORTIFHUNG, 80, out hIcon);
            if (hIcon == 0) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // cross-thread safe once frozen
            return source;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<MonitorCandidate> EnumerateMonitors()
    {
        var list = new List<MonitorCandidate>();
        NativeMethods.EnumDisplayMonitors(0, 0, (hMon, _, _, _) =>
        {
            var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            {
                var b = mi.rcMonitor;
                var bounds = new RectI(b.left, b.top, b.right - b.left, b.bottom - b.top);
                bool primary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                string name = string.IsNullOrWhiteSpace(mi.szDevice) ? $"Display {list.Count + 1}" : mi.szDevice.Trim('\0', '\\', '.');
                list.Add(new MonitorCandidate(hMon, name, bounds, primary));
            }
            return true;
        }, 0);
        return list;
    }
}
