namespace LingoLens.Core.Translation;

/// <summary>
/// Thread-safe, bounded LRU translation memory. Recurring chat lines and menu strings resolve here
/// in microseconds, which dominates real usage. Keyed by (pair, normalized-source).
/// </summary>
public sealed class LruTranslationCache : ITranslationCache
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru = new();
    private long _hits;
    private long _misses;

    public LruTranslationCache(int capacity = 4096)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _map = new Dictionary<string, LinkedListNode<Entry>>(capacity);
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    public double HitRate
    {
        get
        {
            long h = Interlocked.Read(ref _hits), m = Interlocked.Read(ref _misses);
            long total = h + m;
            return total == 0 ? 0 : (double)h / total;
        }
    }

    public bool TryGet(LanguagePair pair, string normalizedSource, out string translation)
    {
        string key = Key(pair, normalizedSource);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                translation = node.Value.Value;
                Interlocked.Increment(ref _hits);
                return true;
            }
        }
        Interlocked.Increment(ref _misses);
        translation = string.Empty;
        return false;
    }

    public void Set(LanguagePair pair, string normalizedSource, string translation)
    {
        string key = Key(pair, normalizedSource);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = existing.Value with { Value = translation };
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, translation));
            _lru.AddFirst(node);
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }

    // Language codes (BCP-47) never contain '|', and the source text is the final segment, so the
    // composite key is unambiguous for distinct (pair, source).
    private static string Key(LanguagePair pair, string normalizedSource) =>
        string.Concat(pair.Source, "|", pair.Target, "|", normalizedSource);

    private record struct Entry(string Key, string Value);
}
