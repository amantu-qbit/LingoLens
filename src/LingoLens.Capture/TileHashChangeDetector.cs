using LingoLens.Core;
using LingoLens.Core.Capture;

namespace LingoLens.Capture;

/// <summary>
/// Gates the pipeline by detecting which parts of a frame actually changed. If the frame already
/// carries OS-reported dirty rects they are merged and returned directly (cheapest path). Otherwise a
/// 64-bit difference-hash (dHash) is computed per tile of an NxN grid from CPU pixels and compared to
/// the previous frame's tile hashes; tiles whose Hamming distance exceeds a threshold are considered
/// changed and merged into a small set of regions.
/// </summary>
/// <remarks>Not thread-safe: a single detector instance is owned by one capture/gate thread.</remarks>
public sealed class TileHashChangeDetector : IChangeDetector
{
    private readonly int _gridCols;
    private readonly int _gridRows;
    private readonly int _hammingThreshold;

    private ulong[]? _previousHashes;
    private int _prevCols;
    private int _prevRows;
    private int _prevWidth;
    private int _prevHeight;

    /// <summary>Creates a tile-hash change detector.</summary>
    /// <param name="gridCols">Number of tile columns (default 16).</param>
    /// <param name="gridRows">Number of tile rows (default 16).</param>
    /// <param name="hammingThreshold">
    /// Per-tile dHash Hamming distance above which a tile is "changed" (0–64; default 6).
    /// </param>
    public TileHashChangeDetector(int gridCols = 16, int gridRows = 16, int hammingThreshold = 6)
    {
        _gridCols = Math.Clamp(gridCols, 1, 64);
        _gridRows = Math.Clamp(gridRows, 1, 64);
        _hammingThreshold = Math.Clamp(hammingThreshold, 0, 64);
    }

    /// <inheritdoc />
    public ChangeResult Detect(ICaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Width <= 0 || frame.Height <= 0)
            return ChangeResult.None;

        // 1) Trust the OS dirty rects when present.
        if (frame.DirtyRects.Count > 0)
        {
            var merged = MergeRects(frame.DirtyRects, frame.Width, frame.Height);
            // Cache hashes so a subsequent dirty-less frame compares against this content.
            if (frame.TryGetCpuPixels(out var px, out int st))
                _previousHashes = ComputeTileHashes(px.Span, st, frame.Width, frame.Height, out _prevCols, out _prevRows);
            _prevWidth = frame.Width;
            _prevHeight = frame.Height;
            return merged.Count == 0 ? ChangeResult.None : new ChangeResult(true, merged);
        }

        // 2) Fall back to tile dHash diffing on CPU pixels.
        if (!frame.TryGetCpuPixels(out var bgra, out int stride) || bgra.IsEmpty)
        {
            // No pixels to compare — be safe and treat the whole frame as dirty.
            return ChangeResult.Whole(frame.Width, frame.Height);
        }

        var hashes = ComputeTileHashes(bgra.Span, stride, frame.Width, frame.Height, out int cols, out int rows);

        // First frame, or geometry changed → whole frame is "changed".
        if (_previousHashes is null || cols != _prevCols || rows != _prevRows ||
            frame.Width != _prevWidth || frame.Height != _prevHeight)
        {
            _previousHashes = hashes;
            _prevCols = cols;
            _prevRows = rows;
            _prevWidth = frame.Width;
            _prevHeight = frame.Height;
            return ChangeResult.Whole(frame.Width, frame.Height);
        }

        int tileW = (frame.Width + cols - 1) / cols;
        int tileH = (frame.Height + rows - 1) / rows;

