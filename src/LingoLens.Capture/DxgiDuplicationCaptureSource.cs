using System.Diagnostics;
using Microsoft.Extensions.Logging;
using LingoLens.Capture.Interop;
using LingoLens.Core;
using LingoLens.Core.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingoLens.Capture;

/// <summary>
/// Captures a full monitor via DXGI Desktop Duplication. Used for Monitor mode and as a fallback when
/// WGC is unavailable. Runs a dedicated <c>AcquireNextFrame</c> loop on a background thread, surfaces
/// the OS-reported dirty + move rectangles on each frame, and transparently recovers from
/// <c>DXGI_ERROR_ACCESS_LOST</c> by recreating the duplication.
/// </summary>
public sealed class DxgiDuplicationCaptureSource : IScreenCaptureSource
{
    // HRESULTs that the duplication API returns through SharpGen's Result.
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A002B);

    private readonly DeviceResources _resources;
    private readonly ILogger<DxgiDuplicationCaptureSource> _logger;
    private readonly object _sync = new();

    private CaptureTarget? _target;
    private CaptureSettings _settings = new();
    private RectI _monitorBounds = RectI.Empty;
    private double _dpi = 96d;

    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private long _minFrameIntervalTicks;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>Creates a desktop-duplication capture source on the shared device.</summary>
    public DxgiDuplicationCaptureSource(DeviceResources resources, ILogger<DxgiDuplicationCaptureSource> logger)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public CaptureTarget? Target => _target;

    /// <inheritdoc />
    public event EventHandler<FrameArrivedEventArgs>? FrameArrived;

    /// <inheritdoc />
    public event EventHandler<CaptureErrorEventArgs>? Error;

    /// <inheritdoc />
    public bool CanCapture(CaptureTarget target) =>
        target.Mode == CaptureMode.Monitor && target.MonitorHandle != 0;

    /// <inheritdoc />
    public Task StartAsync(CaptureTarget target, CaptureSettings settings, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_running)
                throw new InvalidOperationException("Capture is already running.");
            if (target.Mode != CaptureMode.Monitor || target.MonitorHandle == 0)
                throw new ArgumentException("DXGI duplication requires a Monitor target with a valid HMONITOR.", nameof(target));

            _target = target;
            _settings = settings ?? new CaptureSettings();
            _minFrameIntervalTicks = _settings.MaxFps > 0 ? Stopwatch.Frequency / _settings.MaxFps : 0;

            NativeMethods.TryGetMonitorBounds(target.MonitorHandle, out _monitorBounds, out _, out _);
            _dpi = NativeMethods.GetMonitorDpi(target.MonitorHandle);

            _cts = new CancellationTokenSource();
            _running = true;
            _captureThread = new Thread(() => CaptureLoop(target.MonitorHandle, _cts.Token))
            {
                IsBackground = true,
                Name = "LingoLens.DxgiDuplication",
            };
            _captureThread.Start();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        Thread? thread;
        lock (_sync)
        {
            if (!_running && _captureThread is null)
                return Task.CompletedTask;
            _running = false;
            _cts?.Cancel();
            thread = _captureThread;
            _captureThread = null;
        }

        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(2));

        lock (_sync)
        {
            _cts?.Dispose();
            _cts = null;
        }

        return Task.CompletedTask;
    }

    private void CaptureLoop(nint hMonitor, CancellationToken ct)
    {
        IDXGIOutputDuplication? duplication = null;
        IDXGIOutput1? output1 = null;
        long lastFrameTicks = 0;

        try
        {
            while (!ct.IsCancellationRequested && _running)
            {
                if (duplication is null)
                {
                    if (!TryCreateDuplication(hMonitor, out duplication, out output1))
                    {
                        // Could not (re)create; back off briefly then retry unless cancelled.
                        if (ct.WaitHandle.WaitOne(200))
                            break;
                        continue;
                    }
                }

                var result = duplication.AcquireNextFrame(100, out OutduplFrameInfo frameInfo, out IDXGIResource? desktopResource);

                if (result.Code == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    desktopResource?.Dispose();
                    continue; // no new frame within the timeout
                }

                if (result.Code == DXGI_ERROR_ACCESS_LOST || result.Code == DXGI_ERROR_ACCESS_DENIED)
                {
                    _logger.LogInformation("Desktop duplication access lost — recreating.");
                    desktopResource?.Dispose();
                    DisposeDuplication(ref duplication, ref output1);
                    continue;
                }

                if (result.Failure || desktopResource is null)
                {
                    desktopResource?.Dispose();
                    _logger.LogWarning("AcquireNextFrame failed: 0x{Code:X8}", result.Code);
                    DisposeDuplication(ref duplication, ref output1);
                    continue;
                }

                try
                {
                    long now = Stopwatch.GetTimestamp();
                    bool throttled = _minFrameIntervalTicks > 0 && lastFrameTicks != 0 &&
                                     now - lastFrameTicks < _minFrameIntervalTicks;

                    // AccumulatedFrames == 0 with no presentation means only the pointer moved (or the
                    // frame carries no desktop update); skip those. Parenthesize the predicate so the
                    // content check is OR-ed with throttling rather than being swallowed by '&&'.
                    bool mouseOnlyUpdate = frameInfo.AccumulatedFrames == 0 && frameInfo.LastPresentTime == 0;
                    if (throttled || mouseOnlyUpdate)
                    {
                        continue;
                    }

                    using var dxgiTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                    var texDesc = dxgiTexture.Description;
                    var dirty = ReadDirtyRects(duplication, frameInfo, (int)texDesc.Width, (int)texDesc.Height);

                    // Copy the (shared, read-only) desktop image into our own texture so we can release
                    // the duplication frame immediately and let inference outlive the acquire window.
                    var copy = CopyDesktopTexture(dxgiTexture);

                    var frame = new CaptureFrame(
                        _resources,
                        copy,
                        now,
                        _dpi,
                        dirty,
                        ownsTexture: true);

                    lastFrameTicks = now;
                    FrameArrived?.Invoke(this, new FrameArrivedEventArgs(frame));
                }
                finally
                {
                    desktopResource.Dispose();
                    duplication.ReleaseFrame();
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Desktop duplication loop terminated unexpectedly.");
            Error?.Invoke(this, new CaptureErrorEventArgs(CaptureErrorKind.DeviceLost, "Desktop duplication failed.", ex));
        }
        finally
        {
            DisposeDuplication(ref duplication, ref output1);
            _running = false;
        }
    }

    private ID3D11Texture2D CopyDesktopTexture(ID3D11Texture2D source)
    {
        var srcDesc = source.Description;
        var desc = new Texture2DDescription
        {
            Width = srcDesc.Width,
            Height = srcDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = srcDesc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        lock (_resources.ContextLock)
        {
            var dst = _resources.Device.CreateTexture2D(ref desc);
            _resources.Context.CopyResource(dst, source);
            return dst;
        }
    }

    private static readonly int MoveRectSize = System.Runtime.InteropServices.Marshal.SizeOf<OutduplMoveRect>();
    private static readonly int DirtyRectSize = System.Runtime.InteropServices.Marshal.SizeOf<Vortice.RawRect>();
    private static readonly int MoreDataCode = Vortice.DXGI.ResultCode.MoreData.Code;

    private IReadOnlyList<RectI> ReadDirtyRects(IDXGIOutputDuplication duplication, in OutduplFrameInfo frameInfo, int texWidth, int texHeight)
    {
        if (!_settings.PreferDirtyRegions || frameInfo.TotalMetadataBufferSize == 0)
            return Array.Empty<RectI>();

        var frameBounds = new RectI(0, 0, texWidth, texHeight);
        if (frameBounds.IsEmpty)
            return Array.Empty<RectI>();

        var rects = new List<RectI>();
        try
        {
            // The metadata APIs use the documented two-call pattern: TotalMetadataBufferSize is the
            // COMBINED move+dirty budget, so it's only a safe upper bound for either list. Size each
            // buffer with Marshal.SizeOf for the real struct size, and if the runtime reports
            // DXGI_ERROR_MORE_DATA, re-query with the byte count it asks for instead of dropping rects.

            // Move rects: report destination regions (where content landed).
            int moveCapacity = Math.Max(1, (int)frameInfo.TotalMetadataBufferSize / MoveRectSize);
            var moveBuffer = new OutduplMoveRect[moveCapacity];
            var moveResult = duplication.GetFrameMoveRects((uint)(moveBuffer.Length * MoveRectSize), moveBuffer, out uint moveBytes);
            if (moveResult.Code == MoreDataCode && moveBytes > 0)
            {
                moveBuffer = new OutduplMoveRect[Math.Max(1, (int)(moveBytes / MoveRectSize))];
                moveResult = duplication.GetFrameMoveRects((uint)(moveBuffer.Length * MoveRectSize), moveBuffer, out moveBytes);
            }
            if (moveResult.Success)
            {
                int moveCount = (int)(moveBytes / MoveRectSize);
                for (int i = 0; i < moveCount && i < moveBuffer.Length; i++)
                    AddClampedRect(rects, moveBuffer[i].DestinationRect, frameBounds);
            }
            else if (moveResult.Failure)
            {
                _logger.LogDebug("GetFrameMoveRects failed: 0x{Code:X8}", moveResult.Code);
            }

            int dirtyCapacity = Math.Max(1, (int)frameInfo.TotalMetadataBufferSize / DirtyRectSize);
            var dirtyBuffer = new Vortice.RawRect[dirtyCapacity];
            var dirtyResult = duplication.GetFrameDirtyRects((uint)(dirtyBuffer.Length * DirtyRectSize), dirtyBuffer, out uint dirtyBytes);
            if (dirtyResult.Code == MoreDataCode && dirtyBytes > 0)
            {
                dirtyBuffer = new Vortice.RawRect[Math.Max(1, (int)(dirtyBytes / DirtyRectSize))];
                dirtyResult = duplication.GetFrameDirtyRects((uint)(dirtyBuffer.Length * DirtyRectSize), dirtyBuffer, out dirtyBytes);
            }
            if (dirtyResult.Success)
            {
                int dirtyCount = (int)(dirtyBytes / DirtyRectSize);
                for (int i = 0; i < dirtyCount && i < dirtyBuffer.Length; i++)
                    AddClampedRect(rects, dirtyBuffer[i], frameBounds);
            }
            else if (dirtyResult.Failure)
            {
                _logger.LogDebug("GetFrameDirtyRects failed: 0x{Code:X8}", dirtyResult.Code);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read duplication dirty rects.");
            return Array.Empty<RectI>();
        }

        return rects.Count == 0 ? Array.Empty<RectI>() : rects;
    }

    private static void AddClampedRect(List<RectI> sink, Vortice.RawRect r, RectI frameBounds)
    {
        // Duplication rects are in output-local (texture) coordinates — already frame-relative.
        // Clamp to the frame bounds (0,0,texW,texH): the OS can report rects that extend past the
        // surface (and rotation maps coordinates differently), so clamping keeps every emitted rect
        // in-bounds for downstream consumers regardless of DXGI_OUTDUPL_DESC.Rotation.
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0)
            return;

        var clamped = new RectI(r.Left, r.Top, w, h).Intersect(frameBounds);
        if (!clamped.IsEmpty)
            sink.Add(clamped);
    }

    private bool TryCreateDuplication(nint hMonitor, out IDXGIOutputDuplication? duplication, out IDXGIOutput1? output1)
    {
        duplication = null;
        output1 = null;

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter!.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                    {
                        using (output)
                        {
                            if (output!.Description.Monitor != hMonitor)
                                continue;

                            var candidate = output.QueryInterface<IDXGIOutput1>();
                            try
                            {
                                duplication = candidate.DuplicateOutput(_resources.Device);
                                output1 = candidate;
                                _logger.LogInformation("Created desktop duplication for monitor 0x{Mon:X}.", hMonitor);
                                return true;
                            }
                            catch
                            {
                                candidate.Dispose();
                                throw;
                            }
                        }
                    }
                }
            }

            _logger.LogWarning("No DXGI output matched monitor 0x{Mon:X}.", hMonitor);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create desktop duplication.");
            return false;
        }
    }

    private static void DisposeDuplication(ref IDXGIOutputDuplication? duplication, ref IDXGIOutput1? output1)
    {
        try { duplication?.ReleaseFrame(); } catch { /* ignore */ }
        duplication?.Dispose();
        output1?.Dispose();
        duplication = null;
        output1 = null;
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
