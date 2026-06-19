using LingoLens.Core.Capture;
using LingoLens.Core.Translation;

namespace LingoLens.Core.Pipeline;

public enum PipelineState { Stopped, Starting, Running, Paused, Error }

/// <summary>Per-stage timing for one processed frame (milliseconds).</summary>
public readonly record struct StageTimings(
    double CaptureMs,
    double GateMs,
    double OcrMs,
    double TranslateMs,
    double LayoutMs,
    double RenderMs)
{
    public double TotalMs => CaptureMs + GateMs + OcrMs + TranslateMs + LayoutMs + RenderMs;
}

/// <summary>Rolling pipeline health metrics for the HUD/diagnostics.</summary>
public sealed record PipelineMetrics
{
    public double CaptureFps { get; init; }
    public double EffectiveOcrFps { get; init; }
    public double LastEndToEndMs { get; init; }
    public double P50EndToEndMs { get; init; }
    public double P95EndToEndMs { get; init; }
    public double CacheHitRate { get; init; }
    public long FramesGated { get; init; }
    public long FramesProcessed { get; init; }
    public StageTimings LastTimings { get; init; }

    public static readonly PipelineMetrics Empty = new();
}

public sealed class PipelineStateChangedEventArgs(PipelineState state, string? message = null) : EventArgs
{
    public PipelineState State { get; } = state;
    public string? Message { get; } = message;
}

public sealed class PipelineMetricsEventArgs(PipelineMetrics metrics) : EventArgs
{
    public PipelineMetrics Metrics { get; } = metrics;
}

/// <summary>
/// Orchestrates the capture → gate → debounce → cache → OCR → translate → layout → render pipeline
/// across decoupled lanes. Implemented in LingoLens.Pipeline.
/// </summary>
public interface ITranslationPipeline : IAsyncDisposable
{
    PipelineState State { get; }
    PipelineMetrics Metrics { get; }
    LanguagePair Languages { get; set; }

    event EventHandler<PipelineStateChangedEventArgs>? StateChanged;
    event EventHandler<PipelineMetricsEventArgs>? MetricsUpdated;

    Task StartAsync(CaptureTarget target, CancellationToken ct = default);
    Task StopAsync();
    Task PauseAsync();
    Task ResumeAsync();
}
