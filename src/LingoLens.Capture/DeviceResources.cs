using Microsoft.Extensions.Logging;
using LingoLens.Capture.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace LingoLens.Capture;

/// <summary>
/// Owns a single shared Direct3D 11 device + immediate context used by every capture source, plus the
/// WinRT <see cref="IDirect3DDevice"/> projection required by Windows.Graphics.Capture. Created with
/// the <see cref="DeviceCreationFlags.BgraSupport"/> flag so the device is usable by Direct2D / WGC.
/// </summary>
/// <remarks>
/// The immediate context is NOT free-threaded; callers that touch it from multiple threads (CPU
/// readback, region cropping) must serialize via <see cref="ContextLock"/>.
/// </remarks>
public sealed class DeviceResources : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private readonly ILogger<DeviceResources> _logger;
    private readonly object _contextLock = new();
    private IDirect3DDevice? _winrtDevice;
    private bool _disposed;

    /// <summary>Creates the shared device. Pass an explicit adapter to pin a GPU, else the default adapter is used.</summary>
    /// <param name="logger">Diagnostics sink.</param>
    /// <param name="adapter">Optional adapter to create the device on (caller retains ownership).</param>
    public DeviceResources(ILogger<DeviceResources> logger, IDXGIAdapter1? adapter = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var flags = DeviceCreationFlags.BgraSupport;
        var driverType = adapter is null ? DriverType.Hardware : DriverType.Unknown;

        var result = D3D11.D3D11CreateDevice(
            adapter,
            driverType,
            flags,
            FeatureLevels,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context);

        if (result.Failure || device is null || context is null)
        {
            device?.Dispose();
            context?.Dispose();

            // WARP fallback keeps the app alive on machines with no/blocked hardware D3D11.
            _logger.LogWarning("Hardware D3D11 device creation failed ({Result}); falling back to WARP.", result);
            result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Warp,
                flags,
                FeatureLevels,
                out device,
                out context);

            if (result.Failure || device is null || context is null)
            {
                device?.Dispose();
                context?.Dispose();
                result.CheckError();
                throw new InvalidOperationException("Unable to create a Direct3D 11 device.");
            }
        }

        Device = device;
        Context = context;
        _logger.LogInformation("Created shared D3D11 device (feature level {Level}).", Device.FeatureLevel);
    }

    /// <summary>The shared Direct3D 11 device.</summary>
    public ID3D11Device Device { get; }

    /// <summary>The immediate context. Guard multi-threaded use with <see cref="ContextLock"/>.</summary>
    public ID3D11DeviceContext Context { get; }

    /// <summary>Lock object serializing immediate-context access across capture threads.</summary>
    public object ContextLock => _contextLock;

    /// <summary>
    /// The WinRT projection of this device, lazily created via
    /// <c>CreateDirect3D11DeviceFromDXGIDevice</c>. Required to drive a WGC frame pool.
    /// </summary>
    public IDirect3DDevice WinRTDevice
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_winrtDevice is not null)
                return _winrtDevice;

            lock (_contextLock)
            {
                if (_winrtDevice is not null)
                    return _winrtDevice;

                using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
                _winrtDevice = Interop.Interop.CreateDirect3DDevice(dxgiDevice.NativePointer);
                return _winrtDevice;
            }
        }
    }

    /// <summary>Returns the DXGI adapter backing this device (caller owns the result).</summary>
    public IDXGIAdapter GetAdapter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        return dxgiDevice.GetAdapter();
    }

    /// <summary>True if the device has been removed/reset and must be recreated.</summary>
    public bool IsDeviceLost => Device.DeviceRemovedReason.Failure;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_winrtDevice is IDisposable d)
            d.Dispose();
        _winrtDevice = null;

        Context.Dispose();
        Device.Dispose();
    }
}
