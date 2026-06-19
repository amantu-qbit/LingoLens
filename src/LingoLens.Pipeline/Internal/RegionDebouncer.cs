using System.Diagnostics;
using LingoLens.Core;

namespace LingoLens.Pipeline.Internal;

/// <summary>
/// Per-region settle gate. A region that is actively changing (e.g. text still being typed, a window
/// mid-resize, a fade animating) is held back until it has been quiet for a short debounce interval,
/// so we OCR a stable picture rather than a transient one. Keyed by a coarsely-quantized rectangle so
/// jittering boxes that differ by a pixel or two collapse to the same logical region.
/// </summary>
/// <remarks>
/// Settling is inherently cross-frame: a region reports a change on one frame (which resets its settle
/// clock) and is then absent from later frames' change sets; once it has been quiet for its debounce
/// window it "settles" and becomes eligible for OCR exactly once. Not internally synchronized:
/// instances are owned by the capture-thread gate logic, which is the only caller. The debounce
/// interval is chosen per region from its aspect (wide, short lines → chat, faster settle; larger
/// blocks → UI, steadier settle).
/// </remarks>
internal sealed class RegionDebouncer
{
    private const int QuantizePx = 8;
    private const int MaxTrackedRegions = 512;

    private readonly int _uiDebounceMs;
    private readonly int _chatDebounceMs;
    private readonly Dictionary<long, Entry> _entries = new();

    private struct Entry
    {
        public RectI Region;
        public long LastChangeTs;
        public long LastSeenTs;
        public int DebounceMs;
        public bool Committed;
    }

    public RegionDebouncer(int uiDebounceMs, int chatDebounceMs)
    {
        _uiDebounceMs = Math.Max(0, uiDebounceMs);
        _chatDebounceMs = Math.Max(0, chatDebounceMs);
    }

    /// <summary>
    /// Feed this frame's changed regions (resetting their settle clocks) and return any regions that
    /// have now settled — i.e. changed previously, then stayed quiet for their debounce window — and
    /// are therefore ready to commit to OCR. Each region settles at most once per change burst.
    /// </summary>
    /// <param name="changedRegions">Regions the change-gate flagged as changed this frame.</param>
    /// <param name="nowTs">Current Stopwatch timestamp.</param>
    public IReadOnlyList<RectI> Settle(IReadOnlyList<RectI> changedRegions, long nowTs)
    {
        // 1) Register/refresh every region that changed this frame; its settle clock resets.
        for (int i = 0; i < changedRegions.Count; i++)
        {
            RectI region = changedRegions[i];
            if (region.IsEmpty) continue;

            long key = Quantize(region);
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.Region = region;
                existing.LastChangeTs = nowTs;
                existing.LastSeenTs = nowTs;
                existing.Committed = false; // new change → re-arm
                _entries[key] = existing;
            }
            else
            {
                _entries[key] = new Entry
                {
                    Region = region,
                    LastChangeTs = nowTs,
                    LastSeenTs = nowTs,
                    DebounceMs = DebounceFor(region),
                    Committed = false,
                };
            }
        }

        // 2) Sweep all tracked regions; emit those that have gone quiet long enough and not yet committed.
        List<RectI>? settled = null;
        List<long>? stale = null;
        double evictAfterMs = Math.Max(_uiDebounceMs, _chatDebounceMs) * 10.0 + 1000.0;

        foreach (var kv in _entries)
        {
            Entry e = kv.Value;
            double quietMs = Stopwatch.GetElapsedTime(e.LastChangeTs, nowTs).TotalMilliseconds;

            if (!e.Committed && quietMs >= e.DebounceMs)
            {
                e.Committed = true;
                _entries[kv.Key] = e;
                (settled ??= new List<RectI>()).Add(e.Region);
            }

            if (e.Committed && Stopwatch.GetElapsedTime(e.LastSeenTs, nowTs).TotalMilliseconds > evictAfterMs)
                (stale ??= new List<long>()).Add(kv.Key);
        }

        if (stale is not null)
            foreach (long k in stale) _entries.Remove(k);

        if (_entries.Count > MaxTrackedRegions)
            EvictOldest(nowTs);

        return (IReadOnlyList<RectI>?)settled ?? Array.Empty<RectI>();
    }

    public void Reset() => _entries.Clear();

    private int DebounceFor(RectI region)
    {
        // Heuristic: wide, short boxes look like chat lines → faster settle; everything else
        // (menus, dialogs, paragraphs) → steadier settle.
        bool looksLikeChatLine = region.Height > 0 && region.Width >= region.Height * 4 && region.Height <= 64;
        return looksLikeChatLine ? _chatDebounceMs : _uiDebounceMs;
    }

    private static long Quantize(RectI r)
    {
        long qx = Clamp16(r.X / QuantizePx);
        long qy = Clamp16(r.Y / QuantizePx);
        long qw = Clamp16(r.Width / QuantizePx);
        long qh = Clamp16(r.Height / QuantizePx);
        return (qx << 48) | (qy << 32) | (qw << 16) | qh;
    }

    private static long Clamp16(int v) => (uint)Math.Clamp(v, 0, 0xFFFF);

    private void EvictOldest(long nowTs)
    {
        // Bound memory: drop the least-recently-seen entries down to ~75% capacity.
        int target = MaxTrackedRegions * 3 / 4;
        var ordered = new List<KeyValuePair<long, Entry>>(_entries);
        ordered.Sort((a, b) => a.Value.LastSeenTs.CompareTo(b.Value.LastSeenTs));
        int remove = _entries.Count - target;
        for (int i = 0; i < remove && i < ordered.Count; i++)
            _entries.Remove(ordered[i].Key);
    }
}
