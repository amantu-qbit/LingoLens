using LingoLens.Core;
using LingoLens.Core.Capture;

namespace LingoLens.Pipeline.Internal;

/// <summary>
/// A unit of work handed from the capture/gate lane to the inference lane over the bounded channel.
/// The work item takes ownership of the captured <see cref="Frame"/>: the inference worker is
/// responsible for disposing it once OCR has consumed it. If the channel drops this item (drop-oldest
/// under backpressure) the drop handler must dispose the frame instead — ownership transfers exactly
/// once.
/// </summary>
internal sealed class InferenceWorkItem
{
    public required ICaptureFrame Frame { get; init; }

    /// <summary>Regions the gate/debouncer flagged as changed-and-settled (OCR is limited to these).</summary>
    public required IReadOnlyList<RectI> ChangedRegions { get; init; }

    /// <summary>Per-frame stage timer, pre-seeded with capture+gate costs measured on the capture thread.</summary>
    public required StageTimer Timer { get; init; }

    /// <summary>Capture timestamp (Stopwatch ticks) used to compute end-to-end latency at present time.</summary>
    public long CaptureTimestampTicks { get; init; }

    /// <summary>Frame dimensions, captured up front so they survive frame disposal.</summary>
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }

    /// <summary>Effective DPI of the captured surface.</summary>
    public double Dpi { get; init; }
}
