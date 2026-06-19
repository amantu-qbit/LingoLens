using System.Collections.Concurrent;

namespace LingoLens.Core.Translation;

/// <summary>
/// Thread-safe in-memory glossary. User-defined term overrides take priority over machine
/// translation (e.g. proper nouns, game/app jargon). Matching is on the normalized source.
/// </summary>
public sealed class InMemoryGlossary : IGlossary
{
    private readonly ConcurrentDictionary<string, GlossaryEntry> _entries = new();

    public bool TryResolve(LanguagePair pair, string source, out string translation)
    {
        if (_entries.TryGetValue(Key(pair, source), out var e))
        {
            translation = e.Target;
            return true;
        }
        translation = string.Empty;
        return false;
    }

    public void AddOrUpdate(LanguagePair pair, string source, string target)
    {
        var norm = TextNormalizer.Normalize(source);
        _entries[Key(pair, norm)] = new GlossaryEntry(pair, norm, target);
    }

    public void Remove(LanguagePair pair, string source) =>
        _entries.TryRemove(Key(pair, TextNormalizer.Normalize(source)), out _);

    public IReadOnlyCollection<GlossaryEntry> Entries => _entries.Values.ToArray();

    private static string Key(LanguagePair pair, string source) =>
        string.Concat(pair.Source, "|", pair.Target, "|", TextNormalizer.Normalize(source));
}
