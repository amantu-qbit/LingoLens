namespace LingoLens.Pipeline.Internal;

/// <summary>
/// A fixed-capacity rolling window of samples that can report arbitrary percentiles (P50/P95/...).
/// Maintains insertion order for eviction and a parallel sorted view for O(log n) percentile queries.
/// Thread-safe; intended for end-to-end latency tracking over the last <c>capacity</c> frames.
/// </summary>
internal sealed class RollingPercentile
{
    private readonly int _capacity;
    private readonly Queue<double> _order;
    private readonly List<double> _sorted;
    private readonly object _gate = new();

    public RollingPercentile(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _order = new Queue<double>(_capacity);
        _sorted = new List<double>(_capacity);
    }

    /// <summary>Number of samples currently in the window.</summary>
    public int Count
    {
        get { lock (_gate) { return _order.Count; } }
    }

    /// <summary>Add a sample, evicting the oldest if the window is full.</summary>
    public void Add(double sample)
    {
        lock (_gate)
        {
            if (_order.Count >= _capacity)
            {
                double evicted = _order.Dequeue();
                int idx = _sorted.BinarySearch(evicted);
                if (idx < 0) idx = ~idx; // tolerate float identity drift
                if (idx >= _sorted.Count) idx = _sorted.Count - 1;
                if (idx >= 0 && idx < _sorted.Count) _sorted.RemoveAt(idx);
            }

            _order.Enqueue(sample);
            int insert = _sorted.BinarySearch(sample);
            if (insert < 0) insert = ~insert;
            _sorted.Insert(insert, sample);
        }
    }

    /// <summary>
    /// Compute the given percentile (0..1) using nearest-rank interpolation. Returns 0 when empty.
    /// </summary>
    public double Percentile(double q)
    {
        q = Math.Clamp(q, 0.0, 1.0);
        lock (_gate)
        {
            int n = _sorted.Count;
            if (n == 0) return 0;
            if (n == 1) return _sorted[0];

            double rank = q * (n - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            if (lo == hi) return _sorted[lo];

            double frac = rank - lo;
            return _sorted[lo] + (_sorted[hi] - _sorted[lo]) * frac;
        }
    }

    public double P50 => Percentile(0.50);
    public double P95 => Percentile(0.95);

    public void Reset()
    {
        lock (_gate)
        {
            _order.Clear();
            _sorted.Clear();
        }
    }
}
