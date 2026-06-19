using LingoLens.Core;
using LingoLens.Core.Overlay;

namespace LingoLens.Overlay.Smoothing;

/// <summary>
/// Temporal smoothing for the replace-in-place overlay. Matches incoming <see cref="OverlayItem"/>s to the
/// previous frame by <see cref="OverlayItem.Id"/> and:
/// <list type="bullet">
///   <item>exponentially lerps each box corner toward its new position (snapping instantly when the box jumps
///   far — likely a scroll — so text does not "slide" across the screen);</item>
///   <item>keeps an item briefly alive after it disappears, then fades it out over
///   <see cref="OverlayStyle.FadeMilliseconds"/>;</item>
///   <item>fades a freshly-appeared item in over the same window;</item>
///   <item>debounces text changes for the same id so single-frame OCR flicker does not cause the rendered
///   string to thrash.</item>
/// </list>
/// The output is deterministic for a given sequence of <see cref="OverlayFrame.TimestampTicks"/> values, which
/// makes it unit-testable without a clock. <see cref="Reset"/> clears all history.
/// </summary>
/// <remarks>
/// This type is pure logic (no GPU/COM). It is intended to run on the inference/layout lane just before the
/// frame is handed to <see cref="IOverlayRenderer.Present"/>; the renderer itself does no smoothing.
/// Instances are not thread-safe — drive a single instance from one lane, or guard externally.
/// </remarks>
public sealed class ExponentialOverlayStabilizer : IOverlayStabilizer
{
    /// <summary>Tunable knobs for the smoother. Defaults are chosen for 30 fps chat/UI content.</summary>
    public sealed record Settings
    {
        /// <summary>
        /// Per-frame corner blend factor in (0,1]. Higher = snappier (follows the target faster),
        /// lower = smoother but laggier. The effective factor is time-corrected so the look is stable
        /// across frame rates.
        /// </summary>
        public double PositionLerp { get; init; } = 0.5;

        /// <summary>
        /// If a matched box's largest corner displacement exceeds this fraction of the box diagonal,
        /// the move is treated as a scroll/reposition and the box snaps instantly instead of lerping.
        /// </summary>
        public double SnapDistanceFactor { get; init; } = 0.9;

        /// <summary>Absolute floor (in source pixels) for the snap threshold, for tiny boxes.</summary>
        public double SnapDistanceFloorPx { get; init; } = 24.0;

        /// <summary>
        /// How long (ms) a new text value for an id must persist before it replaces the currently shown
        /// text. Suppresses sub-frame OCR flicker. A brand-new id shows immediately.
        /// </summary>
        public double TextDebounceMilliseconds { get; init; } = 90.0;

        /// <summary>
        /// Grace period (ms) to keep showing an item after it stops appearing, before the fade-out starts.
        /// Absorbs single dropped frames without blinking.
        /// </summary>
        public double DisappearGraceMilliseconds { get; init; } = 60.0;

        /// <summary>Reference frame interval (ms) the <see cref="PositionLerp"/> factor is calibrated for.</summary>
        public double ReferenceFrameMilliseconds { get; init; } = 33.0;
    }

    private const double TicksPerMillisecond = TimeSpan.TicksPerMillisecond;

    private sealed class Track
    {
        public Quad DisplayBox;          // currently rendered (smoothed) geometry
        public Quad TargetBox;           // latest incoming geometry
        public string DisplayText = "";  // currently rendered text
        public string? OriginalText;
        public string? PendingText;      // candidate replacement awaiting debounce
        public long PendingSinceTicks;
        public uint? ForegroundArgb;
        public uint? BackgroundArgb;

        public long LastSeenTicks;       // last frame this id was present in the incoming set
        public long FirstSeenTicks;      // when the id first appeared (for fade-in)
        public bool Alive;               // present in the most recent incoming frame
        public double Opacity;           // last rendered opacity
        public double IncomingOpacity;   // target opacity from the incoming item this frame (when alive)
        public double OpacityAtDisappear;// opacity captured the frame the id stopped appearing
        public bool WasAliveLastFrame;   // for detecting the alive→gone transition
    }

