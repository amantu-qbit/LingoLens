using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LingoLens.App.Services;
using LingoLens.Core;

namespace LingoLens.App.Views;

/// <summary>Full-virtual-desktop overlay that lets the user drag out a capture region.</summary>
public partial class RegionSelectorWindow : Window
{
    private Point _start;
    private bool _dragging;

    public RectI? SelectedRegion { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();
        // Cover the whole virtual desktop (DIPs).
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OuterRect.Rect = new Rect(0, 0, RootCanvas.ActualWidth, RootCanvas.ActualHeight);
        // Centre the opening hint in the upper third.
        Hint.UpdateLayout();
        Canvas.SetLeft(Hint, Math.Max(0, (RootCanvas.ActualWidth - Hint.ActualWidth) / 2));
        Canvas.SetTop(Hint, RootCanvas.ActualHeight * 0.14);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _start = e.GetPosition(RootCanvas);
        _dragging = true;
        Hint.Visibility = Visibility.Collapsed;
        SelectionBox.Visibility = Visibility.Visible;
        DimsChip.Visibility = Visibility.Visible;
        CaptureMouse();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(RootCanvas);

        // Crosshair guides follow the cursor.
        CrossV.X1 = p.X; CrossV.X2 = p.X; CrossV.Y1 = 0; CrossV.Y2 = RootCanvas.ActualHeight;
        CrossH.Y1 = p.Y; CrossH.Y2 = p.Y; CrossH.X1 = 0; CrossH.X2 = RootCanvas.ActualWidth;

        if (_dragging)
        {
            double x = Math.Min(p.X, _start.X), y = Math.Min(p.Y, _start.Y);
            double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);

            Canvas.SetLeft(SelectionBox, x);
            Canvas.SetTop(SelectionBox, y);
            SelectionBox.Width = w;
            SelectionBox.Height = h;

            HoleRect.Rect = new Rect(x, y, w, h);

            DimsText.Text = $"{Math.Round(w)} × {Math.Round(h)}";
            double chipTop = y > 34 ? y - 30 : y + h + 8;
            Canvas.SetLeft(DimsChip, Math.Max(0, x));
            Canvas.SetTop(DimsChip, chipTop);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var p = e.GetPosition(RootCanvas);
        double xDip = Math.Min(p.X, _start.X), yDip = Math.Min(p.Y, _start.Y);
        double wDip = Math.Abs(p.X - _start.X), hDip = Math.Abs(p.Y - _start.Y);

        // The two opposite corners may sit on monitors with different DPI, so map each
        // corner to physical virtual-desktop pixels using the DPI of the monitor it lands on.
        var topLeft = DipToPixel(Left + xDip, Top + yDip);
        var bottomRight = DipToPixel(Left + xDip + wDip, Top + yDip + hDip);

        int px = Math.Min(topLeft.X, bottomRight.X);
        int py = Math.Min(topLeft.Y, bottomRight.Y);
        int pw = Math.Abs(bottomRight.X - topLeft.X);
        int ph = Math.Abs(bottomRight.Y - topLeft.Y);
        var region = new RectI(px, py, pw, ph);

        SelectedRegion = region.Width >= 8 && region.Height >= 8 ? region : null;
        DialogResult = SelectedRegion is not null;
        Close();
    }

    /// <summary>
    /// Maps an absolute virtual-desktop point in DIPs to physical pixels using the effective
    /// DPI of the monitor that contains it. Falls back to this window's scale if a monitor's
    /// DIP bounds cannot be resolved (single-DPI desktops are unaffected).
    /// </summary>
    private (int X, int Y) DipToPixel(double xDip, double yDip)
    {
        foreach (var (dipBounds, pixelOrigin, scaleX, scaleY) in EnumerateMonitorScales())
        {
            if (xDip >= dipBounds.Left && xDip < dipBounds.Right &&
                yDip >= dipBounds.Top && yDip < dipBounds.Bottom)
            {
                int x = pixelOrigin.X + (int)Math.Round((xDip - dipBounds.Left) * scaleX);
                int y = pixelOrigin.Y + (int)Math.Round((yDip - dipBounds.Top) * scaleY);
                return (x, y);
            }
        }

        // Fallback: use this window's scale (correct on single-DPI / uniform-DPI desktops).
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return ((int)Math.Round(xDip * dpi.DpiScaleX), (int)Math.Round(yDip * dpi.DpiScaleY));
    }

    /// <summary>
    /// Yields, for each monitor, its DIP bounds, physical-pixel origin, and effective DPI scale.
    /// Each monitor's physical rect comes from Win32; its DIP rect is the physical rect divided
    /// by that monitor's own effective scale, anchored at the primary monitor (physical 0,0 == DIP 0,0).
    /// </summary>
    private static IEnumerable<(Rect DipBounds, (int X, int Y) PixelOrigin, double ScaleX, double ScaleY)> EnumerateMonitorScales()
    {
        var monitors = new List<(NativeMethods.RECT Px, double Sx, double Sy)>();
        NativeMethods.EnumDisplayMonitors(0, 0, (hMon, _, _, _) =>
        {
            var mi = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return true;

            double sx = 1.0, sy = 1.0;
            if (NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
            {
                sx = dpiX / 96.0;
                sy = dpiY / 96.0;
            }
            monitors.Add((mi.rcMonitor, sx, sy));
            return true;
        }, 0);

        foreach (var (px, sx, sy) in monitors)
        {
            // DIP origin: pixel origin scaled down by this monitor's own scale. This matches WPF's
            // per-monitor layout for the common case where each monitor is anchored from the origin.
            double dipLeft = px.left / sx;
            double dipTop = px.top / sy;
            double dipW = (px.right - px.left) / sx;
            double dipH = (px.bottom - px.top) / sy;
            yield return (
                new Rect(dipLeft, dipTop, dipW, dipH),
                (px.left, px.top),
                sx, sy);
        }
    }
}
