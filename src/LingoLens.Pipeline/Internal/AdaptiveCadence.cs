namespace LingoLens.Pipeline.Internal;

/// <summary>
/// Computes an adaptive target capture FPS from observed content-change rate and inference queue
/// pressure, bounded by the monitor refresh estimate and the configured ceiling.
/// </summary>
/// <remarks>
/// Cadence buckets (per the design):
/// <list type="bullet">
///   <item>idle → event-driven floor (~2 fps polling for change),</item>
///   <item>light activity → 10–15 fps,</item>
///   <item>active → 30 fps,</item>
///   <item>burst → up to the refresh rate.</item>
/// </list>
/// Backpressure (a persistently full inference queue) widens the effective period rather than
/// dropping quality: as queue pressure rises toward 1.0 the target FPS is scaled down so the gate
/// throttles upstream instead of the channel silently dropping work.
/// </remarks>
internal sealed class AdaptiveCadence
{
    // Cadence anchors.
    private const double IdleFps = 2.0;
    private const double LightFps = 12.0;
    private const double ActiveFps = 30.0;

    // Change-rate thresholds (fraction of recent frames that reported a change).
    private const double LightChangeRate = 0.05;
    private const double ActiveChangeRate = 0.25;
    private const double BurstChangeRate = 0.60;

    private readonly Ewma _changeRate;
    private readonly Ewma _queuePressure;
    private readonly int _maxFpsCeiling;

    /// <summary>Estimated monitor refresh rate (Hz); the absolute upper bound for capture FPS.</summary>
    public double RefreshHz { get; set; } = 60.0;

    public AdaptiveCadence(int maxFpsCeiling)
    {
        _maxFpsCeiling = Math.Max(1, maxFpsCeiling);
        // ~600 ms / ~400 ms time constants assuming ~30 Hz sampling: steady but responsive to bursts.
        _changeRate = Ewma.FromTimeConstant(timeConstantMs: 600, sampleIntervalMs: 33, initial: 0);
        _queuePressure = Ewma.FromTimeConstant(timeConstantMs: 400, sampleIntervalMs: 33, initial: 0);
    }

    /// <summary>Smoothed fraction of recent frames that reported a change, in [0,1].</summary>
    public double ChangeRate => _changeRate.Value;

    /// <summary>Smoothed inference queue occupancy in [0,1] (depth / capacity).</summary>
    public double QueuePressure => _queuePressure.Value;

    /// <summary>Record whether the most recent frame reported a change (gate result).</summary>
    public void RecordFrame(bool changed) => _changeRate.Add(changed ? 1.0 : 0.0);

    /// <summary>Record current inference queue occupancy as a fraction of capacity in [0,1].</summary>
    public void RecordQueuePressure(double occupancyFraction) =>
        _queuePressure.Add(Math.Clamp(occupancyFraction, 0.0, 1.0));

    /// <summary>
    /// Compute the current target capture FPS = min(refresh, change-rate-derived, ceiling), then scale
    /// down under sustained queue pressure. Never returns below the idle floor so we keep polling for
    /// the next change.
    /// </summary>
    public double TargetFps()
    {
        double cr = _changeRate.Value;

        double desired;
        if (cr < LightChangeRate)
            desired = IdleFps;
        else if (cr < ActiveChangeRate)
            desired = Lerp(LightFps, ActiveFps, (cr - LightChangeRate) / (ActiveChangeRate - LightChangeRate));
        else if (cr < BurstChangeRate)
            desired = Lerp(ActiveFps, RefreshHz, (cr - ActiveChangeRate) / (BurstChangeRate - ActiveChangeRate));
        else
            desired = RefreshHz;

        // Backpressure: at full pressure, halve the rate; ease in only past the half-full mark.
        double pressure = _queuePressure.Value;
        double pressureScale = pressure <= 0.5 ? 1.0 : 1.0 - (pressure - 0.5); // 0.5→1.0 .. 1.0→0.5
        desired *= Math.Clamp(pressureScale, 0.5, 1.0);

        double ceiling = Math.Min(_maxFpsCeiling, Math.Max(1.0, RefreshHz));
        return Math.Clamp(desired, IdleFps, ceiling);
    }

    /// <summary>The current target inter-frame period in milliseconds (1000 / <see cref="TargetFps"/>).</summary>
    public double TargetPeriodMs() => 1000.0 / TargetFps();

    public void Reset()
    {
        _changeRate.Reset();
        _queuePressure.Reset();
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0.0, 1.0);
}
