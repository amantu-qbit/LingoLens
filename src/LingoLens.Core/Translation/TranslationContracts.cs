namespace LingoLens.Core.Translation;

/// <summary>A source→target language pair using BCP-47-ish short codes ("zh", "en", "ja", ...).</summary>
public readonly record struct LanguagePair(string Source, string Target)
{
    public static LanguagePair ZhToEn => new("zh", "en");
    public bool IsAutoSource => string.Equals(Source, "auto", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => $"{Source}->{Target}";
}

/// <summary>A unit of text to translate, carrying a stable id used to re-associate the result.</summary>
public sealed record TranslationItem
{
    /// <summary>Stable id (typically a hash of the normalized source) for matching results to boxes.</summary>
    public required string Id { get; init; }
    public required string Source { get; init; }
}

public sealed record TranslationRequest
{
    public required IReadOnlyList<TranslationItem> Items { get; init; }
    public LanguagePair Languages { get; init; } = LanguagePair.ZhToEn;

    /// <summary>Prior lines (most-recent last) to give the translator conversational context.</summary>
    public IReadOnlyList<string>? Context { get; init; }
}

public sealed record TranslatedItem
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool FromCache { get; init; }
    public double Confidence { get; init; } = 1.0;
}

public sealed record TranslationResult(IReadOnlyList<TranslatedItem> Items)
{
    public static readonly TranslationResult Empty = new(Array.Empty<TranslatedItem>());
}

/// <summary>A translation backend (e.g. Qwen3 LLM, Opus-MT NMT).</summary>
public interface ITranslator : IAsyncDisposable
{
    string Name { get; }
    bool IsReady { get; }

    /// <summary>
    /// Human-readable reason this translator is not ready (model missing, failed to load, …), or null
    /// when it is ready or the reason is unknown. Surfaced to the user so failures aren't silent.
    /// </summary>
    string? UnavailableReason => null;

    /// <summary>Whether this translator can handle the pair (some are zh→en only).</summary>
    bool Supports(LanguagePair pair);

    Task InitializeAsync(CancellationToken ct = default);

    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default);
}

/// <summary>Fast, thread-safe translation memory keyed by (pair, normalized-source).</summary>
public interface ITranslationCache
{
    bool TryGet(LanguagePair pair, string normalizedSource, out string translation);
    void Set(LanguagePair pair, string normalizedSource, string translation);
    void Clear();
    int Count { get; }
    double HitRate { get; }
}

public sealed record GlossaryEntry(LanguagePair Pair, string Source, string Target);

/// <summary>User-defined term overrides that take priority over machine translation.</summary>
public interface IGlossary
{
    bool TryResolve(LanguagePair pair, string source, out string translation);
    void AddOrUpdate(LanguagePair pair, string source, string target);
    void Remove(LanguagePair pair, string source);
    IReadOnlyCollection<GlossaryEntry> Entries { get; }
}
