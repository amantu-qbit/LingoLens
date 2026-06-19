using LingoLens.Core.Overlay;
using Vortice.DirectWrite;

namespace LingoLens.Overlay.Layout;

/// <summary>The measured result of fitting a translated string into a source box footprint.</summary>
/// <param name="FontSize">The chosen font size in DIPs (already clamped to the min-scale floor).</param>
/// <param name="Width">Measured text width in DIPs (ink + trailing-whitespace aware).</param>
/// <param name="Height">Measured text height in DIPs.</param>
/// <param name="LineCount">Number of laid-out lines after word wrap.</param>
/// <param name="Overflowed">
/// True when even the smallest allowed font could not fit the text within the box footprint; the caller may
/// still draw (clipped) but can choose to widen the backplate or reduce padding.
/// </param>
public readonly record struct TextLayoutResult(
    float FontSize,
    float Width,
    float Height,
    int LineCount,
    bool Overflowed);

/// <summary>
/// Measures translated strings with DirectWrite and picks the largest font size that fits inside a source
/// box footprint with word wrap, down to <see cref="OverlayStyle.MinFontScale"/> of a base size derived from
/// the box height. Produces an <see cref="IDWriteTextLayout"/> ready for the renderer to draw, plus metrics.
/// </summary>
/// <remarks>
/// The engine owns a shared <see cref="IDWriteFactory"/> and a small cache of <see cref="IDWriteTextFormat"/>
/// instances keyed by (family, weight, size-bucket). It is safe to call from the overlay UI thread; it is not
/// designed for concurrent use across threads (DirectWrite layout objects are not thread-affine but the
/// internal cache is not synchronized — drive it from the render lane).
/// </remarks>
public sealed class TextLayoutEngine : IDisposable
{
    private const string DefaultLocale = "en-US";

    // Binary-search granularity for font sizing, in DIPs. Below this we stop refining.
    private const float SizeEpsilon = 0.5f;

    private readonly IDWriteFactory _factory;
    private readonly bool _ownsFactory;
    private readonly Dictionary<FormatKey, IDWriteTextFormat> _formatCache = new();
    private bool _disposed;

    /// <summary>Creates an engine that owns its own DirectWrite factory.</summary>
    public TextLayoutEngine()
    {
        _factory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _ownsFactory = true;
    }

    /// <summary>
    /// Creates an engine over an externally-owned DirectWrite factory (e.g. shared with the renderer).
    /// The factory is not disposed by this engine.
    /// </summary>
    public TextLayoutEngine(IDWriteFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _ownsFactory = false;
    }

