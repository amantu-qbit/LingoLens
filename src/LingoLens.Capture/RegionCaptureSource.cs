using System.Diagnostics;
using Microsoft.Extensions.Logging;
using LingoLens.Capture.Interop;
using LingoLens.Core;
using LingoLens.Core.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using RectI = LingoLens.Core.RectI;

namespace LingoLens.Capture;

/// <summary>
/// Captures an arbitrary user-drawn rectangle. It captures the monitor that contains the region (via
/// WGC, falling back to DXGI duplication) and GPU-crops each monitor frame down to the region using
/// <c>CopySubresourceRegion</c>, so only the requested pixels reach the pipeline.
/// </summary>
public sealed class RegionCaptureSource : IScreenCaptureSource
{
    private readonly DeviceResources _resources;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RegionCaptureSource> _logger;
    private readonly object _sync = new();

    private IScreenCaptureSource? _inner;
    private RectI _region = RectI.Empty;
    private RectI _monitorBounds = RectI.Empty;
    private double _dpi = 96d;
    private bool _disposed;

    /// <summary>Creates a region capture source. The logger factory builds the inner monitor source's logger.</summary>
    public RegionCaptureSource(
        DeviceResources resources,
        ILoggerFactory loggerFactory,
        ILogger<RegionCaptureSource> logger)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsRunning => _inner?.IsRunning ?? false;

    /// <inheritdoc />
    public CaptureTarget? Target { get; private set; }

    /// <inheritdoc />
    public event EventHandler<FrameArrivedEventArgs>? FrameArrived;

    /// <inheritdoc />
    public event EventHandler<CaptureErrorEventArgs>? Error;

    /// <inheritdoc />
    public bool CanCapture(CaptureTarget target) =>
        target.Mode == CaptureMode.Region && !target.Region.IsEmpty;

    /// <inheritdoc />
    public async Task StartAsync(CaptureTarget target, CaptureSettings settings, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (target.Mode != CaptureMode.Region || target.Region.IsEmpty)
            throw new ArgumentException("Region capture requires a non-empty Region target.", nameof(target));

        IScreenCaptureSource inner;
        CaptureTarget monitorTarget;
        lock (_sync)
        {
            if (_inner is not null)
                throw new InvalidOperationException("Capture is already running.");

            Target = target;
            _region = target.Region;

            // Resolve the monitor that hosts the region's centre.
            int cx = _region.X + _region.Width / 2;
            int cy = _region.Y + _region.Height / 2;
            nint hMonitor = NativeMethods.MonitorFromVirtualPoint(cx, cy);
            if (hMonitor == 0)
                throw new InvalidOperationException("Could not resolve a monitor for the region.");

            NativeMethods.TryGetMonitorBounds(hMonitor, out _monitorBounds, out _, out _);
            _dpi = NativeMethods.GetMonitorDpi(hMonitor);

            monitorTarget = CaptureTarget.ForMonitor(hMonitor, target.DisplayName);
            inner = CreateInner(monitorTarget);
            inner.FrameArrived += OnInnerFrame;
            inner.Error += OnInnerError;
            _inner = inner;
        }

        try
        {
            // Forward the same settings (the monitor source handles cadence/dirty-region probing).
            await inner.StartAsync(monitorTarget, settings, ct).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IScreenCaptureSource CreateInner(CaptureTarget monitorTarget)
    {
        if (GraphicsCaptureItemSupported())
        {
            var wgc = new WgcCaptureSource(_resources, _loggerFactory.CreateLogger<WgcCaptureSource>());
            if (wgc.CanCapture(monitorTarget))
                return wgc;
            _ = wgc.DisposeAsync();
        }

        return new DxgiDuplicationCaptureSource(_resources, _loggerFactory.CreateLogger<DxgiDuplicationCaptureSource>());
    }

    private static bool GraphicsCaptureItemSupported()
    {
        try { return Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }

    private void OnInnerError(object? sender, CaptureErrorEventArgs e) => Error?.Invoke(this, e);

    private void OnInnerFrame(object? sender, FrameArrivedEventArgs e)
    {
        using var monitorFrame = e.Frame;

        // Translate the virtual-desktop region into monitor-local coordinates. These are still in
        // virtual-desktop (DIP-ish) units, whereas the captured texture is physical pixels: on a
        // monitor scaled above 100% the texture is larger than monitorBounds. Compute the scale from
        // the actual frame size vs the monitor's virtual size and convert the crop into physical px.
        if (_monitorBounds.Width <= 0 || _monitorBounds.Height <= 0 ||
            monitorFrame.Width <= 0 || monitorFrame.Height <= 0)
            return;

        double scaleX = (double)monitorFrame.Width / _monitorBounds.Width;
        double scaleY = (double)monitorFrame.Height / _monitorBounds.Height;

        int localXv = _region.X - _monitorBounds.X;
        int localYv = _region.Y - _monitorBounds.Y;

        int px = (int)Math.Floor(localXv * scaleX);
        int py = (int)Math.Floor(localYv * scaleY);
        int pRight = (int)Math.Ceiling((localXv + _region.Width) * scaleX);
        int pBottom = (int)Math.Ceiling((localYv + _region.Height) * scaleY);

        RectI local = new RectI(px, py, pRight - px, pBottom - py)
            .Intersect(new RectI(0, 0, monitorFrame.Width, monitorFrame.Height));
        if (local.IsEmpty)
            return;

        nint srcPtr = monitorFrame.D3D11Texture;
        if (srcPtr == 0)
            return;

        // Wrap the source pointer WITHOUT taking ownership: the monitor frame owns this texture and
        // disposes it. SharpGen's pointer ctor does not AddRef, but its finalizer/Dispose WOULD
        // Release — so we detach (zero the pointer + suppress finalize) when done to keep refcounts
        // balanced and avoid over-releasing the WGC/duplication texture.
        var source = new ID3D11Texture2D(srcPtr);
        try
        {
            var srcDesc = source.Description;

            var desc = new Texture2DDescription
            {
                Width = (uint)local.Width,
                Height = (uint)local.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = srcDesc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            ID3D11Texture2D cropped;
            lock (_resources.ContextLock)
            {
                cropped = _resources.Device.CreateTexture2D(ref desc);
                var box = new Box(local.X, local.Y, 0, local.Right, local.Bottom, 1);
                _resources.Context.CopySubresourceRegion(cropped, 0, 0, 0, 0, source, 0, box);
            }

            var regionFrame = new CaptureFrame(
                _resources,
                cropped,
                Stopwatch.GetTimestamp(),
                _dpi,
                Array.Empty<RectI>(),
                ownsTexture: true);

            FrameArrived?.Invoke(this, new FrameArrivedEventArgs(regionFrame));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Region crop failed.");
            Error?.Invoke(this, new CaptureErrorEventArgs(CaptureErrorKind.Unknown, "Region crop failed.", ex));
        }
        finally
        {
            // Detach the borrowed wrapper so neither Dispose nor the finalizer releases the
            // monitor-frame-owned texture.
            source.NativePointer = nint.Zero;
            GC.SuppressFinalize(source);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        IScreenCaptureSource? inner;
        lock (_sync)
        {
            inner = _inner;
            _inner = null;
        }

        if (inner is not null)
        {
            inner.FrameArrived -= OnInnerFrame;
            inner.Error -= OnInnerError;
            await inner.StopAsync().ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }
}
