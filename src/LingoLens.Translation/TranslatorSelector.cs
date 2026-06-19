using Microsoft.Extensions.Logging;
using LingoLens.Core.Compute;
using LingoLens.Core.Configuration;
using LingoLens.Core.Translation;

namespace LingoLens.Translation;

/// <summary>
/// Chooses the active inner translator from the configured engine preference, the recommended model
/// tier, and each backend's readiness, then exposes it wrapped in a <see cref="CachingTranslator"/>.
/// Selection policy:
/// <list type="bullet">
///   <item><description>Explicit engine "qwen3" / "opus-mt" is honoured when that backend is ready.</description></item>
///   <item><description>"auto": prefer Qwen3 on Quality/Balanced tiers when ready; otherwise Opus-MT.</description></item>
///   <item><description>If the preferred backend is not ready, fall back to the other ready backend.</description></item>
/// </list>
/// The result is always wrapped with caching + glossary so the pipeline gets a single
/// <see cref="ITranslator"/>.
/// </summary>
public sealed class TranslatorSelector : ITranslator
{
    private readonly OpusMtTranslator _opus;
    private readonly Qwen3Translator _qwen;
    private readonly ITranslationCache _cache;
    private readonly IGlossary _glossary;
    private readonly IComputeDeviceManager _devices;
    private readonly TranslationOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TranslatorSelector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITranslator? _active; // the wrapped (caching) translator currently in use
    private ITranslator? _activeInner;

    public TranslatorSelector(
        OpusMtTranslator opus,
        Qwen3Translator qwen,
        ITranslationCache cache,
        IGlossary glossary,
        IComputeDeviceManager devices,
        LingoLensOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        _opus = opus;
        _qwen = qwen;
        _cache = cache;
        _glossary = glossary;
        _devices = devices;
        _options = options.Translation;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<TranslatorSelector>();
    }

    /// <inheritdoc />
    public string Name => _active?.Name ?? "translator-selector";

    /// <inheritdoc />
    public bool IsReady => _active?.IsReady ?? false;

    /// <inheritdoc />
    public bool Supports(LanguagePair pair) => _qwen.Supports(pair) || _opus.Supports(pair);

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ResolveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-evaluates the available backends after the installed model set changes (e.g. the user just
    /// downloaded a model). Resets both backends and clears the cached selection so the next translate
    /// picks up — and lazily loads — whichever backend is now ready. Safe to call while the pipeline is
    /// running: each backend's reset waits for any in-flight inference to finish before tearing down.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _opus.ResetAsync(ct).ConfigureAwait(false);
            await _qwen.ResetAsync(ct).ConfigureAwait(false);
            _active = null;
            _activeInner = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        if (_active is null) await InitializeAsync(ct).ConfigureAwait(false);
        var active = _active;
        if (active is null || !active.IsReady) return TranslationResult.Empty;
        return await active.TranslateAsync(request, ct).ConfigureAwait(false);
    }

    private async Task ResolveAsync(CancellationToken ct)
    {
        if (_active is not null) return;

        ITranslator inner = await ChooseInnerAsync(ct).ConfigureAwait(false);
        _activeInner = inner;
        _active = new CachingTranslator(
            inner, _cache, _glossary, _options.UseGlossary,
            _loggerFactory.CreateLogger<CachingTranslator>());

        _logger.LogInformation(
            "Translator selected: {Inner} (engine='{Engine}', tier={Tier}, ready={Ready}).",
            inner.Name, _options.Engine, _devices.RecommendedTier, inner.IsReady);
    }

    private async Task<ITranslator> ChooseInnerAsync(CancellationToken ct)
    {
        string engine = (_options.Engine ?? "auto").Trim().ToLowerInvariant();
        ModelTier tier = _devices.RecommendedTier;

        // Initialize lazily, only the candidate(s) we might use, to avoid loading both LLM + NMT.
        switch (engine)
        {
            case "opus-mt":
                await _opus.InitializeAsync(ct).ConfigureAwait(false);
                if (_opus.IsReady) return _opus;
                await _qwen.InitializeAsync(ct).ConfigureAwait(false);
                return _qwen.IsReady ? _qwen : _opus;

            case "qwen3":
                await _qwen.InitializeAsync(ct).ConfigureAwait(false);
                if (_qwen.IsReady) return _qwen;
                await _opus.InitializeAsync(ct).ConfigureAwait(false);
                return _opus; // ready or not, OpusMt is the documented fallback

            default: // "auto"
                bool preferQwen = tier is ModelTier.Quality or ModelTier.Balanced;
                if (preferQwen)
                {
                    await _qwen.InitializeAsync(ct).ConfigureAwait(false);
                    if (_qwen.IsReady) return _qwen;
                }
                await _opus.InitializeAsync(ct).ConfigureAwait(false);
                if (_opus.IsReady) return _opus;

                // Last resort on Light tier or when Opus is unavailable: try Qwen if not already.
                if (!preferQwen)
                {
                    await _qwen.InitializeAsync(ct).ConfigureAwait(false);
                    if (_qwen.IsReady) return _qwen;
                }
                return _opus; // not ready, but a valid object the pipeline can probe via IsReady
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Dispose the concrete backends once (the caching wrapper delegates to its inner, so disposing
        // it would double-dispose the chosen backend; dispose the backends directly instead).
        await _opus.DisposeAsync().ConfigureAwait(false);
        await _qwen.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
        _active = null;
        _activeInner = null;
    }
}
