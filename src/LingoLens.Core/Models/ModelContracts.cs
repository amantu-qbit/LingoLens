using LingoLens.Core.Compute;

namespace LingoLens.Core.Models;

/// <summary>A single downloadable model file with integrity metadata.</summary>
public sealed record ModelAsset
{
    public required string FileName { get; init; }
    public required string Url { get; init; }
    public required string Sha256 { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>
    /// Optional fallback download URLs (alternate hosts / revisions) tried in order if <see cref="Url"/>
    /// fails — a host returning 403/404 or a transient error no longer blocks the install. Any pinned
    /// <see cref="Sha256"/> still applies to whichever mirror serves the file.
    /// </summary>
    public IReadOnlyList<string>? Mirrors { get; init; }

    /// <summary>The primary URL followed by any mirrors, in attempt order.</summary>
    public IEnumerable<string> CandidateUrls()
    {
        yield return Url;
        if (Mirrors is null) yield break;
        foreach (string m in Mirrors)
            if (!string.IsNullOrWhiteSpace(m) && !string.Equals(m, Url, StringComparison.Ordinal))
                yield return m;
    }
}

/// <summary>A named set of files that make up one model (e.g. det+rec+cls+dict for PP-OCRv5).</summary>
public sealed record ModelBundle
{
    public required string Id { get; init; }       // e.g. "ppocrv5-mobile", "qwen3-4b-int4"
    public required string Kind { get; init; }     // "ocr" | "translation"
    public ModelTier Tier { get; init; }
    public string? License { get; init; }
    public required IReadOnlyList<ModelAsset> Assets { get; init; }
}

public sealed record ModelManifest(IReadOnlyList<ModelBundle> Bundles)
{
    public ModelBundle? Find(string id) => Bundles.FirstOrDefault(b => b.Id == id);
}

public readonly record struct ModelDownloadProgress(string BundleId, string FileName, long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? (double)BytesReceived / TotalBytes : 0;
}

/// <summary>
/// Resolves, downloads (with checksum verification), and locates model files. Implemented in
/// LingoLens.Translation/Ocr or a dedicated infrastructure assembly.
/// </summary>
public interface IModelRepository
{
    string ModelsRoot { get; }
    bool IsInstalled(string bundleId);
    string GetAssetPath(string bundleId, string fileName);
    Task<ModelManifest> GetManifestAsync(CancellationToken ct = default);
    Task EnsureInstalledAsync(string bundleId, IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default);
}
