using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LingoLens.Capture.Interop;

/// <summary>
/// Native COM / Win32 interop required to bridge Windows.Graphics.Capture (WGC) and the WinRT
/// Direct3D projection to the underlying DXGI/Direct3D11 objects exposed by Vortice.
/// Patterns follow robmikh's <c>Win32CaptureSample</c>.
/// </summary>
internal static class Interop
{
    /// <summary>HRESULT for "interface not supported".</summary>
    internal const int E_NOINTERFACE = unchecked((int)0x80004002);

    /// <summary>
    /// Activation-factory interop used to create a <see cref="GraphicsCaptureItem"/> from a raw
    /// HWND or HMONITOR. Obtained via <c>GraphicsCaptureItem</c>'s activation factory.
    /// </summary>
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphicsCaptureItemInterop
    {
        /// <summary>Creates a capture item for a top-level window.</summary>
        nint CreateForWindow([In] nint window, [In] in Guid iid);

        /// <summary>Creates a capture item for a monitor.</summary>
        nint CreateForMonitor([In] nint monitor, [In] in Guid iid);
    }

    /// <summary>
    /// COM interface that lets us pull the underlying DXGI interface (e.g. an ID3D11Texture2D) out of
    /// a WinRT <c>IDirect3DSurface</c>.
    /// </summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        /// <summary>Returns the requested DXGI/Direct3D interface as a raw COM pointer.</summary>
        nint GetInterface([In] in Guid iid);
    }

    /// <summary>
    /// Wraps a DXGI device as a WinRT <see cref="IDirect3DDevice"/> so it can drive a WGC frame pool.
    /// </summary>
    /// <param name="dxgiDevice">A raw IDXGIDevice COM pointer (caller retains ownership).</param>
    /// <returns>The projected WinRT device. Caller owns the lifetime.</returns>
    internal static IDirect3DDevice CreateDirect3DDevice(nint dxgiDevice)
    {
        if (dxgiDevice == 0)
            throw new ArgumentException("DXGI device pointer must be non-null.", nameof(dxgiDevice));

        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint inspectable);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        try
        {
            // Marshal the IInspectable into the WinRT projection, then release our raw ref.
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>
    /// Extracts the native ID3D11Texture2D pointer backing a WinRT Direct3D surface.
    /// Caller owns the returned reference and must <see cref="Marshal.Release"/> it.
    /// </summary>
    internal static nint GetDxgiInterfaceFromSurface(IDirect3DSurface surface, in Guid iid)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        return access.GetInterface(iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = false)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    /// <summary>Well-known interface GUIDs used during interop.</summary>
    internal static class Guids
    {
        /// <summary>IID of the WinRT <see cref="GraphicsCaptureItem"/> runtime class.</summary>
        internal static readonly Guid GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        /// <summary>IID of ID3D11Texture2D (used with <see cref="IDirect3DDxgiInterfaceAccess"/>).</summary>
        internal static readonly Guid ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        /// <summary>IID of IDXGIDevice.</summary>
        internal static readonly Guid IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    }
}
