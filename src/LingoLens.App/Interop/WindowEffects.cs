using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LingoLens.App.Interop;

/// <summary>Applies modern DWM window effects (Mica backdrop, rounded corners, dark mode) to WPF windows.</summary>
public static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // DWM_WINDOW_CORNER_PREFERENCE
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    // DWM_SYSTEMBACKDROP_TYPE
    public const int BackdropNone = 1;
    public const int BackdropMica = 2;
    public const int BackdropAcrylic = 3;
    public const int BackdropTabbed = 4;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    public static void Apply(Window window, int backdrop = BackdropMica, bool roundedSmall = false)
    {
        if (window is null) return;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        ApplyToHandle(hwnd, backdrop, roundedSmall);
    }

    public static void ApplyToHandle(nint hwnd, int backdrop = BackdropMica, bool roundedSmall = false)
    {
        if (hwnd == 0) return;
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int corner = roundedSmall ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            int type = backdrop;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        }
        catch
        {
            // Pre-Win11 builds lack these attributes; the app still works without the backdrop.
        }
    }
}