        var changedTiles = new List<RectI>();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int idx = r * cols + c;
                int distance = PopCount(hashes[idx] ^ _previousHashes[idx]);
                if (distance > _hammingThreshold)
                {
                    int x = c * tileW;
                    int y = r * tileH;
                    int w = Math.Min(tileW, frame.Width - x);
                    int h = Math.Min(tileH, frame.Height - y);
                    changedTiles.Add(new RectI(x, y, w, h));
                }
            }
        }

        _previousHashes = hashes;
        _prevCols = cols;
        _prevRows = rows;
        _prevWidth = frame.Width;
        _prevHeight = frame.Height;

        if (changedTiles.Count == 0)
            return ChangeResult.None;

        var mergedRegions = MergeRects(changedTiles, frame.Width, frame.Height);
        return new ChangeResult(true, mergedRegions);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _previousHashes = null;
        _prevCols = _prevRows = _prevWidth = _prevHeight = 0;
    }

    /// <summary>
    /// Computes a 9x8 grayscale dHash for each tile, yielding a 64-bit signature per tile. Comparing
    /// adjacent horizontal samples makes the hash robust to small brightness shifts.
    /// </summary>
    private ulong[] ComputeTileHashes(ReadOnlySpan<byte> bgra, int stride, int width, int height, out int cols, out int rows)
    {
        cols = _gridCols;
        rows = _gridRows;
        var hashes = new ulong[cols * rows];

        int tileW = (width + cols - 1) / cols;
        int tileH = (height + rows - 1) / rows;

        // dHash uses a 9x8 reduced grid → 8x8 = 64 comparison bits.
        const int sw = 9;
        const int sh = 8;
        Span<int> samples = stackalloc int[sw * sh];

        for (int r = 0; r < rows; r++)
        {
            int tileY = r * tileH;
            int curH = Math.Min(tileH, height - tileY);
            for (int c = 0; c < cols; c++)
            {
                int tileX = c * tileW;
                int curW = Math.Min(tileW, width - tileX);
                if (curW <= 0 || curH <= 0)
                {
                    hashes[r * cols + c] = 0;
                    continue;
                }

                // Sample the tile into a 9x8 luminance grid (nearest-neighbour).
                for (int sy = 0; sy < sh; sy++)
                {
                    int py = tileY + (int)((sy + 0.5) * curH / sh);
                    if (py >= height) py = height - 1;
                    int rowBase = py * stride;
                    for (int sx = 0; sx < sw; sx++)
                    {
                        int px = tileX + (int)((sx + 0.5) * curW / sw);
                        if (px >= width) px = width - 1;
                        int p = rowBase + px * 4;
                        int lum;
                        if (p + 2 < bgra.Length)
                        {
                            // BGRA → approximate luminance (integer weights).
                            int b = bgra[p], g = bgra[p + 1], rr = bgra[p + 2];
                            lum = (rr * 77 + g * 150 + b * 29) >> 8;
                        }
                        else
                        {
                            lum = 0;
                        }
                        samples[sy * sw + sx] = lum;
                    }
                }

                ulong hash = 0;
                int bit = 0;
                for (int sy = 0; sy < sh; sy++)
                {
                    for (int sx = 0; sx < sw - 1; sx++)
                    {
                        if (samples[sy * sw + sx] < samples[sy * sw + sx + 1])
                            hash |= 1UL << bit;
                        bit++;
                    }
                }

                hashes[r * cols + c] = hash;
            }
        }

        return hashes;
    }

    private static int PopCount(ulong value) => System.Numerics.BitOperations.PopCount(value);

    /// <summary>
    /// Coalesces a set of (possibly adjacent) rectangles into a small set by repeatedly merging any
    /// two whose inflated bounds intersect. Results are clamped to the frame bounds.
    /// </summary>
    private static IReadOnlyList<RectI> MergeRects(IReadOnlyList<RectI> input, int width, int height)
    {
        var frame = new RectI(0, 0, width, height);
        var rects = new List<RectI>(input.Count);
        foreach (var r in input)
        {
            var clamped = r.Intersect(frame);
            if (!clamped.IsEmpty)
                rects.Add(clamped);
        }

        if (rects.Count <= 1)
            return rects;

        bool mergedAny = true;
        while (mergedAny && rects.Count > 1)
        {
            mergedAny = false;
            for (int i = 0; i < rects.Count && !mergedAny; i++)
            {
                for (int j = i + 1; j < rects.Count; j++)
                {
                    // Inflate by a couple of pixels so touching tiles coalesce.
                    if (rects[i].Inflate(2, 2).IntersectsWith(rects[j]))
                    {
                        rects[i] = rects[i].Union(rects[j]).Intersect(frame);
                        rects.RemoveAt(j);
                        mergedAny = true;
                        break;
                    }
                }
            }
        }

        return rects;
    }
}
