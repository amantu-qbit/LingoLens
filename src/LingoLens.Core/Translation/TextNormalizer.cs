using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LingoLens.Core.Translation;

/// <summary>
/// Normalizes OCR'd source text so trivially-different captures (whitespace jitter, full/half-width
/// punctuation) hit the same cache/glossary entry, and produces stable ids for matching.
/// </summary>
public static class TextNormalizer
{
    /// <summary>Collapse whitespace, trim, normalize width/compatibility forms.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // NFKC folds full-width Latin/punctuation to half-width and unifies compatibility chars.
        string nfkc = text.Normalize(NormalizationForm.FormKC);

        var sb = new StringBuilder(nfkc.Length);
        bool prevWs = false;
        foreach (char c in nfkc)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevWs && sb.Length > 0) sb.Append(' ');
                prevWs = true;
            }
            else
            {
                sb.Append(c);
                prevWs = false;
            }
        }
        // trim a possible trailing space
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>A short, stable, URL-safe id for a normalized source string.</summary>
    public static string Hash(string normalized)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(normalized), hash);
        // 12 bytes -> 16 base64url chars: plenty for in-session uniqueness.
        return Convert.ToBase64String(hash[..12]).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>True if the text contains at least one CJK ideograph (worth translating zh→en).</summary>
    public static bool ContainsCjk(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            if ((v >= 0x4E00 && v <= 0x9FFF) ||   // CJK Unified
                (v >= 0x3400 && v <= 0x4DBF) ||   // Ext A
                (v >= 0x20000 && v <= 0x2A6DF) || // Ext B
                (v >= 0xF900 && v <= 0xFAFF))     // Compatibility
                return true;
        }
        return false;
    }

    /// <summary>Fraction of letters/ideographs that are CJK — a cheap source-language signal.</summary>
    public static double CjkRatio(string text)
    {
        int cjk = 0, letters = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                letters++;
                int v = rune.Value;
                if ((v >= 0x4E00 && v <= 0x9FFF) || (v >= 0x3400 && v <= 0x4DBF) ||
                    (v >= 0x20000 && v <= 0x2A6DF) || (v >= 0xF900 && v <= 0xFAFF))
                    cjk++;
            }
        }
        return letters == 0 ? 0 : (double)cjk / letters;
    }
}
