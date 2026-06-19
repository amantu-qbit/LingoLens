using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using LingoLens.Core.Models;

namespace LingoLens.Translation.Models;

/// <summary>
/// Resolves, downloads (with SHA-256 verification) and locates model files under
/// <c>%LOCALAPPDATA%\LingoLens\models\{bundleId}\{fileName}</c>. Downloads stream to a
/// temporary file, are verified, then atomically moved into place; already-present-and-valid assets
/// are skipped.
/// </summary>
public sealed class ModelRepository : IModelRepository
{
    private const int CopyBufferSize = 1 << 20; // 1 MiB

    private readonly HttpClient _http;
    private readonly ILogger<ModelRepository> _logger;
    private readonly Lazy<ModelManifest> _manifest;

    public ModelRepository(HttpClient? httpClient = null, ILogger<ModelRepository>? logger = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelRepository>.Instance;
        _manifest = new Lazy<ModelManifest>(DefaultModelManifest.Create, isThreadSafe: true);

        ModelsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LingoLens", "models");
    }

    /// <inheritdoc />
    public string ModelsRoot { get; }

    /// <inheritdoc />
    public Task<ModelManifest> GetManifestAsync(CancellationToken ct = default) =>
        Task.FromResult(_manifest.Value);

    /// <inheritdoc />
    public string GetAssetPath(string bundleId, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleId);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        // Guard against path traversal from a malformed manifest.
        string safeFile = Path.GetFileName(fileName);
        return Path.Combine(ModelsRoot, bundleId, safeFile);
    }

    /// <inheritdoc />
    public bool IsInstalled(string bundleId)
    {
        var bundle = _manifest.Value.Find(bundleId);
        if (bundle is null) return false;
        foreach (var asset in bundle.Assets)
        {
            if (!File.Exists(GetAssetPath(bundleId, asset.FileName)))
                return false;
        }
        return true;
    }

    /// <inheritdoc />
    public async Task EnsureInstalledAsync(
        string bundleId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var bundle = _manifest.Value.Find(bundleId)
            ?? throw new InvalidOperationException($"Unknown model bundle '{bundleId}'.");

        Directory.CreateDirectory(Path.Combine(ModelsRoot, bundleId));

        foreach (var asset in bundle.Assets)
        {
            ct.ThrowIfCancellationRequested();
            string destination = GetAssetPath(bundleId, asset.FileName);

            if (await IsValidAsync(destination, asset.Sha256, ct).ConfigureAwait(false))
            {
                _logger.LogDebug("Asset {Bundle}/{File} already present and valid; skipping.",
                    bundleId, asset.FileName);
                progress?.Report(new ModelDownloadProgress(
                    bundleId, asset.FileName, asset.SizeBytes, asset.SizeBytes));
                continue;
            }

            await DownloadAndVerifyAsync(bundleId, asset, destination, progress, ct).ConfigureAwait(false);
        }
    }

    private async Task DownloadAndVerifyAsync(
        string bundleId,
        ModelAsset asset,
        string destination,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Downloading {Bundle}/{File} from {Url}.",
            bundleId, asset.FileName, asset.Url);

        string tempPath = destination + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await _http
                .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? asset.SizeBytes;

            await using (var http = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, CopyBufferSize, useAsync: true))
            {
                await CopyWithProgressAsync(http, file, bundleId, asset.FileName, total, progress, ct)
                    .ConfigureAwait(false);
            }

            if (!HasSha256(asset.Sha256))
            {
                // The manifest does not pin a digest for this asset; integrity cannot be verified, so
                // skip the check (with a warning) rather than failing the download.
                _logger.LogWarning(
                    "Asset {Bundle}/{File} has no SHA-256 in the manifest; integrity NOT verified.",
                    bundleId, asset.FileName);
            }
            else
            {
                string actual = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"SHA-256 mismatch for {bundleId}/{asset.FileName}: expected {asset.Sha256}, got {actual}.");
                }
            }

            // Atomic publish: overwrite any stale file in a single move.
            File.Move(tempPath, destination, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        string bundleId,
        string fileName,
        long total,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                       .ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress?.Report(new ModelDownloadProgress(bundleId, fileName, received, total));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> IsValidAsync(string path, string expectedSha256, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        if (!HasSha256(expectedSha256))
            return true; // No digest pinned; cannot verify, so trust an existing file.
        string actual = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash); // upper-case hex
    }

    /// <summary>Whether the manifest pins a real SHA-256 digest for an asset (empty/placeholder == none).</summary>
    private static bool HasSha256(string sha256) =>
        !string.IsNullOrWhiteSpace(sha256) && !sha256.Equals("TODO", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of the temp file.
        }
    }
}
