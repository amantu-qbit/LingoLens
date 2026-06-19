using System.Diagnostics;
using Microsoft.Extensions.Logging;
using LingoLens.Capture.Interop;
using LingoLens.Core;
using LingoLens.Core.Capture;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LingoLens.Capture;

/// <summary>
/// Captures a window or monitor via Windows.Graphics.Capture (WGC). Sees occluded windows, is
/// cross-GPU, and honours per-monitor DPI. Feature-probes for cursor/border suppression, capped
/// update interval, and dirty-region reporting (24H2+). Detects all-black/identical output produced
/// by DRM-protected windows and reports it as <see cref="CaptureErrorKind.ProtectedContent"/>.
/// </summary>
public sealed class WgcCaptureSource : IScreenCaptureSource
{
    private const string CaptureSessionTypeName = "Windows.Graphics.Capture.GraphicsCaptureSession";

    private readonly DeviceResources _resources;
    private readonly ILogger<WgcCaptureSource> _logger;
    private readonly object _sync = new();

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private CaptureSettings _settings = new();
    private double _dpi = 96d;

    private long _lastFrameTicks;
    private long _minFrameIntervalTicks;
    private int _lastPoolWidth;
    private int _lastPoolHeight;
    private int _consecutiveBlackFrames;
    private bool _protectedReported;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>Creates a WGC capture source on the shared device.</summary>
    public WgcCaptureSource(DeviceResources resources, ILogger<WgcCaptureSource> logger)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public CaptureTarget? Target { get; private set; }

    /// <inheritdoc />
    public event EventHandler<FrameArrivedEventArgs>? FrameArrived;

    /// <inheritdoc />
    public event EventHandler<CaptureErrorEventArgs>? Error;

    /// <inheritdoc />
    public bool CanCapture(CaptureTarget target)
    {
        if (target.Mode is not (CaptureMode.Window or CaptureMode.Monitor))
            return false;
        if (!GraphicsCaptureSession.IsSupported())
            return false;
        return target.Mode == CaptureMode.Window
            ? target.WindowHandle != 0 && NativeMethods.IsWindow(target.WindowHandle)
            : target.MonitorHandle != 0;
    }

    /// <inheritdoc />
    public Task StartAsync(CaptureTarget target, CaptureSettings settings, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_running)
                throw new InvalidOperationException("Capture is already running.");

            if (!GraphicsCaptureSession.IsSupported())
                throw new NotSupportedException("Windows.Graphics.Capture is not supported on this OS.");

            _settings = settings ?? new CaptureSettings();
            Target = target;
            _consecutiveBlackFrames = 0;
            _protectedReported = false;
            _lastFrameTicks = 0;
            _minFrameIntervalTicks = _settings.MaxFps > 0
                ? Stopwatch.Frequency / _settings.MaxFps
                : 0;

            try
            {
                _item = CreateItem(target, out _dpi);
                _item.Closed += OnItemClosed;

                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _resources.WinRTDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    _item.Size);
                _lastPoolWidth = _item.Size.Width;
                _lastPoolHeight = _item.Size.Height;
                _framePool.FrameArrived += OnFrameArrived;

                _session = _framePool.CreateCaptureSession(_item);
                ConfigureSession(_session);