    /// <summary>
    /// Builds a word-wrapped <see cref="IDWriteTextLayout"/> auto-fitted to the given box footprint.
    /// The returned layout is owned by the caller and must be disposed.
    /// </summary>
    /// <param name="text">The translated text to lay out.</param>
    /// <param name="boxWidth">Available width in DIPs (the source box footprint width).</param>
    /// <param name="boxHeight">Available height in DIPs (the source box footprint height).</param>
    /// <param name="fontFamily">Font family name (from <see cref="OverlayStyle.FontFamily"/>).</param>
    /// <param name="minFontScale">Lower bound as a fraction of the base size (from style).</param>
    /// <param name="result">Measured metrics for the chosen layout.</param>
    /// <returns>A laid-out, measured DirectWrite text layout. Never null; may be overflowed.</returns>
    public IDWriteTextLayout CreateFittedLayout(
        string text,
        float boxWidth,
        float boxHeight,
        string fontFamily,
        double minFontScale,
        out TextLayoutResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        text ??= string.Empty;

        // Guard against degenerate footprints.
        float maxW = MathF.Max(1f, boxWidth);
        float maxH = MathF.Max(1f, boxHeight);

        // Base size derived from box height: a single line of text typically occupies ~80% of its line box.
        float baseSize = MathF.Max(6f, maxH * 0.82f);
        float minSize = MathF.Max(6f, baseSize * (float)Math.Clamp(minFontScale, 0.05, 1.0));

        // Binary-search the largest size in [minSize, baseSize] that fits both width and height with wrap.
        float lo = minSize, hi = baseSize;
        float bestSize = minSize;
        bool anyFit = false;

        // First, a quick check at the max: if it already fits, we're done.
        if (Fits(text, fontFamily, baseSize, maxW, maxH))
        {
            bestSize = baseSize;
            anyFit = true;
        }
        else
        {
            // Standard bisection on a monotonic "fits" predicate (smaller size ⇒ more likely to fit).
            while (hi - lo > SizeEpsilon)
            {
                float mid = (lo + hi) * 0.5f;
                if (Fits(text, fontFamily, mid, maxW, maxH))
                {
                    bestSize = mid;
                    anyFit = true;
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            if (!anyFit) bestSize = minSize; // nothing fit; draw at the floor and report overflow
        }

        var layout = BuildLayout(text, fontFamily, bestSize, maxW, maxH);
        var metrics = layout.Metrics;
        bool overflowed = !anyFit || metrics.Height > maxH + 0.5f ||
                          metrics.WidthIncludingTrailingWhitespace > maxW + 0.5f;

        result = new TextLayoutResult(
            FontSize: bestSize,
            Width: metrics.WidthIncludingTrailingWhitespace,
            Height: metrics.Height,
            LineCount: (int)metrics.LineCount,
            Overflowed: overflowed);

        return layout;
    }

    /// <summary>
    /// Measures only (no retained layout). Useful for pipeline layout decisions that do not draw immediately.
    /// </summary>
    public TextLayoutResult Measure(
        string text, float boxWidth, float boxHeight, string fontFamily, double minFontScale)
    {
        using var layout = CreateFittedLayout(text, boxWidth, boxHeight, fontFamily, minFontScale, out var r);
        return r;
    }

    private bool Fits(string text, string fontFamily, float size, float maxW, float maxH)
    {
        using var layout = BuildLayout(text, fontFamily, size, maxW, maxH);
        var m = layout.Metrics;
        // Width is bounded by maxW because we wrap; the binding constraint is height (line count) and any
        // single unbreakable run wider than the box (DetermineMinWidth).
        return m.Height <= maxH + 0.5f && layout.DetermineMinWidth() <= maxW + 0.5f;
    }

    private IDWriteTextLayout BuildLayout(string text, string fontFamily, float size, float maxW, float maxH)
    {
        var format = GetFormat(fontFamily, size);
        var layout = _factory.CreateTextLayout(text, format, maxW, maxH);
        // Word wrap within the footprint; multi-line auto-fit relies on this.
        layout.WordWrapping = WordWrapping.WholeWord;
        layout.TextAlignment = TextAlignment.Leading;
        layout.ParagraphAlignment = ParagraphAlignment.Center; // vertically center within the box
        return layout;
    }

    private IDWriteTextFormat GetFormat(string fontFamily, float size)
    {
        // Bucket sizes to the nearest 0.25 DIP to keep the cache small while staying visually exact.
        float bucketed = MathF.Round(size * 4f) / 4f;
        var key = new FormatKey(fontFamily ?? "Segoe UI Variable", bucketed);
        if (_formatCache.TryGetValue(key, out var cached)) return cached;

        var format = _factory.CreateTextFormat(
            key.Family,
            FontWeight.SemiBold, // a touch heavier reads better on busy backgrounds
            FontStyle.Normal,
            FontStretch.Normal,
            key.Size);
        format.WordWrapping = WordWrapping.WholeWord;
        // Use the app locale so number/quote substitution behaves for English output.
        try { format.SetLineSpacing(LineSpacingMethod.Default, 0f, 0f); } catch { /* optional */ }

        _formatCache[key] = format;
        return format;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var format in _formatCache.Values)
            format.Dispose();
        _formatCache.Clear();

        if (_ownsFactory)
            _factory.Dispose();
    }

    private readonly record struct FormatKey(string Family, float Size);
}