    private readonly Settings _settings;
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);
    private long _lastTimestampTicks;
    private RectI _lastBounds = RectI.Empty;

    /// <summary>Creates a stabilizer with default <see cref="Settings"/>.</summary>
    public ExponentialOverlayStabilizer() : this(new Settings()) { }

    /// <summary>Creates a stabilizer with the supplied tuning.</summary>
    public ExponentialOverlayStabilizer(Settings settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <inheritdoc />
    public OverlayFrame Stabilize(OverlayFrame incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        long now;
        if (incoming.TimestampTicks != 0)
        {
            now = incoming.TimestampTicks;
        }
        else
        {
            // No clock supplied: synthesize one frame's worth of elapsed time so the per-frame delta is a
            // real reference interval. Advancing by a single tick would make AdvanceBox see dt≈0, collapse
            // alpha to ~0, and freeze all smoothing.
            long frameTicks = Math.Max(1L, (long)(_settings.ReferenceFrameMilliseconds * TicksPerMillisecond));
            now = _lastTimestampTicks + frameTicks;
        }
        // Guard against non-monotonic timestamps so deltas never go negative.
        if (now < _lastTimestampTicks) now = _lastTimestampTicks;

        double fadeMs = Math.Max(1.0, _lastFadeMs);

        // A change of coordinate space invalidates all geometry history (positions are space-relative).
        if (!incoming.SourceBounds.Equals(_lastBounds))
        {
            _tracks.Clear();
            _lastBounds = incoming.SourceBounds;
        }

        // 1) Fold the incoming items into the track table.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in incoming.Items)
        {
            if (string.IsNullOrEmpty(item.Id)) continue;
            seen.Add(item.Id);

            if (!_tracks.TryGetValue(item.Id, out var track))
            {
                track = new Track
                {
                    DisplayBox = item.SourceBox,
                    TargetBox = item.SourceBox,
                    DisplayText = item.Text,
                    OriginalText = item.OriginalText,
                    ForegroundArgb = item.ForegroundArgb,
                    BackgroundArgb = item.BackgroundArgb,
                    FirstSeenTicks = now,
                    LastSeenTicks = now,
                    Alive = true,
                    Opacity = 0.0, // fade in from transparent
                    IncomingOpacity = Math.Clamp(item.Opacity, 0.0, 1.0),
                };
                _tracks[item.Id] = track;
                continue;
            }

            // Existing id: update geometry target and run text-change debounce.
            track.TargetBox = item.SourceBox;
            track.OriginalText = item.OriginalText;
            track.ForegroundArgb = item.ForegroundArgb;
            track.BackgroundArgb = item.BackgroundArgb;
            track.LastSeenTicks = now;
            track.Alive = true;
            track.IncomingOpacity = Math.Clamp(item.Opacity, 0.0, 1.0);

            if (!string.Equals(item.Text, track.DisplayText, StringComparison.Ordinal))
            {
                if (!string.Equals(item.Text, track.PendingText, StringComparison.Ordinal))
                {
                    // New candidate — start its debounce window.
                    track.PendingText = item.Text;
                    track.PendingSinceTicks = now;
                }
                else if (TicksToMs(now - track.PendingSinceTicks) >= _settings.TextDebounceMilliseconds)
                {
                    // Candidate has been stable long enough — commit it.
                    track.DisplayText = item.Text;
                    track.PendingText = null;
                }
            }
            else
            {
                // Incoming text matches what we already show — drop any stale pending candidate.
                track.PendingText = null;
            }
        }

        // 2) Advance every track (alive or fading) and emit the ones still visible.
        var items = new List<OverlayItem>(_tracks.Count);
        var expired = new List<string>();

        foreach (var (id, track) in _tracks)
        {
            bool stillIncoming = seen.Contains(id);
            // Capture the opacity at the exact alive→gone transition so the fade-out starts from
            // wherever the (possibly still-fading-in) item currently is.
            if (track.WasAliveLastFrame && !stillIncoming)
                track.OpacityAtDisappear = track.Opacity;
            track.WasAliveLastFrame = stillIncoming;
            track.Alive = stillIncoming;

            // Smooth geometry toward the target (alive tracks only; fading tracks freeze in place).
            if (stillIncoming)
            {
                track.DisplayBox = AdvanceBox(track.DisplayBox, track.TargetBox, now);
            }

            // Opacity ramp: fade in toward incoming.Opacity, fade out after the grace period elapses.
            // The incoming opacity was captured onto the track during the fold loop above, so there is no
            // need to rescan incoming.Items here (which would make this an O(N^2) pass).
            double computedOpacity;

            if (stillIncoming)
            {
                double targetOpacity = track.IncomingOpacity;
                double age = TicksToMs(now - track.FirstSeenTicks);
                double fadeIn = fadeMs <= 0 ? 1.0 : Math.Clamp(age / fadeMs, 0.0, 1.0);
                computedOpacity = targetOpacity * fadeIn;
            }
            else
            {
                double sinceGone = TicksToMs(now - track.LastSeenTicks);
                double afterGrace = sinceGone - _settings.DisappearGraceMilliseconds;
                if (afterGrace <= 0)
                {
                    computedOpacity = track.Opacity; // hold during the grace period
                }
                else
                {
                    // Ramp from the opacity captured at the moment it disappeared down to zero.
                    double fadeFraction = fadeMs <= 0 ? 0.0 : Math.Clamp(1.0 - afterGrace / fadeMs, 0.0, 1.0);
                    computedOpacity = track.OpacityAtDisappear * fadeFraction;
                }
            }

            track.Opacity = computedOpacity;

            if (computedOpacity <= 0.001 && !stillIncoming)
            {
                expired.Add(id);
                continue;
            }

            items.Add(new OverlayItem
            {
                Id = id,
                SourceBox = track.DisplayBox,
                Text = track.DisplayText,
                OriginalText = track.OriginalText,
                Opacity = Math.Clamp(computedOpacity, 0.0, 1.0),
                ForegroundArgb = track.ForegroundArgb,
                BackgroundArgb = track.BackgroundArgb,
            });
        }

        foreach (var id in expired) _tracks.Remove(id);

        _lastTimestampTicks = now;

        return new OverlayFrame
        {
            Items = items,
            SourceBounds = incoming.SourceBounds,
            TimestampTicks = now,
        };
    }

    /// <inheritdoc />
    public void Reset()
    {
        _tracks.Clear();
        _lastTimestampTicks = 0;
        _lastBounds = RectI.Empty;
    }

    // The fade duration is owned by OverlayStyle on the renderer, but the stabilizer needs it for ramps.
    // It is provided out-of-band so the pure logic stays independent of the renderer. Defaults to 120 ms.
    private double _lastFadeMs = 120.0;

    /// <summary>
    /// Sets the fade duration used for opacity ramps (mirrors <see cref="OverlayStyle.FadeMilliseconds"/>).
    /// The renderer keeps this in sync when its style changes.
    /// </summary>
    public void SetFadeMilliseconds(double fadeMs) => _lastFadeMs = Math.Max(0.0, fadeMs);

    private Quad AdvanceBox(Quad current, Quad target, long now)
    {
        // Time-correct the lerp factor so motion looks consistent regardless of frame interval.
        double dtMs = TicksToMs(now - _lastTimestampTicks);
        if (dtMs <= 0) dtMs = _settings.ReferenceFrameMilliseconds;
        double baseLerp = Math.Clamp(_settings.PositionLerp, 0.0001, 1.0);
        // Convert the per-reference-frame factor into a frame-rate-independent smoothing coefficient.
        double frames = dtMs / Math.Max(1.0, _settings.ReferenceFrameMilliseconds);
        double alpha = 1.0 - Math.Pow(1.0 - baseLerp, frames);

        double maxMove = MaxCornerDelta(current, target);
        double diag = Diagonal(target);
        double snapThreshold = Math.Max(_settings.SnapDistanceFloorPx, diag * _settings.SnapDistanceFactor);
        if (maxMove >= snapThreshold)
            return target; // big jump ⇒ scroll/reposition ⇒ snap, do not slide across the screen

        return new Quad(
            Lerp(current.TopLeft, target.TopLeft, alpha),
            Lerp(current.TopRight, target.TopRight, alpha),
            Lerp(current.BottomRight, target.BottomRight, alpha),
            Lerp(current.BottomLeft, target.BottomLeft, alpha));
    }

    private static double MaxCornerDelta(Quad a, Quad b) => Math.Max(
        Math.Max(Dist(a.TopLeft, b.TopLeft), Dist(a.TopRight, b.TopRight)),
        Math.Max(Dist(a.BottomRight, b.BottomRight), Dist(a.BottomLeft, b.BottomLeft)));

    private static double Diagonal(Quad q) => Dist(q.TopLeft, q.BottomRight);

    private static double Dist(Point2 a, Point2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Point2 Lerp(Point2 a, Point2 b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static double TicksToMs(long ticks) => ticks <= 0 ? 0.0 : ticks / TicksPerMillisecond;
}