                _session.StartCapture();
                _running = true;
                _logger.LogInformation("WGC capture started for {Mode} ({Width}x{Height}).",
                    target.Mode, _item.Size.Width, _item.Size.Height);
            }
            catch (Exception ex)
            {
                TeardownLocked();
                _logger.LogError(ex, "Failed to start WGC capture.");
                throw;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        lock (_sync)
        {
            TeardownLocked();
        }

        return Task.CompletedTask;
    }

    private GraphicsCaptureItem CreateItem(CaptureTarget target, out double dpi)
    {
        // Obtain the GraphicsCaptureItem activation factory as the interop interface.
        using var factoryRef = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var factory = factoryRef.AsInterface<Interop.Interop.IGraphicsCaptureItemInterop>();
        Guid iid = Interop.Interop.Guids.GraphicsCaptureItem;

        nint itemPtr;
        if (target.Mode == CaptureMode.Window)
        {
            if (target.WindowHandle == 0)
                throw new ArgumentException("Window target requires a valid HWND.", nameof(target));
            itemPtr = factory.CreateForWindow(target.WindowHandle, iid);
            dpi = NativeMethods.GetWindowDpi(target.WindowHandle);
        }
        else
        {
            if (target.MonitorHandle == 0)
                throw new ArgumentException("Monitor target requires a valid HMONITOR.", nameof(target));
            itemPtr = factory.CreateForMonitor(target.MonitorHandle, iid);
            dpi = NativeMethods.GetMonitorDpi(target.MonitorHandle);
        }

        if (itemPtr == 0)
            throw new InvalidOperationException("Failed to create a GraphicsCaptureItem for the target.");

        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.Release(itemPtr);
        }
    }

    private void ConfigureSession(GraphicsCaptureSession session)
    {
        // Cursor capture (always available on the supported baseline).
        try { session.IsCursorCaptureEnabled = _settings.CaptureCursor; }
        catch (Exception ex) { _logger.LogDebug(ex, "IsCursorCaptureEnabled not settable."); }

        // Yellow capture border suppression (Win11+). The property is absent from the 19041
        // projection, so it is set reflectively after an ApiInformation probe.
        if (ApiInformation.IsPropertyPresent(CaptureSessionTypeName, "IsBorderRequired"))
        {
            TrySetSessionProperty(session, "IsBorderRequired", _settings.ShowCaptureBorder, "IsBorderRequired");
        }

        // Cap delivery cadence at the source (24H2+); otherwise we throttle in OnFrameArrived.
        if (_settings.MaxFps > 0 &&
            ApiInformation.IsPropertyPresent(CaptureSessionTypeName, "MinUpdateInterval"))
        {
            TrySetSessionProperty(session, "MinUpdateInterval", TimeSpan.FromSeconds(1.0 / _settings.MaxFps), "MinUpdateInterval");
        }

        // Dirty-region reporting (24H2+): when present we surface per-frame dirty rects.
        if (_settings.PreferDirtyRegions &&
            ApiInformation.IsPropertyPresent(CaptureSessionTypeName, "DirtyRegionMode"))
        {
            try
            {
                // TODO(verify-on-hardware): DirtyRegionMode.ReportOnly is the desired value; the enum
                // is only present on 24H2+, so we set it reflectively to avoid a hard type dependency
                // on SDKs that predate it.
                TrySetDirtyRegionMode(session);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DirtyRegionMode not settable.");
            }
        }
    }

    private void TrySetSessionProperty(GraphicsCaptureSession session, string propertyName, object value, string label)
    {
        try
        {
            var prop = typeof(GraphicsCaptureSession).GetProperty(propertyName);
            if (prop is { CanWrite: true })
                prop.SetValue(session, value);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Label} not settable.", label);
        }
    }

    private static void TrySetDirtyRegionMode(GraphicsCaptureSession session)
    {
        var prop = typeof(GraphicsCaptureSession).GetProperty("DirtyRegionMode");
        if (prop is null || !prop.CanWrite)
            return;
        var enumType = prop.PropertyType;
        // Prefer "ReportOnly"; fall back to the first non-default value.
        object? value = null;
        foreach (var name in Enum.GetNames(enumType))
        {
            if (string.Equals(name, "ReportOnly", StringComparison.Ordinal))
            {
                value = Enum.Parse(enumType, name);
                break;
            }
        }
        value ??= Enum.GetValues(enumType).GetValue(0);
        prop.SetValue(session, value);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // OnFrameArrived runs free-threaded and can race TeardownLocked (which disposes _item /
        // _framePool). Snapshot the shared state under _sync and bail if capture has stopped so we
        // never touch a disposed item/pool.
        GraphicsCaptureItem? item;
        Direct3D11CaptureFramePool? framePool;
        lock (_sync)
        {
            if (!_running)
                return;
            item = _item;
            framePool = _framePool;
        }

        if (item is null || framePool is null || sender != framePool)
            return;

        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
                return;

            long now = Stopwatch.GetTimestamp();
            if (_minFrameIntervalTicks > 0 && _lastFrameTicks != 0 &&
                now - _lastFrameTicks < _minFrameIntervalTicks)
            {
                return; // software throttle when MinUpdateInterval is unavailable
            }

            // Resize the pool if the content size changed (DPI/resolution/window resize). Recreate
            // with the *new* size from the frame (the item's Size lags behind), and track the size we
            // recreated for so the branch doesn't re-fire on every subsequent frame.
            var contentSize = frame.ContentSize;
            if (contentSize.Width > 0 && contentSize.Height > 0 &&
                (contentSize.Width != _lastPoolWidth || contentSize.Height != _lastPoolHeight))
            {
                sender.Recreate(
                    _resources.WinRTDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    contentSize);
                _lastPoolWidth = contentSize.Width;
                _lastPoolHeight = contentSize.Height;

                // The frame we just pulled was sized for the old pool; drop it and pick up a
                // correctly-sized frame on the next arrival.
                return;
            }

            nint texturePtr = Interop.Interop.GetDxgiInterfaceFromSurface(
                frame.Surface, Interop.Interop.Guids.ID3D11Texture2D);
            if (texturePtr == 0)
                return;

            var texture = new ID3D11Texture2D(texturePtr);
            var dirty = ExtractDirtyRects(frame, texture);

            var captureFrame = new CaptureFrame(
                _resources,
                texture,
                Stopwatch.GetTimestamp(),
                _dpi,
                dirty,
                ownsTexture: true);

            if (DetectProtectedContent(captureFrame))
            {
                captureFrame.Dispose();
                return;
            }

            _lastFrameTicks = now;
            FrameArrived?.Invoke(this, new FrameArrivedEventArgs(captureFrame));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WGC frame processing failed.");
            RaiseError(CaptureErrorKind.Unknown, "WGC frame processing failed.", ex);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private static IReadOnlyList<RectI> ExtractDirtyRects(Direct3D11CaptureFrame frame, ID3D11Texture2D texture)
    {
        // DirtyRegions is only present on 24H2+; access it reflectively so we still compile/run on
        // older SDKs and OSes. Values are Windows.Graphics.RectInt32.
        var prop = frame.GetType().GetProperty("DirtyRegions");
        if (prop?.GetValue(frame) is not System.Collections.IEnumerable regions)
            return Array.Empty<RectI>();

        var list = new List<RectI>();
        foreach (var region in regions)
        {
            var t = region.GetType();
            int x = Convert.ToInt32(t.GetProperty("X")!.GetValue(region));
            int y = Convert.ToInt32(t.GetProperty("Y")!.GetValue(region));
            int w = Convert.ToInt32(t.GetProperty("Width")!.GetValue(region));
            int h = Convert.ToInt32(t.GetProperty("Height")!.GetValue(region));
            if (w > 0 && h > 0)
                list.Add(new RectI(x, y, w, h));
        }

        return list.Count == 0 ? Array.Empty<RectI>() : list;
    }

    /// <summary>
    /// DRM-protected windows are delivered as solid black. Sample a sparse pixel grid; if several
    /// consecutive frames are fully black, report protected content (no bypass attempt).
    /// </summary>
    private bool DetectProtectedContent(CaptureFrame frame)
    {
        if (_protectedReported)
            return true;

        if (!frame.TryGetCpuPixels(out var bgra, out int stride) || bgra.IsEmpty)
            return false;

        if (IsFrameBlack(bgra.Span, stride, frame.Width, frame.Height))
        {
            _consecutiveBlackFrames++;
            if (_consecutiveBlackFrames >= 5)
            {
                _protectedReported = true;
                _logger.LogWarning("Detected all-black output — treating as protected content.");
                RaiseError(CaptureErrorKind.ProtectedContent,
                    "The target appears to be DRM-protected and returns no pixels.");
                return true;
            }
        }
        else
        {
            _consecutiveBlackFrames = 0;
        }

        return false;
    }

    private static bool IsFrameBlack(ReadOnlySpan<byte> bgra, int stride, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;

        // Sample ~256 points across the frame; tolerate tiny non-zero noise.
        int stepX = Math.Max(1, width / 16);
        int stepY = Math.Max(1, height / 16);
        for (int y = 0; y < height; y += stepY)
        {
            int rowBase = y * stride;
            for (int x = 0; x < width; x += stepX)
            {
                int idx = rowBase + x * 4;
                if (idx + 2 >= bgra.Length)
                    continue;
                // BGRA — ignore alpha, threshold on color channels.
                if (bgra[idx] > 4 || bgra[idx + 1] > 4 || bgra[idx + 2] > 4)
                    return false;
            }
        }

        return true;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _logger.LogInformation("WGC capture item closed (target gone).");
        RaiseError(CaptureErrorKind.TargetClosed, "The capture target was closed.");
        _ = StopAsync();
    }

    private void RaiseError(CaptureErrorKind kind, string message, Exception? ex = null) =>
        Error?.Invoke(this, new CaptureErrorEventArgs(kind, message, ex));

    private void TeardownLocked()
    {
        _running = false;

        if (_framePool is not null)
            _framePool.FrameArrived -= OnFrameArrived;
        if (_item is not null)
            _item.Closed -= OnItemClosed;

        try { _session?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Session dispose."); }
        try { _framePool?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Frame pool dispose."); }

        _session = null;
        _framePool = null;
        _item = null;
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
