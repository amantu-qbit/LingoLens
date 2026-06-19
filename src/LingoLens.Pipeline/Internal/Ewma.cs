namespace LingoLens.Pipeline.Internal;

/// <summary>
/// A small, thread-safe exponential moving average. Used to smooth noisy pipeline signals such as
/// the per-frame change rate and inference queue pressure so the adaptive cadence does not oscillate.
/// </summary>
/// <remarks>
/// The smoothing factor <c>alpha</c> is the weight given to each new sample; higher reacts faster,
/// lower is steadier. A value derived from a desired time constant can be computed via
/// <see cref="FromTimeConstant"/>.
/// </remarks>
internal sealed class Ewma
{
    private readonly double _alpha;
    private readonly object _gate = new();
    private double _value;
    private bool _seeded;

    /// <param name="alpha">Smoothing factor in (0,1]. Clamped into a safe range.</param>
    /// <param name="initial">Optional seed value; when supplied the average starts already seeded.</param>
    public Ewma(double alpha, double? initial = null)
    {
        _alpha = Math.Clamp(alpha, 0.0001, 1.0);
        if (initial is { } v)
        {
            _value = v;
            _seeded = true;
        }
    }

    /// <summary>The current smoothed value (0 until the first sample is observed).</summary>
    public double Value
    {
        get { lock (_gate) { return _value; } }
    }

    /// <summary>Whether at least one sample has been observed.</summary>
    public bool IsSeeded
    {
        get { lock (_gate) { return _seeded; } }
    }

    /// <summary>Fold a new sample into the average and return the updated smoothed value.</summary>
    public double Add(double sample)
    {
        lock (_gate)
        {
            if (!_seeded)
            {
                _value = sample;
                _seeded = true;
            }
            else
            {
                _value += _alpha * (sample - _value);
            }

            return _value;
        }
    }

    /// <summary>Reset to the unseeded state.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _value = 0;
            _seeded = false;
        }
    }

    /// <summary>
    /// Build an EWMA whose response approximates a first-order filter with the given time constant,
    /// assuming samples arrive roughly every <paramref name="sampleIntervalMs"/> milliseconds.
    /// </summary>
    public static Ewma FromTimeConstant(double timeConstantMs, double sampleIntervalMs, double? initial = null)
    {
        timeConstantMs = Math.Max(1.0, timeConstantMs);
        sampleIntervalMs = Math.Max(0.001, sampleIntervalMs);
        double alpha = 1.0 - Math.Exp(-sampleIntervalMs / timeConstantMs);
        return new Ewma(alpha, initial);
    }
}
