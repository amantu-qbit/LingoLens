using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using LingoLens.Core;
using LingoLens.Core.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;

namespace LingoLens.Ocr;

/// <summary>
/// Zero-footprint OCR engine backed by the OS <see cref="Windows.Media.Ocr.OcrEngine"/>. Requires the
/// Chinese (Simplified or Traditional) OCR language pack to be installed; if neither is available the
/// engine reports <see cref="IsReady"/> = false with a descriptive <see cref="UnavailableReason"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    private static readonly string[] CandidateLanguages = { "zh-Hans", "zh-Hant" };

    private readonly ILogger<WindowsMediaOcrEngine> _logger;
    // Windows.Media.Ocr.OcrEngine rejects overlapping RecognizeAsync calls ("Another RecognizeAsync
    // operation is already running!"). The pipeline runs more than one inference worker, so serialize
    // recognition on this engine instance.
    private readonly SemaphoreSlim _recognizeGate = new(1, 1);
    private WinOcrEngine? _engine;
    private string? _languageTag;
    private bool _initialized;

    public WindowsMediaOcrEngine(ILogger<WindowsMediaOcrEngine>? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsMediaOcrEngine>.Instance;

    /// <inheritdoc />
    public string Name => "windows";

    /// <inheritdoc />
    public bool IsReady => _engine is not null;

    /// <summary>Human-readable reason the engine could not initialize, or null when ready.</summary>
    public string? UnavailableReason { get; private set; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedScripts { get; } = new[] { "Hans", "Hant", "Latn" };

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return Task.CompletedTask;
        _initialized = true;

        try
        {
            foreach (var tag in CandidateLanguages)
            {
                var lang = new Language(tag);
                if (!WinOcrEngine.IsLanguageSupported(lang)) continue;
                var engine = WinOcrEngine.TryCreateFromLanguage(lang);
                if (engine is not null)
                {
                    _engine = engine;
                    _languageTag = tag;
                    _logger.LogInformation("Windows.Media.Ocr ready for language {Lang}.", tag);
                    return Task.CompletedTask;
                }
            }

            UnavailableReason =
                "No Chinese (zh-Hans / zh-Hant) OCR language pack is installed. " +
                "Install it via Settings → Time & language → Language & region → add Chinese with the optional OCR feature.";
            _logger.LogWarning("{Reason}", UnavailableReason);
        }
        catch (Exception ex)
        {
            UnavailableReason = $"Windows OCR initialization failed: {ex.Message}";
            _logger.LogWarning(ex, "Windows.Media.Ocr initialization failed.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DetectedText>> RecognizeAsync(OcrRequest request, CancellationToken ct = default)
    {
        if (_engine is null) return Array.Empty<DetectedText>();
        if (!request.Frame.TryGetCpuPixels(out var bgra, out int stride))
        {
            _logger.LogWarning("Windows OCR: could not read CPU pixels from the captured frame; skipping OCR for this frame.");
            return Array.Empty<DetectedText>();
        }

        int frameW = request.Frame.Width;
        int frameH = request.Frame.Height;
        var rois = ResolveRois(request, frameW, frameH);
        var results = new List<DetectedText>();

        foreach (var roi in rois)
        {
            ct.ThrowIfCancellationRequested();
            var clamped = Internal.OcrImaging.ClampRoi(roi, frameW, frameH);
            if (clamped.IsEmpty) continue;

            using SoftwareBitmap bitmap = CropToSoftwareBitmap(bgra, stride, frameW, frameH, clamped);

            // Only one RecognizeAsync may run on the engine at a time.
            await _recognizeGate.WaitAsync(ct).ConfigureAwait(false);
            OcrResult ocr;
            try
            {
                ocr = await _engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            }
            finally
            {
                _recognizeGate.Release();
            }

            AppendLines(ocr, clamped, results);
        }

        _logger.LogDebug("Windows OCR: recognized {Lines} line(s) across {Rois} ROI(s) on a {W}x{H} frame.",
            results.Count, rois.Count, frameW, frameH);
        return results;
    }

    private static IReadOnlyList<RectI> ResolveRois(OcrRequest request, int w, int h)
    {
        if (request.RegionsOfInterest is { Count: > 0 } rois) return rois;
        return new[] { new RectI(0, 0, w, h) };
    }

    private void AppendLines(OcrResult ocr, RectI roiOrigin, List<DetectedText> results)
    {
        string script = _languageTag == "zh-Hant" ? "Hant" : "Hans";
        foreach (var line in ocr.Lines)
        {
            // Union the word bounding rects to form the line box; join words for the line text.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            bool any = false;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                if (r.Width <= 0 || r.Height <= 0) continue;
                any = true;
                minX = Math.Min(minX, r.X);
                minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.Width);
                maxY = Math.Max(maxY, r.Y + r.Height);
            }

            if (!any || string.IsNullOrWhiteSpace(line.Text)) continue;

            // Offset by the ROI origin to return frame-pixel coordinates.
            var box = new RectD(minX + roiOrigin.X, minY + roiOrigin.Y, maxX - minX, maxY - minY);
            results.Add(new DetectedText
            {
                // Windows OCR emits Chinese as space-separated single characters ("波 胆 欧 洲 盘"). Those
                // spaces wreck SentencePiece tokenization and produce garbage translations, so collapse any
                // space sitting next to a CJK character before handing the line downstream.
                Text = CollapseCjkSpaces(line.Text),
                Box = Quad.FromRect(box),
                // Windows OCR does not expose per-line confidence; report a neutral-high value so the
                // pipeline does not over-suppress, and let translation/cache de-dupe noise.
                Confidence = 0.9,
                Script = script,
            });
        }
    }

    /// <summary>Drop spaces that sit next to a CJK ideograph so "波 胆 欧 洲 盘" becomes "波胆欧洲盘".</summary>
    private static string CollapseCjkSpaces(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf(' ') < 0) return s;

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool dropSpace = c == ' '
                && ((i > 0 && IsCjkIdeograph(s[i - 1])) || (i + 1 < s.Length && IsCjkIdeograph(s[i + 1])));
            if (!dropSpace) sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsCjkIdeograph(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF) || (ch >= 0xF900 && ch <= 0xFAFF);

    /// <summary>
    /// Copies a clamped ROI out of the BGRA frame into a premultiplied BGRA8 <see cref="SoftwareBitmap"/>.
    /// </summary>
    private static SoftwareBitmap CropToSoftwareBitmap(
        ReadOnlyMemory<byte> bgra, int stride, int frameW, int frameH, RectI roi)
    {
        int w = roi.Width, h = roi.Height;
        // Tightly packed BGRA destination (stride = w*4).
        var dst = new byte[w * h * 4];
        var src = bgra.Span;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (roi.Y + y) * stride + roi.X * 4;
            int dstRow = y * w * 4;
            src.Slice(srcRow, w * 4).CopyTo(dst.AsSpan(dstRow, w * 4));
        }

        // Premultiply alpha so BitmapAlphaMode.Premultiplied is honoured. Captured frames are typically
        // opaque (alpha=255), in which case this is a no-op, but be correct for translucent captures.
        for (int i = 0; i < dst.Length; i += 4)
        {
            byte a = dst[i + 3];
            if (a == 255) continue;
            dst[i + 0] = (byte)(dst[i + 0] * a / 255);
            dst[i + 1] = (byte)(dst[i + 1] * a / 255);
            dst[i + 2] = (byte)(dst[i + 2] * a / 255);
        }

        return SoftwareBitmap.CreateCopyFromBuffer(
            dst.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Windows.Media.Ocr.OcrEngine is a WinRT projected object without IDisposable; drop the reference.
        _engine = null;
        _recognizeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
