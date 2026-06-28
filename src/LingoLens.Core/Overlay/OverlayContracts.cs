namespace LingoLens.Core.Overlay;

/// <summary>How translated text is presented over the source.</summary>
public enum OverlayStyleKind
{
    /// <summary>Draw English over the original Chinese on a frosted backplate (default, most seamless).</summary>
    ReplaceInPlace,
    /// <summary>Show translations in a floating panel/tooltips; original stays visible.</summary>
    FloatingPanel,
    /// <summary>Only show when the user hovers a box or presses a hotkey.</summary>
    OnDemand,
}

/// <summary>Visual styling for the overlay, surfaced in Settings.</summary>
public sealed record OverlayStyle
{
    public OverlayStyleKind Kind { get; init; } = OverlayStyleKind.ReplaceInPlace;
    /// <summary>Backplate opacity in [0,1].</summary>
    public double BackplateOpacity { get; init; } = 0.72;
    /// <summary>Auto-sample backplate tint from underlying pixels for contrast.</summary>
    public bool AutoContrast { get; init; } = true;
    public double CornerRadius { get; init; } = 6;
    public double FadeMilliseconds { get; init; } = 120;
    public string FontFamily { get; init; } = "Segoe UI Variable";
    /// <summary>Allow shrinking English to fit the original footprint (down to this fraction).</summary>
    public double MinFontScale { get; init; } = 0.6;
}

/// <summary>A single piece of translated text positioned over its source box.</summary>
public sealed record OverlayItem
{
    /// <summary>Stable id (matches the translation/source) for smoothing across frames.</summary>
    public required string Id { get; init; }

    /// <summary>Where the original text sits, in frame pixel coordinates.</summary>
    public required Quad SourceBox { get; init; }

    /// <summary>The translated text to render.</summary>
    public required string Text { get; init; }

    public string? OriginalText { get; init; }

    public double Opacity { get; init; } = 1.0;

    public uint? ForegroundArgb { get; init; }
    public uint? BackgroundArgb { get; init; }
}

/// <summary>A complete set of overlay items for one rendered frame, in a known coordinate space.</summary>
public sealed record OverlayFrame
{
    public required IReadOnlyList<OverlayItem> Items { get; init; }

    /// <summary>The frame/source pixel-space bounds these items are expressed in.</summary>
    public required RectI SourceBounds { get; init; }

    /// <summary>
    /// The source-space regions that were re-examined (re-OCR'd) to produce this frame, if known. The
    /// stabilizer uses these to decide which previously-shown translations are now stale: a prior item
    /// whose box lies inside a re-examined region but is absent from <see cref="Items"/> has had its
    /// source change or disappear, so it is expired; items whose regions were NOT re-examined persist
    /// (their source is unchanged). Empty ⇒ unknown, in which case the stabilizer falls back to a purely
    /// time-based grace/fade.
    /// </summary>
    public IReadOnlyList<RectI> ChangedRegions { get; init; } = Array.Empty<RectI>();

    public long TimestampTicks { get; init; }

    public static readonly OverlayFrame Empty =
        new() { Items = Array.Empty<OverlayItem>(), SourceBounds = RectI.Empty };
}

/// <summary>
/// The transparent, click-through overlay surface. Implemented in LingoLens.Overlay
/// (DirectComposition + Direct2D/DirectWrite).
/// </summary>
public interface IOverlayRenderer : IAsyncDisposable
{
    bool IsVisible { get; }
    OverlayStyle Style { get; set; }

    void Show();
    void Hide();

    /// <summary>Position/size the overlay over a target rectangle on the virtual desktop, at a DPI.</summary>
    void SetTargetBounds(RectI screenBounds, double dpi);

    /// <summary>Render a new set of items (maps SourceBounds → the target bounds).</summary>
    void Present(OverlayFrame frame);

    /// <summary>Clear all drawn items (e.g. on stop or target change).</summary>
    void Clear();
}

/// <summary>
/// Temporal smoothing of detected boxes + strings across frames to prevent flicker. Pure logic,
/// implemented in LingoLens.Core for testability.
/// </summary>
public interface IOverlayStabilizer
{
    /// <summary>Blend the incoming frame with recent history; returns the frame to actually render.</summary>
    OverlayFrame Stabilize(OverlayFrame incoming);
    void Reset();
}
