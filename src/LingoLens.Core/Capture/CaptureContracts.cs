namespace LingoLens.Core.Capture;

/// <summary>What the user is pointing LingoLens at.</summary>
public enum CaptureMode
{
    /// <summary>A specific top-level window (captured even when occluded).</summary>
    Window,
    /// <summary>An arbitrary user-drawn rectangle on the virtual desktop.</summary>
    Region,
    /// <summary>An entire monitor / display output.</summary>
    Monitor,
}

/// <summary>Immutable description of a capture target.</summary>
public sealed record CaptureTarget
{
    public CaptureMode Mode { get; init; }

    /// <summary>HWND for <see cref="CaptureMode.Window"/>.</summary>
    public nint WindowHandle { get; init; }

    /// <summary>HMONITOR for <see cref="CaptureMode.Monitor"/>.</summary>
    public nint MonitorHandle { get; init; }

    /// <summary>Region in virtual-desktop pixel coordinates for <see cref="CaptureMode.Region"/>.</summary>
    public RectI Region { get; init; } = RectI.Empty;

    /// <summary>
    /// Where the target sits on the virtual desktop, in physical pixels — used to position the overlay
    /// for <see cref="CaptureMode.Monitor"/> and <see cref="CaptureMode.Window"/> targets (Region targets
    /// use <see cref="Region"/>). Empty when unknown.
    /// </summary>
    public RectI ScreenBounds { get; init; } = RectI.Empty;

    /// <summary>Optional friendly label (window title / monitor name) for UI.</summary>
    public string? DisplayName { get; init; }

    public static CaptureTarget ForWindow(nint hwnd, string? name = null, RectI screenBounds = default) =>
        new() { Mode = CaptureMode.Window, WindowHandle = hwnd, DisplayName = name, ScreenBounds = screenBounds };

    public static CaptureTarget ForMonitor(nint hmonitor, string? name = null, RectI screenBounds = default) =>
        new() { Mode = CaptureMode.Monitor, MonitorHandle = hmonitor, DisplayName = name, ScreenBounds = screenBounds };

    public static CaptureTarget ForRegion(RectI region, string? name = null) =>
        new() { Mode = CaptureMode.Region, Region = region, ScreenBounds = region, DisplayName = name };
}

/// <summary>
/// A single captured frame. Prefer the GPU texture path (<see cref="D3D11Texture"/>) for zero-copy
/// inference; <see cref="TryGetCpuPixels"/> is the portable fallback. Frames are pooled — dispose
/// promptly to return them to the capturer.
/// </summary>
public interface ICaptureFrame : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>High-resolution capture timestamp (Stopwatch ticks).</summary>
    long TimestampTicks { get; }

    /// <summary>Effective DPI of the captured surface (96 = 100%).</summary>
    double Dpi { get; }

    /// <summary>Native ID3D11Texture2D pointer, or 0 if this is a CPU-only frame.</summary>
    nint D3D11Texture { get; }

    /// <summary>
    /// OS-reported changed rectangles since the previous frame (WGC DirtyRegion / DXGI dirty rects).
    /// Empty means "unknown — treat the whole frame as dirty".
    /// </summary>
    IReadOnlyList<RectI> DirtyRects { get; }

    /// <summary>Try to obtain BGRA8 pixels on the CPU. Returns false for GPU-only frames not yet mapped.</summary>
    bool TryGetCpuPixels(out ReadOnlyMemory<byte> bgra, out int stride);
}

public sealed class FrameArrivedEventArgs(ICaptureFrame frame) : EventArgs
{
    public ICaptureFrame Frame { get; } = frame;
}

public enum CaptureErrorKind { Unknown, ProtectedContent, DeviceLost, TargetClosed, Unsupported }

public sealed class CaptureErrorEventArgs(CaptureErrorKind kind, string message, Exception? exception = null) : EventArgs
{
    public CaptureErrorKind Kind { get; } = kind;
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}

/// <summary>Tunables for a capture session.</summary>
public sealed record CaptureSettings
{
    /// <summary>Upper bound on delivered frames per second (the gate/cadence may go lower).</summary>
    public int MaxFps { get; init; } = 30;
    public bool CaptureCursor { get; init; } = false;
    /// <summary>Whether to show the OS yellow capture border (false where the platform allows).</summary>
    public bool ShowCaptureBorder { get; init; } = false;
    /// <summary>Request OS dirty-region reporting where available.</summary>
    public bool PreferDirtyRegions { get; init; } = true;
}

/// <summary>A source of captured frames for a single target.</summary>
public interface IScreenCaptureSource : IAsyncDisposable
{
    bool IsRunning { get; }
    CaptureTarget? Target { get; }

    /// <summary>True if this implementation can capture the given target on this OS/hardware.</summary>
    bool CanCapture(CaptureTarget target);

    event EventHandler<FrameArrivedEventArgs>? FrameArrived;
    event EventHandler<CaptureErrorEventArgs>? Error;

    Task StartAsync(CaptureTarget target, CaptureSettings settings, CancellationToken ct = default);
    Task StopAsync();
}

/// <summary>Picks a concrete capture source for a target (WGC vs DXGI vs region).</summary>
public interface ICaptureSourceFactory
{
    IScreenCaptureSource Create(CaptureTarget target);
}

/// <summary>Result of comparing the current frame to the previous one.</summary>
public readonly record struct ChangeResult(bool HasChanges, IReadOnlyList<RectI> ChangedRegions)
{
    public static readonly ChangeResult None = new(false, Array.Empty<RectI>());
    public static ChangeResult Whole(int w, int h) => new(true, new[] { new RectI(0, 0, w, h) });
}

/// <summary>
/// Gates the pipeline: only frames/regions that actually changed proceed to OCR. Implementations may
/// use OS dirty rects, GPU tile diffing, or perceptual hashing.
/// </summary>
public interface IChangeDetector
{
    ChangeResult Detect(ICaptureFrame frame);
    void Reset();
}
