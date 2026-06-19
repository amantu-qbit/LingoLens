using LingoLens.Core;
using LingoLens.Core.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingoLens.Capture;

/// <summary>
/// A captured frame backed by a GPU <see cref="ID3D11Texture2D"/>. CPU pixels are produced lazily by
/// copying into a CPU-readable staging texture and mapping it; the result is cached for the frame's
/// lifetime. Disposing returns the texture (and any staging texture) to the GPU / releases them.
/// </summary>
internal sealed class CaptureFrame : ICaptureFrame
{
    private readonly DeviceResources _resources;
    private readonly bool _ownsTexture;
    private readonly Action? _onDisposed;
    private readonly object _cpuLock = new();

    private ID3D11Texture2D? _texture;
    private byte[]? _cpuPixels;
    private int _cpuStride;
    private bool _cpuAttempted;
    private bool _disposed;

    /// <summary>Creates a frame wrapping a GPU texture.</summary>
    /// <param name="resources">Shared device used for the staging copy.</param>
    /// <param name="texture">The captured BGRA texture.</param>
    /// <param name="timestampTicks">High-resolution capture timestamp (Stopwatch ticks).</param>
    /// <param name="dpi">Effective DPI of the surface.</param>
    /// <param name="dirtyRects">OS-reported dirty rects (empty ⇒ whole-frame unknown).</param>
    /// <param name="ownsTexture">When true the texture is disposed with the frame.</param>
    /// <param name="onDisposed">Optional callback invoked after disposal (e.g. pool return / ReleaseFrame).</param>
    public CaptureFrame(
        DeviceResources resources,
        ID3D11Texture2D texture,
        long timestampTicks,
        double dpi,
        IReadOnlyList<RectI> dirtyRects,
        bool ownsTexture = true,
        Action? onDisposed = null)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _texture = texture ?? throw new ArgumentNullException(nameof(texture));
        _ownsTexture = ownsTexture;
        _onDisposed = onDisposed;

        var desc = texture.Description;
        Width = (int)desc.Width;
        Height = (int)desc.Height;
        TimestampTicks = timestampTicks;
        Dpi = dpi <= 0 ? 96d : dpi;
        DirtyRects = dirtyRects ?? Array.Empty<RectI>();
    }

    public int Width { get; }

    public int Height { get; }

    public long TimestampTicks { get; }

    public double Dpi { get; }

    public nint D3D11Texture => _texture?.NativePointer ?? 0;

    public IReadOnlyList<RectI> DirtyRects { get; }

    public bool TryGetCpuPixels(out ReadOnlyMemory<byte> bgra, out int stride)
    {
        if (_disposed || _texture is null)
        {
            bgra = ReadOnlyMemory<byte>.Empty;
            stride = 0;
            return false;
        }

        // Serialize the lazy CPU-pixel materialization so concurrent first-time callers don't race
        // on _cpuAttempted/_cpuPixels (the staging copy/map would otherwise run more than once and
        // corrupt the cached state).
        lock (_cpuLock)
        {
            if (_disposed || _texture is null)
            {
                bgra = ReadOnlyMemory<byte>.Empty;
                stride = 0;
                return false;
            }

            if (_cpuPixels is not null)
            {
                bgra = _cpuPixels.AsMemory(0, _cpuStride * Height);
                stride = _cpuStride;
                return true;
            }

            if (_cpuAttempted)
            {
                bgra = ReadOnlyMemory<byte>.Empty;
                stride = 0;
                return false;
            }

            _cpuAttempted = true;

            // Copy the GPU texture into a tightly-described CPU-readable staging texture, then map it.
            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };

            ID3D11Texture2D? staging = null;
            try
            {
                staging = _resources.Device.CreateTexture2D(ref stagingDesc);

                lock (_resources.ContextLock)
                {
                    var context = _resources.Context;
                    context.CopyResource(staging, _texture);

                    var mapInfo = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    // Map/Unmap must be paired under the SAME ContextLock acquisition; the cleanup
                    // Unmap on the failure path therefore runs inside this lock via try/finally.
                    bool mapped = true;
                    try
                    {
                        int srcStride = (int)mapInfo.RowPitch;
                        int dstStride = Width * 4;
                        var buffer = new byte[dstStride * Height];

                        unsafe
                        {
                            byte* src = (byte*)mapInfo.DataPointer;
                            fixed (byte* dst = buffer)
                            {
                                if (srcStride == dstStride)
                                {
                                    Buffer.MemoryCopy(src, dst, buffer.Length, buffer.Length);
                                }
                                else
                                {
                                    for (int y = 0; y < Height; y++)
                                        Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, dstStride, dstStride);
                                }
                            }
                        }

                        context.Unmap(staging, 0);
                        mapped = false;

                        _cpuPixels = buffer;
                        _cpuStride = dstStride;
                    }
                    finally
                    {
                        if (mapped)
                        {
                            try { context.Unmap(staging, 0); } catch { /* best effort */ }
                        }
                    }
                }
            }
            catch
            {
                bgra = ReadOnlyMemory<byte>.Empty;
                stride = 0;
                return false;
            }
            finally
            {
                staging?.Dispose();
            }

            bgra = _cpuPixels.AsMemory(0, _cpuStride * Height);
            stride = _cpuStride;
            return true;
        }
    }

    public void Dispose()
    {
        // Take _cpuLock so we don't tear down _texture/_cpuPixels while an in-flight
        // TryGetCpuPixels is mid-copy (it reads _texture under the same lock).
        lock (_cpuLock)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_ownsTexture)
                _texture?.Dispose();
            _texture = null;
            _cpuPixels = null;
        }

        _onDisposed?.Invoke();
    }
}
