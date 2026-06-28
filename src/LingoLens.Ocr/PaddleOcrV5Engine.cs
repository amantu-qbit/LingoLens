using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using LingoLens.Core;
using LingoLens.Core.Compute;
using LingoLens.Core.Configuration;
using LingoLens.Core.Models;
using LingoLens.Core.Ocr;
using LingoLens.Ocr.Internal;

namespace LingoLens.Ocr;

/// <summary>
/// PP-OCRv5 two-stage OCR engine on ONNX Runtime: a DBNet text detector produces oriented text boxes,
/// and a CRNN recognizer + CTC decoder reads each box. The execution provider (DirectML / CUDA /
/// TensorRT / CPU) is chosen from the selected <see cref="ComputeDevice"/>. Sessions are created once and
/// reused; the engine is safe to call concurrently (inference is serialized internally per session as
/// required by ONNX Runtime session semantics).
/// </summary>
public sealed class PaddleOcrV5Engine : IOcrEngine
{
    // Detection input must be a multiple of 32 in both dimensions for DBNet's downsampling stride.
    private const int DetSizeMultiple = 32;
    // Recognition strip geometry (PP-OCRv5 rec): fixed height, dynamic width capped for batching.
    private const int RecHeight = 48;
    private const int RecMaxWidth = 320;
    private const int RecMinWidth = 16;

    private readonly IModelRepository _models;
    private readonly IComputeDeviceManager _devices;
    private readonly OcrOptions _options;
    private readonly ILogger<PaddleOcrV5Engine> _logger;
    private readonly SemaphoreSlim _detLock = new(1, 1);
    private readonly SemaphoreSlim _recLock = new(1, 1);

    private InferenceSession? _det;
    private InferenceSession? _rec;
    private InferenceSession? _cls; // optional angle classifier
    private CtcDecoder? _decoder;
    private string? _detInputName, _detOutputName;
    private string? _recInputName, _recOutputName;
    private bool _initialized;
    private int _disposed;

    public PaddleOcrV5Engine(
        IModelRepository models,
        IComputeDeviceManager devices,
        LingoLensOptions options,
        ILogger<PaddleOcrV5Engine>? logger = null)
    {
        _models = models;
        _devices = devices;
        _options = options.Ocr;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PaddleOcrV5Engine>.Instance;
    }

    /// <inheritdoc />
    public string Name => "ppocr-v5";

    /// <inheritdoc />
    public bool IsReady => _det is not null && _rec is not null && _decoder is not null;

