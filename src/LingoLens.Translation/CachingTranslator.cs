using Microsoft.Extensions.Logging;
using LingoLens.Core.Translation;

namespace LingoLens.Translation;

/// <summary>
/// An <see cref="ITranslator"/> decorator that resolves each item against the user glossary and the
/// translation cache before delegating cache misses to an inner translator in a single batch. This is
/// the translator the pipeline consumes: it guarantees deterministic ordering, marks only genuine
/// translation-memory hits via <see cref="TranslatedItem.FromCache"/> (glossary substitutions and
/// non-CJK pass-throughs are reported as <see langword="false"/> so cache metrics stay accurate), and
/// skips items with no CJK content. Empty/failed translations are never written back to the cache.
/// </summary>
public sealed class CachingTranslator : ITranslator
{
    private readonly ITranslator _inner;
    private readonly ITranslationCache _cache;
    private readonly IGlossary _glossary;
    private readonly bool _useGlossary;
    private readonly ILogger<CachingTranslator> _logger;

    public CachingTranslator(
        ITranslator inner,
        ITranslationCache cache,
        IGlossary glossary,
        bool useGlossary = true,
        ILogger<CachingTranslator>? logger = null)
    {
        _inner = inner;
        _cache = cache;
        _glossary = glossary;
        _useGlossary = useGlossary;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CachingTranslator>.Instance;
    }

    /// <inheritdoc />
    public string Name => $"cached({_inner.Name})";

    /// <inheritdoc />
    public bool IsReady => _inner.IsReady;

    /// <inheritdoc />
    public bool Supports(LanguagePair pair) => _inner.Supports(pair);

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        int count = request.Items.Count;
        if (count == 0) return TranslationResult.Empty;

        var pair = request.Languages;
        var results = new TranslatedItem[count];
        var normalized = new string[count];

        // Misses that must go to the inner translator, in first-seen order.
        var missItems = new List<TranslationItem>();
        // Map a normalized source -> indices that share it, so one translation fans out to all.
        var missIndicesByNorm = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            var item = request.Items[i];
            string norm = TextNormalizer.Normalize(item.Source);
            normalized[i] = norm;

            // 1) Nothing worth translating (e.g. pure punctuation/latin) — pass through.
            // This is not a translation-memory hit, so it must NOT inflate cache metrics.
            if (norm.Length == 0 || !TextNormalizer.ContainsCjk(norm))
            {
                results[i] = Hit(item, item.Source, fromCache: false);
                continue;
            }

            // 2) Glossary override (highest priority). A glossary substitution is not a cache hit either.
            if (_useGlossary && _glossary.TryResolve(pair, norm, out string glossed))
            {
                results[i] = Hit(item, glossed, fromCache: false);
                continue;
            }

            // 3) Translation memory — the only genuine cache hit.
            if (_cache.TryGet(pair, norm, out string cached))
            {
                results[i] = Hit(item, cached, fromCache: true);
                continue;
            }

            // 4) Miss — schedule for the inner translator, de-duplicating identical sources.
            if (missIndicesByNorm.TryGetValue(norm, out var indices))
            {
                indices.Add(i);
            }
            else
            {
                missIndicesByNorm[norm] = new List<int> { i };
                // Use the normalized source as the unit so the inner translator and cache agree.
                missItems.Add(new TranslationItem { Id = item.Id, Source = norm });
            }
        }

        if (missItems.Count > 0)
        {
            var innerResult = await _inner.TranslateAsync(
                request with { Items = missItems }, ct).ConfigureAwait(false);

            // Match inner results back to their normalized source (the unit we submitted).
            foreach (var translated in innerResult.Items)
            {
                string norm = TextNormalizer.Normalize(translated.Source);
                if (!missIndicesByNorm.TryGetValue(norm, out var indices)) continue;

                // Only persist genuine, confident translations. Empty/failed results (which the inner
                // translators emit as the verbatim source with Confidence 0) would otherwise poison the
                // translation memory permanently.
                if (!string.IsNullOrEmpty(translated.Target) && translated.Confidence > 0.0)
                {
                    _cache.Set(pair, norm, translated.Target);
                }

                foreach (int idx in indices)
                {
                    var original = request.Items[idx];
                    results[idx] = new TranslatedItem
                    {
                        Id = original.Id,
                        Source = original.Source,
                        Target = translated.Target,
                        FromCache = false,
                        Confidence = translated.Confidence,
                    };
                }
            }

            // Defensive: if the inner translator dropped an item, echo the source so the overlay
            // never shows an empty box.
            for (int i = 0; i < count; i++)
            {
                if (results[i] is null)
                {
                    var original = request.Items[i];
                    _logger.LogDebug("Inner translator omitted item {Id}; echoing source.", original.Id);
                    results[i] = Hit(original, original.Source, fromCache: false, confidence: 0.0);
                }
            }
        }

        return new TranslationResult(results);
    }

    private static TranslatedItem Hit(TranslationItem item, string target, bool fromCache, double confidence = 1.0) =>
        new()
        {
            Id = item.Id,
            Source = item.Source,
            Target = target,
            FromCache = fromCache,
            Confidence = confidence,
        };

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
