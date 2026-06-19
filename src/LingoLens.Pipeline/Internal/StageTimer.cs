using System.Diagnostics;
using LingoLens.Core.Pipeline;

namespace LingoLens.Pipeline.Internal;

/// <summary>
/// Stopwatch-based accumulator for the per-stage timings of a single frame's journey through the
/// pipeline. Not thread-safe by design: one instance belongs to exactly one in-flight work item and
/// is mutated only by the worker processing it.
/// </summary>
internal sealed class StageTimer
{
    private double _captureMs;
    private double _gateMs;
    private double _ocrMs;
    private double _translateMs;
    private double _layoutMs;
    private double _renderMs;

    /// <summary>Seed the capture-stage cost (measured on the capture thread, before hand-off).</summary>
    public void SetCapture(double ms) => _captureMs = ms;

    /// <summary>Seed the gate-stage cost (measured on the capture thread, before hand-off).</summary>
    public void SetGate(double ms) => _gateMs = ms;

    /// <summary>
    /// Time the supplied stage, recording the elapsed milliseconds against the named stage.
    /// </summary>
    public async Task<T> MeasureAsync<T>(PipelineStage stage, Func<Task<T>> action)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Record(stage, Elapsed(start));
        }
    }

    /// <summary>Time a synchronous stage (e.g. layout/stabilize, present marshalling).</summary>
    public T Measure<T>(PipelineStage stage, Func<T> action)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            Record(stage, Elapsed(start));
        }
    }

    /// <summary>Time a synchronous, void stage.</summary>
    public void Measure(PipelineStage stage, Action action)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            action();
        }
        finally
        {
            Record(stage, Elapsed(start));
        }
    }

    private void Record(PipelineStage stage, double ms)
    {
        switch (stage)
        {
            case PipelineStage.Ocr: _ocrMs += ms; break;
            case PipelineStage.Translate: _translateMs += ms; break;
            case PipelineStage.Layout: _layoutMs += ms; break;
            case PipelineStage.Render: _renderMs += ms; break;
        }
    }

    private static double Elapsed(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    public StageTimings ToTimings() =>
        new(_captureMs, _gateMs, _ocrMs, _translateMs, _layoutMs, _renderMs);
}

/// <summary>The discrete inference/UI stages we time per frame.</summary>
internal enum PipelineStage
{
    Ocr,
    Translate,
    Layout,
    Render,
}