    /// <summary>Reason the engine is not ready (model not installed, load failure, …), or null.</summary>
    public string? UnavailableReason { get; private set; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedScripts { get; } = new[] { "Hans", "Hant", "Latn" };

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        _initialized = true;

        if (!_models.IsInstalled(OcrModelBundles.PpOcrV5Mobile))
        {
            UnavailableReason =
                $"Model bundle '{OcrModelBundles.PpOcrV5Mobile}' is not installed. " +
                "Download it from Settings to enable high-accuracy OCR.";
            _logger.LogInformation("{Reason}", UnavailableReason);
            return;
        }

        try
        {
            string detPath = _models.GetAssetPath(OcrModelBundles.PpOcrV5Mobile, OcrModelBundles.PpOcrV5Assets.Detection);
            string recPath = _models.GetAssetPath(OcrModelBundles.PpOcrV5Mobile, OcrModelBundles.PpOcrV5Assets.Recognition);
            string dictPath = _models.GetAssetPath(OcrModelBundles.PpOcrV5Mobile, OcrModelBundles.PpOcrV5Assets.Dictionary);

            ComputeDevice device = _devices.Selected;
            _logger.LogInformation("Initializing PP-OCRv5 on {Device} ({Provider}).", device.Name, device.Provider);

            // Detection + recognition sessions on the same device. Run off the calling thread because
            // session construction (graph optimization / EP init) is CPU/GPU-heavy and synchronous.
            await Task.Run(() =>
            {
                _det = OnnxSessionFactory.Create(detPath, device, _logger);
                _rec = OnnxSessionFactory.Create(recPath, device, _logger);

                // Optional angle classifier.
                string clsPath = _models.GetAssetPath(OcrModelBundles.PpOcrV5Mobile, OcrModelBundles.PpOcrV5Assets.Classification);
                if (File.Exists(clsPath))
                {
                    try { _cls = OnnxSessionFactory.Create(clsPath, device, _logger); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Angle classifier failed to load; proceeding without it."); }
                }
            }, ct).ConfigureAwait(false);

            _decoder = CtcDecoder.FromDictionaryFile(dictPath);

            _detInputName = _det!.InputNames[0];
            _detOutputName = _det.OutputNames[0];
            _recInputName = _rec!.InputNames[0];
            _recOutputName = _rec.OutputNames[0];

            WarmUp();
            _logger.LogInformation("PP-OCRv5 ready ({Labels} labels).", _decoder.LabelCount);
        }
        catch (Exception ex)
        {
            UnavailableReason = $"PP-OCRv5 failed to initialize: {ex.Message}";
            _logger.LogError(ex, "PP-OCRv5 initialization failed.");
            await DisposeSessionsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Runs one tiny inference per session so the first real frame is not penalized by JIT/EP warmup.</summary>
    private void WarmUp()
    {
        try
        {
            // Detection: a 32x32 black tile.
            var detInput = new DenseTensor<float>(new[] { 1, 3, DetSizeMultiple, DetSizeMultiple });
            using (_det!.Run(new[] { NamedOnnxValue.CreateFromTensor(_detInputName!, detInput) })) { }

            // Recognition: a single blank strip.
            var recInput = new DenseTensor<float>(new[] { 1, 3, RecHeight, RecMinWidth });
            using (_rec!.Run(new[] { NamedOnnxValue.CreateFromTensor(_recInputName!, recInput) })) { }
        }
        catch (Exception ex)
        {
            // TODO(verify-on-hardware): a model with a fixed (non-dynamic) input shape will reject these
            // warmup tensors; in that case warmup is skipped harmlessly.
            _logger.LogDebug(ex, "OCR warm-up skipped (model likely uses a fixed input shape).");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DetectedText>> RecognizeAsync(OcrRequest request, CancellationToken ct = default)
    {
        if (!IsReady) return Array.Empty<DetectedText>();
        if (!request.Frame.TryGetCpuPixels(out var bgra, out int stride))
            return Array.Empty<DetectedText>();

        int frameW = request.Frame.Width;
        int frameH = request.Frame.Height;
        var image = new OcrImaging.BgraImage(bgra, frameW, frameH, stride);

        var rois = request.RegionsOfInterest is { Count: > 0 } r
            ? r
            : (IReadOnlyList<RectI>)new[] { new RectI(0, 0, frameW, frameH) };

        var output = new List<DetectedText>();
        foreach (var rawRoi in rois)
        {
            ct.ThrowIfCancellationRequested();
            var roi = OcrImaging.ClampRoi(rawRoi, frameW, frameH);
            if (roi.IsEmpty) continue;

            IReadOnlyList<DbNetPostProcessor.DetectionBox> boxes = await DetectAsync(image, roi, ct).ConfigureAwait(false);
            if (boxes.Count == 0) continue;

            await RecognizeBoxesAsync(image, boxes, output, ct).ConfigureAwait(false);
        }

        return output;
    }

    /// <summary>
    /// Runs DBNet over the ROI and returns boxes already mapped into frame-pixel coordinates.
    /// </summary>
    private async Task<IReadOnlyList<DbNetPostProcessor.DetectionBox>> DetectAsync(
        OcrImaging.BgraImage image, RectI roi, CancellationToken ct)
    {
        // Compute the detector input size: scale the long side down to MaxDetectionSide, pad to /32.
        int longSide = Math.Max(roi.Width, roi.Height);
        double scale = longSide > _options.MaxDetectionSide ? (double)_options.MaxDetectionSide / longSide : 1.0;
        int inW = OcrImaging.RoundUpToMultiple(Math.Max(DetSizeMultiple, (int)Math.Round(roi.Width * scale)), DetSizeMultiple);
        int inH = OcrImaging.RoundUpToMultiple(Math.Max(DetSizeMultiple, (int)Math.Round(roi.Height * scale)), DetSizeMultiple);

        var input = new DenseTensor<float>(new[] { 1, 3, inH, inW });
        OcrImaging.ResizeRoiToNchwRgb(image, roi, inW, inH, OcrImaging.DetMean, OcrImaging.DetStd, input.Buffer.Span);

        float[] probMap;
        int mapW, mapH;
        await _detLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A concurrent DisposeAsync may have nulled the session after IsReady was observed but before
            // we acquired the lock. Capture into a local and bail gracefully if it is gone.
            InferenceSession? det = _det;
            if (det is null) return Array.Empty<DbNetPostProcessor.DetectionBox>();

            using var results = det.Run(new[] { NamedOnnxValue.CreateFromTensor(_detInputName!, input) });
            var outTensor = results.First(v => v.Name == _detOutputName).AsTensor<float>();
            // DBNet output is [1,1,H,W]; flatten to the spatial probability map.
            var dims = outTensor.Dimensions;
            mapH = dims[^2];
            mapW = dims[^1];
            probMap = outTensor.ToArray();
        }
        finally
        {
            _detLock.Release();
        }

        var post = new DbNetPostProcessor(boxThreshold: (float)Math.Min(0.5, _options.MinConfidence));
        var raw = post.Extract(probMap, mapW, mapH);
        if (raw.Count == 0) return Array.Empty<DbNetPostProcessor.DetectionBox>();

        // Map probability-map coordinates → ROI coordinates → frame coordinates.
        // probMap is at the (inW x inH) input resolution (DBNet preserves aspect by our square-ish pad),
        // which itself maps to the ROI with a uniform x/y scale.
        double mapToInputX = inW / (double)mapW;
        double mapToInputY = inH / (double)mapH;
        double inputToRoiX = roi.Width / (double)inW;
        double inputToRoiY = roi.Height / (double)inH;

        var mapped = new List<DbNetPostProcessor.DetectionBox>(raw.Count);
        foreach (var b in raw)
        {
            Point2 M(Point2 p) => new(
                p.X * mapToInputX * inputToRoiX + roi.X,
                p.Y * mapToInputY * inputToRoiY + roi.Y);
            var q = new Quad(M(b.Quad.TopLeft), M(b.Quad.TopRight), M(b.Quad.BottomRight), M(b.Quad.BottomLeft));
            mapped.Add(new DbNetPostProcessor.DetectionBox(q, b.Score));
        }
        return mapped;
    }

    /// <summary>
    /// Crops each detected box to a recognition strip, runs the CRNN recognizer (one box per Run for
    /// dynamic-width robustness), CTC-decodes, and emits <see cref="DetectedText"/> above the confidence
    /// threshold. Boxes are processed under a lock to honour single-threaded session use.
    /// </summary>
    private async Task RecognizeBoxesAsync(
        OcrImaging.BgraImage image,
        IReadOnlyList<DbNetPostProcessor.DetectionBox> boxes,
        List<DetectedText> output,
        CancellationToken ct)
    {
        await _recLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A concurrent DisposeAsync may have nulled the session/decoder after IsReady was observed but
            // before we acquired the lock. Capture into locals and bail gracefully if either is gone.
            InferenceSession? rec = _rec;
            CtcDecoder? decoder = _decoder;
            if (rec is null || decoder is null) return;

            foreach (var box in boxes)
            {
                ct.ThrowIfCancellationRequested();

                // Strip width preserves the box aspect ratio at the fixed recognition height.
                double bw = Dist(box.Quad.TopLeft, box.Quad.TopRight);
                double bh = Dist(box.Quad.TopLeft, box.Quad.BottomLeft);
                if (bw < 1 || bh < 1) continue;
                int stripW = (int)Math.Round(bw / bh * RecHeight);
                stripW = Math.Clamp(stripW, RecMinWidth, RecMaxWidth);

                var input = new DenseTensor<float>(new[] { 1, 3, RecHeight, stripW });
                OcrImaging.WarpQuadToRecStrip(image, box.Quad, stripW, RecHeight, input.Buffer.Span);

                string text;
                double conf;
                using (var results = rec.Run(new[] { NamedOnnxValue.CreateFromTensor(_recInputName!, input) }))
                {
                    var outTensor = results.First(v => v.Name == _recOutputName).AsTensor<float>();
                    var dims = outTensor.Dimensions; // [1, T, C]
                    int t = dims[^2];
                    int c = dims[^1];
                    (text, conf) = decoder.Decode(outTensor.ToArray(), t, c);
                }

                if (string.IsNullOrWhiteSpace(text)) continue;
                if (conf < _options.MinConfidence)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("PP-OCRv5 dropped a line below the confidence gate: conf={Conf:F3} < {Min} text=\"{Text}\".",
                            conf, _options.MinConfidence, text);
                    continue;
                }

                var (fg, bgCol) = OcrImaging.SampleColors(image, box.Quad);
                output.Add(new DetectedText
                {
                    Text = text,
                    Box = box.Quad,
                    Confidence = conf,
                    Script = "Hans",
                    ForegroundArgb = fg,
                    BackgroundArgb = bgCol,
                });
            }
        }
        finally
        {
            _recLock.Release();
        }
    }

    private static double Dist(Point2 a, Point2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Idempotent: the engine can be disposed by both its wrapper and the DI container. A second pass
        // would await an already-disposed semaphore and throw ObjectDisposedException at shutdown.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await DisposeSessionsAsync().ConfigureAwait(false);
        _detLock.Dispose();
        _recLock.Dispose();
    }

    private async ValueTask DisposeSessionsAsync()
    {
        // Serialize disposal behind the inference locks so we never free a session mid-Run. Guarded so a
        // late call after the locks are disposed degrades to a no-op instead of throwing, releasing only
        // the locks actually acquired.
        bool detTaken = false, recTaken = false;
        try
        {
            await _detLock.WaitAsync().ConfigureAwait(false); detTaken = true;
            await _recLock.WaitAsync().ConfigureAwait(false); recTaken = true;
        }
        catch (ObjectDisposedException)
        {
            if (recTaken) _recLock.Release();
            if (detTaken) _detLock.Release();
            return;
        }
        try
        {
            _det?.Dispose(); _det = null;
            _rec?.Dispose(); _rec = null;
            _cls?.Dispose(); _cls = null;
            _decoder = null;
        }
        finally
        {
            _recLock.Release();
            _detLock.Release();
        }
    }
}
