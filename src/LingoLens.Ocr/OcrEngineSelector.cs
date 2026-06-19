using Microsoft.Extensions.Logging;
using LingoLens.Core;
using LingoLens.Core.Configuration;
using LingoLens.Core.Ocr;

namespace LingoLens.Ocr;

/// <summary>
/// The composite <see cref="IOcrEngine"/> the pipeline consumes. It initializes the candidate engines and
/// routes recognition to the best ready one: PP-OCRv5 for accuracy when its models are installed, otherwise
/// the zero-footprint Windows OCR fallback. Selection honours <see cref="OcrOptions.Engine"/>
/// ("ppocr-v5" | "windows" | "auto").
/// </summary>
public sealed class OcrEngineSelector : IOcrEngine
{
    private readonly PaddleOcrV5Engine _paddle;
    private readonly WindowsMediaOcrEngine _windows;
    private readonly OcrOptions _options;
    private readonly ILogger<OcrEngineSelector> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private IOcrEngine? _active;
    private bool _initialized;

    public OcrEngineSelector(
        PaddleOcrV5Engine paddle,
        WindowsMediaOcrEngine windows,
        LingoLensOptions options,
        ILogger<OcrEngineSelector>? logger = null)
    {
        _paddle = paddle;
        _windows = windows;
        _options = options.Ocr;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OcrEngineSelector>.Instance;
    }

    /// <inheritdoc />
    public string Name => _active?.Name ?? "ocr-selector";

    /// <inheritdoc />
    public bool IsReady => _active?.IsReady ?? false;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedScripts => _active?.SupportedScripts ?? Array.Empty<string>();

    /// <summary>The engine currently selected, or null before <see cref="InitializeAsync"/> resolves one.</summary>
    public IOcrEngine? Active => _active;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            _initialized = true;

            string pref = _options.Engine?.Trim().ToLowerInvariant() ?? "auto";

            // Initialize candidates according to preference (avoid loading heavy models if not wanted).
            if (pref is "ppocr-v5" or "ppocrv5" or "auto")
                await _paddle.InitializeAsync(ct).ConfigureAwait(false);
            if (pref is "windows" or "winocr" or "auto")
                await _windows.InitializeAsync(ct).ConfigureAwait(false);

            _active = Resolve(pref);
            if (_active is null)
                _logger.LogWarning("No OCR engine is ready (preference '{Pref}'). Recognition will return empty.", pref);
            else
                _logger.LogInformation("OCR engine selected: {Engine}.", _active.Name);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private IOcrEngine? Resolve(string pref)
    {
        switch (pref)
        {
            case "windows":
            case "winocr":
                return _windows.IsReady ? _windows : (_paddle.IsReady ? _paddle : null);
            case "ppocr-v5":
            case "ppocrv5":
                return _paddle.IsReady ? _paddle : (_windows.IsReady ? _windows : null);
            default: // auto: prefer accuracy, fall back to zero-footprint.
                if (_paddle.IsReady) return _paddle;
                if (_windows.IsReady) return _windows;
                return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DetectedText>> RecognizeAsync(OcrRequest request, CancellationToken ct = default)
    {
        if (!_initialized) await InitializeAsync(ct).ConfigureAwait(false);
        if (_active is null) return Array.Empty<DetectedText>();
        return await _active.RecognizeAsync(request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _paddle.DisposeAsync().ConfigureAwait(false);
        await _windows.DisposeAsync().ConfigureAwait(false);
        _initLock.Dispose();
    }
}
