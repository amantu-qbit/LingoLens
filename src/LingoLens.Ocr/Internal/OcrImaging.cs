using System.Runtime.CompilerServices;
using LingoLens.Core;

namespace LingoLens.Ocr.Internal;

/// <summary>
/// Pure-managed image helpers operating directly on BGRA8 pixel spans (no external image library).
/// All routines are allocation-conscious and side-effect free where practical.
/// </summary>
internal static class OcrImaging
{
    /// <summary>PP-OCR detection normalization mean (RGB order), scaled to [0,1] input.</summary>
    public static readonly float[] DetMean = { 0.485f, 0.456f, 0.406f };

    /// <summary>PP-OCR detection normalization std (RGB order).</summary>
    public static readonly float[] DetStd = { 0.229f, 0.224f, 0.225f };

    /// <summary>
    /// A lightweight, copyable view over a BGRA8 image buffer. Coordinates are in image pixels.
    /// </summary>
    public readonly struct BgraImage
    {
        public readonly ReadOnlyMemory<byte> Pixels;
        public readonly int Width;
        public readonly int Height;
        public readonly int Stride;

        public BgraImage(ReadOnlyMemory<byte> pixels, int width, int height, int stride)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            Stride = stride;
        }

        /// <summary>Reads a single pixel as packed (b,g,r,a). Clamps out-of-range coordinates to the edge.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (byte B, byte G, byte R, byte A) At(int x, int y)
        {
            if (x < 0) x = 0; else if (x >= Width) x = Width - 1;
            if (y < 0) y = 0; else if (y >= Height) y = Height - 1;
            var span = Pixels.Span;
            int i = y * Stride + x * 4;
            return (span[i], span[i + 1], span[i + 2], span[i + 3]);
        }
    }

    /// <summary>
    /// Clamps a region of interest to the frame bounds. Returns <see cref="RectI.Empty"/> if the ROI
    /// does not intersect the frame.
    /// </summary>
    public static RectI ClampRoi(RectI roi, int frameWidth, int frameHeight) =>
        roi.Intersect(new RectI(0, 0, frameWidth, frameHeight));

    /// <summary>
    /// Bilinearly resamples an axis-aligned sub-rectangle of <paramref name="src"/> into an RGB float
    /// NCHW tensor buffer of shape [1,3,dstH,dstW], applying mean/std normalization. The channel order
    /// written is RGB (PP-OCR convention). The destination buffer must be at least 3*dstH*dstW long.
    /// </summary>
    public static void ResizeRoiToNchwRgb(
        in BgraImage src, RectI roi, int dstW, int dstH,
        ReadOnlySpan<float> mean, ReadOnlySpan<float> std, Span<float> dst)
    {
        int plane = dstW * dstH;
        // Map destination pixel centers back into the source ROI.
        double sx = roi.Width / (double)dstW;
        double sy = roi.Height / (double)dstH;
        var pix = src.Pixels.Span;

        for (int dy = 0; dy < dstH; dy++)
        {
            double fy = (dy + 0.5) * sy - 0.5 + roi.Y;
            int y0 = (int)Math.Floor(fy);
            double wy = fy - y0;
            int y0c = Clamp(y0, 0, src.Height - 1);
            int y1c = Clamp(y0 + 1, 0, src.Height - 1);

            for (int dx = 0; dx < dstW; dx++)
            {
                double fx = (dx + 0.5) * sx - 0.5 + roi.X;
                int x0 = (int)Math.Floor(fx);
                double wx = fx - x0;
                int x0c = Clamp(x0, 0, src.Width - 1);
                int x1c = Clamp(x0 + 1, 0, src.Width - 1);

                // Fetch the four neighbours (BGRA).
                int i00 = y0c * src.Stride + x0c * 4;
                int i01 = y0c * src.Stride + x1c * 4;
                int i10 = y1c * src.Stride + x0c * 4;
                int i11 = y1c * src.Stride + x1c * 4;

                double w00 = (1 - wx) * (1 - wy);
                double w01 = wx * (1 - wy);
                double w10 = (1 - wx) * wy;
                double w11 = wx * wy;

                // R channel = byte[2], G = byte[1], B = byte[0].
                double r = pix[i00 + 2] * w00 + pix[i01 + 2] * w01 + pix[i10 + 2] * w10 + pix[i11 + 2] * w11;
                double g = pix[i00 + 1] * w00 + pix[i01 + 1] * w01 + pix[i10 + 1] * w10 + pix[i11 + 1] * w11;
                double b = pix[i00 + 0] * w00 + pix[i01 + 0] * w01 + pix[i10 + 0] * w10 + pix[i11 + 0] * w11;

                int o = dy * dstW + dx;
                dst[0 * plane + o] = (float)((r / 255.0 - mean[0]) / std[0]);
                dst[1 * plane + o] = (float)((g / 255.0 - mean[1]) / std[1]);
                dst[2 * plane + o] = (float)((b / 255.0 - mean[2]) / std[2]);
            }
        }
    }

    /// <summary>
    /// Perspective-warps the (possibly rotated) <paramref name="quad"/> region of the source image into a
    /// fixed-height recognition strip and writes it as a normalized NCHW RGB tensor of shape
    /// [1,3,height,width]. PP-OCR rec normalization is (pixel/255 - 0.5) / 0.5 per channel.
    /// </summary>
    public static void WarpQuadToRecStrip(
        in BgraImage src, in Quad quad, int width, int height, Span<float> dst)
    {
        int plane = width * height;
        // Bilinear-interpolate the quad corners across the destination grid.
        var tl = quad.TopLeft; var tr = quad.TopRight; var br = quad.BottomRight; var bl = quad.BottomLeft;
        var pix = src.Pixels.Span;

        for (int dy = 0; dy < height; dy++)
        {
            double v = height > 1 ? dy / (double)(height - 1) : 0.0;
            // Left/right edge points at this row.
            double lx = tl.X + (bl.X - tl.X) * v;
            double ly = tl.Y + (bl.Y - tl.Y) * v;
            double rx = tr.X + (br.X - tr.X) * v;
            double ry = tr.Y + (br.Y - tr.Y) * v;

            for (int dx = 0; dx < width; dx++)
            {
                double u = width > 1 ? dx / (double)(width - 1) : 0.0;
                double fx = lx + (rx - lx) * u;
                double fy = ly + (ry - ly) * u;

                int x0 = (int)Math.Floor(fx);
                int y0 = (int)Math.Floor(fy);
                double wx = fx - x0, wy = fy - y0;
                int x0c = Clamp(x0, 0, src.Width - 1);
                int x1c = Clamp(x0 + 1, 0, src.Width - 1);
                int y0c = Clamp(y0, 0, src.Height - 1);
                int y1c = Clamp(y0 + 1, 0, src.Height - 1);

                int i00 = y0c * src.Stride + x0c * 4;
                int i01 = y0c * src.Stride + x1c * 4;
                int i10 = y1c * src.Stride + x0c * 4;
                int i11 = y1c * src.Stride + x1c * 4;
                double w00 = (1 - wx) * (1 - wy), w01 = wx * (1 - wy), w10 = (1 - wx) * wy, w11 = wx * wy;

                double r = pix[i00 + 2] * w00 + pix[i01 + 2] * w01 + pix[i10 + 2] * w10 + pix[i11 + 2] * w11;
                double g = pix[i00 + 1] * w00 + pix[i01 + 1] * w01 + pix[i10 + 1] * w10 + pix[i11 + 1] * w11;
                double b = pix[i00 + 0] * w00 + pix[i01 + 0] * w01 + pix[i10 + 0] * w10 + pix[i11 + 0] * w11;

                int o = dy * width + dx;
                dst[0 * plane + o] = (float)(r / 255.0 - 0.5) / 0.5f;
                dst[1 * plane + o] = (float)(g / 255.0 - 0.5) / 0.5f;
                dst[2 * plane + o] = (float)(b / 255.0 - 0.5) / 0.5f;
            }
        }
    }

    /// <summary>
    /// Samples a representative foreground (text) and background color from a quad region using a simple
    /// luminance split: the darker cluster is assumed to be text on light backgrounds and vice-versa.
    /// Returns ARGB values suitable for overlay styling, or (null,null) if the region is degenerate.
    /// </summary>
    public static (uint? Foreground, uint? Background) SampleColors(in BgraImage src, in Quad quad)
    {
        var b = quad.Bounds;
        int x0 = Clamp((int)Math.Floor(b.X), 0, src.Width - 1);
        int y0 = Clamp((int)Math.Floor(b.Y), 0, src.Height - 1);
        int x1 = Clamp((int)Math.Ceiling(b.Right), 0, src.Width);
        int y1 = Clamp((int)Math.Ceiling(b.Bottom), 0, src.Height);
        if (x1 <= x0 || y1 <= y0) return (null, null);

        // Two-bucket accumulation split at the mean luminance (a fast, deterministic Otsu-lite).
        long lumSum = 0, count = 0;
        var pix = src.Pixels.Span;
        // First pass: mean luminance (subsampled for speed on large boxes).
        int stepX = Math.Max(1, (x1 - x0) / 32);
        int stepY = Math.Max(1, (y1 - y0) / 32);
        for (int y = y0; y < y1; y += stepY)
            for (int x = x0; x < x1; x += stepX)
            {
                int i = y * src.Stride + x * 4;
                lumSum += Luma(pix[i + 2], pix[i + 1], pix[i]);
                count++;
            }
        if (count == 0) return (null, null);
        long mean = lumSum / count;

        long darkR = 0, darkG = 0, darkB = 0, darkN = 0;
        long liteR = 0, liteG = 0, liteB = 0, liteN = 0;
        for (int y = y0; y < y1; y += stepY)
            for (int x = x0; x < x1; x += stepX)
            {
                int i = y * src.Stride + x * 4;
                int r = pix[i + 2], g = pix[i + 1], bl = pix[i];
                if (Luma(r, g, bl) < mean) { darkR += r; darkG += g; darkB += bl; darkN++; }
                else { liteR += r; liteG += g; liteB += bl; liteN++; }
            }
        if (darkN == 0 || liteN == 0) return (null, null);

        uint dark = Argb((int)(darkR / darkN), (int)(darkG / darkN), (int)(darkB / darkN));
        uint lite = Argb((int)(liteR / liteN), (int)(liteG / liteN), (int)(liteB / liteN));
        // Heuristic: the smaller (text stroke) cluster is foreground.
        if (darkN <= liteN) return (dark, lite);
        return (lite, dark);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Luma(int r, int g, int b) => (r * 77 + g * 150 + b * 29) >> 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Argb(int r, int g, int b) =>
        0xFF000000u | ((uint)(byte)r << 16) | ((uint)(byte)g << 8) | (uint)(byte)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>Rounds <paramref name="value"/> up to the next multiple of <paramref name="multiple"/>.</summary>
    public static int RoundUpToMultiple(int value, int multiple)
    {
        if (multiple <= 1) return value;
        int rem = value % multiple;
        return rem == 0 ? value : value + (multiple - rem);
    }
}
