using System.Runtime.InteropServices;
using System.Text;
using LingoLens.Core;
using LingoLens.Core.Capture;

namespace LingoLens.App.Services;

/// <summary>A capturable window candidate for the target picker.</summary>
public sealed record WindowCandidate(nint Handle, string Title, string ProcessName)
{
    public CaptureTarget ToTarget() => CaptureTarget.ForWindow(Handle, Title);
}

/// <summary>A monitor candidate for the target picker.</summary>
public sealed record MonitorCandidate(nint Handle, string Name, RectI Bounds, bool IsPrimary)
{
    public CaptureTarget ToTarget() => CaptureTarget.ForMonitor(Handle, Name);
}

/// <summary>Enumerates top-level windows and monitors for selecting a capture target.</summary>
public sealed class TargetEnumerator
{
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

            list.Add(new WindowCandidate(hwnd, title, process));
            return true;
        }, 0);

        return list
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
