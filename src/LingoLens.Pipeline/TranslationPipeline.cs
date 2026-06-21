using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using LingoLens.Core;
using LingoLens.Core.Capture;
using LingoLens.Core.Compute;
using LingoLens.Core.Configuration;
using LingoLens.Core.Ocr;
using LingoLens.Core.Overlay;
using LingoLens.Core.Pipeline;
using LingoLens.Core.Translation;
using LingoLens.Pipeline.Internal;

namespace LingoLens.Pipeline;

/// <summary>
/// Default <see cref="ITranslationPipeline"/>. Orchestrates three decoupled lanes:
/// <list type="number">
///   <item><b>Capture/gate lane</b> — receives frames, runs the cheap change-gate and per-region
///   debounce, and enqueues settled work onto a bounded latest-wins channel (or disposes the frame).</item>
///   <item><b>Inference lane</b> — one or more workers drain the channel, run OCR then translation
///   (cache applied inside the injected translator), build and stabilize an <see cref="OverlayFrame"/>.</item>
///   <item><b>UI/render lane</b> — the final <see cref="IOverlayRenderer.Present"/> is marshalled onto
///   the UI thread via <see cref="IUiDispatcher"/> and is never blocked by inference.</item>
/// </list>
/// </summary>
public sealed class TranslationPipeline : ITranslationPipeline
{
    private const int LatencyWindowSamples = 120;
    private const double MetricsIntervalMs = 250; // ~4 Hz
    private const double MinFpsSampleDeltaMs = 1.0; // ignore sub-ms inter-frame deltas (see TryFpsSample)
    private const int MaxLinesPerFrame = 60; // safety cap on lines fed to the sequential NMT decoder per frame

    private readonly ICaptureSourceFactory _captureFactory;
    private readonly IChangeDetector _changeDetector;
    private readonly IOcrEngine _ocr;
    private readonly ITranslator _translator;
    private readonly IOverlayStabilizer _stabilizer;
    private readonly IOverlayRenderer _renderer;
    private readonly IComputeDeviceManager _devices;
    private readonly ITranslationCache _cache;
    private readonly IGlossary _glossary;
    private readonly LingoLensOptions _options;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<TranslationPipeline> _logger;

    // ---- Metrics state (guarded by _metricsGate) ----
    private readonly object _metricsGate = new();
    private readonly RollingPercentile _e2e = new(LatencyWindowSamples);
    private readonly Ewma _captureFps = Ewma.FromTimeConstant(1000, 200, 0);
    private readonly Ewma _ocrFps = Ewma.FromTimeConstant(1000, 200, 0);
    private long _framesGated;
    private long _framesProcessed;
    private long _lastCaptureTs;
    private long _lastProcessedTs;
    private StageTimings _lastTimings;
    private double _lastE2eMs;

    // ---- Adaptive cadence + context ----
    private readonly AdaptiveCadence _cadence;
    private readonly List<string> _contextLines = new();
    private readonly object _contextGate = new();

    // ---- Lifecycle state ----
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private PipelineState _state = PipelineState.Stopped;
    private LanguagePair _languages;
    private CaptureTarget? _target;

    private IScreenCaptureSource? _source;
    private RegionDebouncer? _debouncer;
    private Channel<InferenceWorkItem>? _channel;
    private Task[]? _workers;
    private Task? _metricsLoop;
    private CancellationTokenSource? _runCts;
    private volatile bool _paused;
    private int _disposed;

    public TranslationPipeline(
        ICaptureSourceFactory captureFactory,
        IChangeDetector changeDetector,
        IOcrEngine ocr,
        ITranslator translator,
        IOverlayStabilizer stabilizer,
        IOverlayRenderer renderer,
        IComputeDeviceManager devices,
        ITranslationCache cache,
        IGlossary glossary,
        LingoLensOptions options,
        ILogger<TranslationPipeline> logger,
        IUiDispatcher? uiDispatcher = null)
    {
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _changeDetector = changeDetector ?? throw new ArgumentNullException(nameof(changeDetector));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _stabilizer = stabilizer ?? throw new ArgumentNullException(nameof(stabilizer));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _glossary = glossary ?? throw new ArgumentNullException(nameof(glossary));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Capture the host's UI context now (composition root usually runs on the UI thread).
        _ui = uiDispatcher ?? new SynchronizationContextUiDispatcher();

        _languages = _options.Translation.Pair;
        _cadence = new AdaptiveCadence(_options.Capture.MaxFps);
    }

    /// <inheritdoc />
    public PipelineState State
    {
        get { lock (_stateGate) return _state; }
    }

    /// <inheritdoc />
    public PipelineMetrics Metrics => SnapshotMetrics();

    /// <inheritdoc />
    public LanguagePair Languages
    {
        get { lock (_stateGate) return _languages; }
        set
        {
            lock (_stateGate) _languages = value;
            // Conversation context is language-pair specific; drop it on a pair change.
            lock (_contextGate) _contextLines.Clear();
        }
    }

    /// <inheritdoc />
    public event EventHandler<PipelineStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<PipelineMetricsEventArgs>? MetricsUpdated;

    // ===================================================================================
    // Lifecycle
    // ===================================================================================

    /// <inheritdoc />
    public async Task StartAsync(CaptureTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is PipelineState.Running or PipelineState.Starting)
            {
                _logger.LogDebug("StartAsync ignored: pipeline already {State}.", State);
                return;
            }

            SetState(PipelineState.Starting);

            try
            {
                _target = target;
                _paused = false;
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                ResetMetricsAndState();

                // Ensure the heavy engines are ready before we start feeding frames.
                await EnsureEnginesReadyAsync(_runCts.Token).ConfigureAwait(false);

                // Bounded latest-wins channel: capture → inference. We implement drop-oldest manually
                // (see EnqueueLatestWins) rather than via BoundedChannelFullMode.DropOldest, because the
                // built-in mode silently discards the displaced item — which would leak its pooled GPU
                // frame. Manual eviction lets us dispose the dropped frame deterministically.
                //
                // FullMode is Wait (not DropOldest): the manual eviction guarantees room before TryWrite,
                // and TryWrite never blocks even in Wait mode (it returns false when full). Choosing Wait
                // ensures that if eviction ever fails to make room (e.g. Reader.Count unsupported), the
                // displaced frame is NOT silently dropped by the channel and leaked — TryWrite simply
                // returns false and the caller disposes the frame deterministically via its finally block.
                int depth = Math.Max(1, _options.Pipeline.InferenceQueueDepth);
                _channel = Channel.CreateBounded<InferenceWorkItem>(new BoundedChannelOptions(depth)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = true, // frames arrive serially on the capture thread
                    AllowSynchronousContinuations = false,
                });

                _debouncer = new RegionDebouncer(_options.Pipeline.UiDebounceMs, _options.Pipeline.ChatDebounceMs);
                _changeDetector.Reset();
                _stabilizer.Reset();
                _cadence.Reset();

                // Position + show the overlay over the target.
                ConfigureOverlayForTarget(target);
                PostToUi(_renderer.Show);

                // Start the inference workers + metrics loop.
                int workerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);
                _workers = new Task[workerCount];
                for (int i = 0; i < workerCount; i++)
                    _workers[i] = Task.Run(() => InferenceWorkerLoopAsync(_runCts.Token), CancellationToken.None);

                _metricsLoop = Task.Run(() => MetricsLoopAsync(_runCts.Token), CancellationToken.None);

                // Start capture last so frames only arrive once everything downstream is live.
                _source = _captureFactory.Create(target);
                _source.FrameArrived += OnFrameArrived;
                _source.Error += OnCaptureError;

                var settings = BuildCaptureSettings();
                await _source.StartAsync(target, settings, _runCts.Token).ConfigureAwait(false);

                SetState(PipelineState.Running);
                _logger.LogInformation(
                    "Pipeline started for {Mode} target '{Name}' ({Pair}) on device {Device}.",
                    target.Mode, target.DisplayName ?? "(unnamed)", Languages, _devices.Selected.Name);

                // Surface a degraded state so the HUD explains why nothing appears, instead of running
                // silently with no overlay. Include the specific reason when the translator reports one.
                if (!_translator.IsReady)
                {
                    string reason = _translator.UnavailableReason ?? "open Settings ▸ Models to download one";
                    SetState(PipelineState.Running, $"Can't translate — {reason}");
                }
                else if (!_ocr.IsReady)
                {
                    SetState(PipelineState.Running, "No OCR engine is ready — add the Chinese OCR language pack in Windows settings.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline failed to start.");
                SetState(PipelineState.Error, ex.Message);
                await TeardownAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is PipelineState.Stopped) return;
            await TeardownAsync().ConfigureAwait(false);
            SetState(PipelineState.Stopped);
            _logger.LogInformation("Pipeline stopped.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PauseAsync()
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != PipelineState.Running) return;
            _paused = true;

            if (_source is not null)
            {
                try { await _source.StopAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error pausing capture source."); }
            }

            // Drop the smoothed overlay state so a resume starts clean and any late in-flight present
            // (guarded in PresentStabilized) has nothing stale to re-show.
            _stabilizer.Reset();

            PostToUi(_renderer.Clear);
            SetState(PipelineState.Paused);
            _logger.LogInformation("Pipeline paused.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ResumeAsync()
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != PipelineState.Paused || _source is null || _target is null || _runCts is null)
                return;

            _paused = false;
            _changeDetector.Reset();
            _debouncer?.Reset();
            // Also reset the temporal smoother and cadence controller so resume does not start from stale
            // smoothed boxes or a stale change-rate/queue-pressure estimate.
            _stabilizer.Reset();
            _cadence.Reset();

            await _source.StartAsync(_target, BuildCaptureSettings(), _runCts.Token).ConfigureAwait(false);
            PostToUi(_renderer.Show);
            SetState(PipelineState.Running);
            _logger.LogInformation("Pipeline resumed.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // ===================================================================================
    // Capture / gate lane (runs on the capture thread)
    // ===================================================================================

    private void OnFrameArrived(object? sender, FrameArrivedEventArgs e)
    {
        ICaptureFrame frame = e.Frame;

        // Channel/teardown guards: if we cannot accept work, dispose immediately to return the frame.
        Channel<InferenceWorkItem>? channel = _channel;
        RegionDebouncer? debouncer = _debouncer;
        CancellationTokenSource? cts = _runCts;
        if (_paused || channel is null || debouncer is null || cts is null || cts.IsCancellationRequested)
        {
            frame.Dispose();
            return;
        }

        bool ownershipTransferred = false;
        try
        {
            // --- Capture cadence accounting ---
            long now = Stopwatch.GetTimestamp();
            RecordCaptureFrame(now);

            // --- Change gate (cheap) ---
            var timer = new StageTimer();
            long gateStart = Stopwatch.GetTimestamp();
            ChangeResult change = _changeDetector.Detect(frame);
            timer.SetGate(Stopwatch.GetElapsedTime(gateStart).TotalMilliseconds);

            // Estimate the capture cost as the age of the frame at gate time (best-effort).
            double captureMs = frame.TimestampTicks > 0
                ? Math.Max(0, Stopwatch.GetElapsedTime(frame.TimestampTicks, now).TotalMilliseconds)
                : 0;
            timer.SetCapture(captureMs);

            _cadence.RecordFrame(change.HasChanges);

            // --- Per-region debounce/settle ---
            // Feed the gate's changed regions (clamped to the frame) to the debouncer every frame —
            // including no-change frames, so quiet regions get a chance to settle — then act on the
            // regions that have settled this tick.
            IReadOnlyList<RectI> changedClamped = ClampRegions(change.ChangedRegions, frame.Width, frame.Height);
            IReadOnlyList<RectI> settled = debouncer.Settle(changedClamped, now);

            if (settled.Count == 0)
            {
                // Either nothing changed, or changed regions are still settling. Skip OCR; the overlay
                // keeps its last (smoothed) content.
                Interlocked.Increment(ref _framesGated);
                return;
            }

            // --- Hand off to the inference lane ---
            var work = new InferenceWorkItem
            {
                Frame = frame,
                ChangedRegions = settled,
                Timer = timer,
                CaptureTimestampTicks = frame.TimestampTicks != 0 ? frame.TimestampTicks : now,
                FrameWidth = frame.Width,
                FrameHeight = frame.Height,
                Dpi = frame.Dpi,
            };

            int depth = Math.Max(1, _options.Pipeline.InferenceQueueDepth);
            if (EnqueueLatestWins(channel, work, depth))
            {
                // Ownership of the frame now belongs to the worker.
                ownershipTransferred = true;
            }
            else
            {
                // Writer closed (teardown racing) — dispose ourselves via the finally block.
                Interlocked.Increment(ref _framesGated);
            }

            // Update queue-pressure EWMA for the cadence controller.
            UpdateQueuePressure();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in capture/gate handler; dropping frame.");
        }
        finally
        {
            if (!ownershipTransferred)
                frame.Dispose();
        }
    }

    /// <summary>Clamp the gate's changed regions to the frame bounds, dropping any that fall empty.</summary>
    private static IReadOnlyList<RectI> ClampRegions(IReadOnlyList<RectI> regions, int width, int height)
    {
        if (regions.Count == 0) return Array.Empty<RectI>();

        var frameRect = new RectI(0, 0, width, height);
        List<RectI>? clamped = null;
        for (int i = 0; i < regions.Count; i++)
        {
            RectI c = regions[i].ClampTo(frameRect);
            if (c.IsEmpty) continue;
            (clamped ??= new List<RectI>(regions.Count)).Add(c);
        }

        return (IReadOnlyList<RectI>?)clamped ?? Array.Empty<RectI>();
    }

    /// <summary>
    /// Enqueue a work item with leak-free latest-wins semantics: if the bounded channel is at capacity,
    /// evict and dispose the oldest item's frame first so its pooled GPU texture is returned, then write
    /// the new item. Returns false only if the channel writer is closed (teardown). The capture thread
    /// is the single writer, so the read-then-write here cannot race another writer; concurrent worker
    /// reads only help (they may drain before we do, in which case there is nothing to evict).
    /// </summary>
    private bool EnqueueLatestWins(Channel<InferenceWorkItem> channel, InferenceWorkItem work, int depth)
    {
        // Evict oldest items while the channel is full to keep the freshest frame.
        int safety = depth + 2;
        bool countSupported = true;
        while (safety-- > 0)
        {
            int count;
            try { count = channel.Reader.Count; }
            catch (NotSupportedException) { countSupported = false; break; }

            if (count < depth) break;

            if (channel.Reader.TryRead(out InferenceWorkItem? evicted))
                SafeDispose(evicted.Frame);
            else
                break; // a worker drained it; room should be available now
        }

        if (!countSupported)
        {
            // Reader.Count is unavailable on this channel implementation, so we cannot tell whether the
            // channel is full. Try the write first; if it fails (full), evict one oldest item to make
            // room and retry once. This keeps eviction leak-free without relying on Count.
            if (channel.Writer.TryWrite(work))
                return true;

            if (channel.Reader.TryRead(out InferenceWorkItem? evicted))
                SafeDispose(evicted.Frame);

            return channel.Writer.TryWrite(work);
        }

        return channel.Writer.TryWrite(work);
    }

    private void OnCaptureError(object? sender, CaptureErrorEventArgs e)
    {
        _logger.LogWarning("Capture error: {Kind} — {Message}", e.Kind, e.Message);
        switch (e.Kind)
        {
            case CaptureErrorKind.ProtectedContent:
                // Non-fatal: surface to the HUD, keep running (capturer will keep yielding black).
                SetState(PipelineState.Running, "Protected content — translation unavailable for this target.");
                break;
            case CaptureErrorKind.TargetClosed:
                _logger.LogInformation("Capture target closed; stopping pipeline.");
                // Dispatch teardown off the capture source's own callback thread. StopAsync stops/disposes
                // that same source under _lifecycleGate; invoking it inline could deadlock or re-enter the
                // source's teardown. Task.Run breaks the call chain onto a thread-pool thread.
                _ = Task.Run(StopAsync);
                break;
            case CaptureErrorKind.DeviceLost:
            case CaptureErrorKind.Unsupported:
            case CaptureErrorKind.Unknown:
            default:
                SetState(PipelineState.Error, e.Message);
                break;
        }
    }

    // ===================================================================================
    // Inference lane
    // ===================================================================================

    private async Task InferenceWorkerLoopAsync(CancellationToken ct)
    {
        Channel<InferenceWorkItem>? channel = _channel;
        if (channel is null) return;

        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out InferenceWorkItem? work))
                {
                    UpdateQueuePressure();
                    await ProcessWorkItemAsync(work, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inference worker crashed.");
            SetState(PipelineState.Error, ex.Message);
        }
        finally
        {
            // Drain any frames left in the channel so pooled textures are returned.
            while (channel.Reader.TryRead(out InferenceWorkItem? leftover))
                SafeDispose(leftover.Frame);
        }
    }

    private async Task ProcessWorkItemAsync(InferenceWorkItem work, CancellationToken ct)
    {
        StageTimer timer = work.Timer;
        var sourceBounds = new RectI(0, 0, work.FrameWidth, work.FrameHeight);
        try
        {
            // ---- OCR (frame consumed here) ----
            IReadOnlyList<DetectedText> rawDetections;
            try
            {
                rawDetections = await timer.MeasureAsync(PipelineStage.Ocr, () =>
                    _ocr.RecognizeAsync(new OcrRequest
                    {
                        Frame = work.Frame,
                        RegionsOfInterest = work.ChangedRegions,
                        ExpectedScript = _options.Ocr.Scripts.Count > 0 ? _options.Ocr.Scripts[0] : null,
                    }, ct)).ConfigureAwait(false);
            }
            finally
            {
                // OCR is the only stage that needs the frame; release it as early as possible so the
                // pooled GPU texture goes back to the capturer.
                SafeDispose(work.Frame);
            }

            IReadOnlyList<DetectedText> kept = FilterDetections(rawDetections);
            // For a CJK source, only translate lines that actually contain source-script characters. A
            // full-screen capture is dominated by UI chrome in the user's own language ("Search", "chats");
            // translating every line floods the sequential NMT decoder and is wrong for a zh→en model anyway.
            IReadOnlyList<DetectedText> translatable = FilterToSourceScript(kept, Languages);
            // Safety valve: bound how many lines one frame pushes through the sequential decoder so a busy
            // full-screen capture can't stall the overlay. Region mode stays well under this.
            IReadOnlyList<DetectedText> detections = CapCount(translatable, MaxLinesPerFrame);

            // Per-frame chain diagnostics: shows exactly where a "nothing appears" frame breaks down —
            // OCR found no text, nothing was in the source language, translation was empty, or no boxes built.
            _logger.LogInformation(
                "Frame OCR: {Raw} raw → {Kept} kept → {Translatable} translatable across {Regions} region(s); first: '{Sample}'.",
                rawDetections.Count, kept.Count, translatable.Count, work.ChangedRegions.Count,
                translatable.Count > 0 ? Short(translatable[0].Text)
                    : (rawDetections.Count > 0 ? Short(rawDetections[0].Text) : "(none)"));

            if (detections.Count == 0)
            {
                // Windows OCR is flaky frame-to-frame: it intermittently returns nothing for a region that
                // plainly has text. Presenting an empty frame here cleared the overlay, so translated text
                // flickered away after ~1 s. Instead keep the last overlay up and just record the frame —
                // real text on a later frame replaces it, and Pause/Stop still clears explicitly.
                CommitFrameMetrics(work, hadText: false);
                return;
            }

            // ---- Translate (cache + glossary applied) ----
            LanguagePair pair = Languages;
            long translateStart = Stopwatch.GetTimestamp();
            TranslationResult translation = await timer.MeasureAsync(PipelineStage.Translate, () =>
                TranslateAsync(detections, pair, ct)).ConfigureAwait(false);
            double translateMs = Stopwatch.GetElapsedTime(translateStart).TotalMilliseconds;

            // ---- Layout / build overlay frame + temporal smoothing ----
            OverlayFrame overlay = timer.Measure(PipelineStage.Layout, () =>
                BuildOverlayFrame(detections, translation, sourceBounds, work.CaptureTimestampTicks));

            if (_logger.IsEnabled(LogLevel.Information))
            {
                int nonEmpty = 0;
                TranslatedItem? firstShown = null;
                foreach (TranslatedItem t in translation.Items)
                {
                    if (string.IsNullOrEmpty(t.Target)) continue;
                    nonEmpty++;
                    firstShown ??= t;
                }

                _logger.LogInformation(
                    "Frame translate: {NonEmpty}/{Total} non-empty target(s) → {Boxes} overlay box(es) in {Ms:F0} ms. sample: '{Src}' → '{Tgt}'.",
                    nonEmpty, translation.Items.Count, overlay.Items.Count, translateMs,
                    firstShown is not null ? Short(firstShown.Source) : "(none)",
                    firstShown is not null ? Short(firstShown.Target) : "(none)");
            }

            PresentStabilized(overlay, timer);
            CommitFrameMetrics(work, hadText: true);
        }
        catch (OperationCanceledException)
        {
            SafeDispose(work.Frame); // no-op if already disposed
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inference work item failed; dropping.");
            SafeDispose(work.Frame);
        }
    }

    /// <summary>
    /// Drop low-confidence detections (we suppress rather than show wrong text) and empties.
    /// </summary>
    private IReadOnlyList<DetectedText> FilterDetections(IReadOnlyList<DetectedText> detections)
    {
        double min = _options.Ocr.MinConfidence;
        List<DetectedText>? kept = null;
        foreach (DetectedText d in detections)
        {
            if (d.Confidence < min) continue;
            if (string.IsNullOrWhiteSpace(d.Text)) continue;
            (kept ??= new List<DetectedText>(detections.Count)).Add(d);
        }

        return (IReadOnlyList<DetectedText>?)kept ?? Array.Empty<DetectedText>();
    }

    /// <summary>
    /// When the source language is CJK (zh / ja), keep only lines that contain at least one Han
    /// character. This skips the user's own-language UI chrome that a full-screen capture is full of,
    /// which both fixes throughput (the slow NMT decoder isn't flooded) and avoids mistranslating
    /// non-source text. For non-CJK / "auto" sources we cannot cheaply tell, so we keep every line.
    /// </summary>
    private static IReadOnlyList<DetectedText> FilterToSourceScript(IReadOnlyList<DetectedText> detections, LanguagePair pair)
    {
        if (detections.Count == 0 || !IsHanSource(pair.Source)) return detections;

        List<DetectedText>? kept = null;
        foreach (DetectedText d in detections)
        {
            if (!ContainsHan(d.Text)) continue;
            (kept ??= new List<DetectedText>(detections.Count)).Add(d);
        }

        return (IReadOnlyList<DetectedText>?)kept ?? Array.Empty<DetectedText>();
    }

    private static bool IsHanSource(string source) =>
        source.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    /// <summary>Keep at most <paramref name="max"/> items (cheap, allocation-free when already small).</summary>
    private static IReadOnlyList<DetectedText> CapCount(IReadOnlyList<DetectedText> items, int max)
    {
        if (items.Count <= max) return items;
        var capped = new List<DetectedText>(max);
        for (int i = 0; i < max; i++) capped.Add(items[i]);
        return capped;
    }

    /// <summary>True if the text contains a CJK ideograph (covers the vast majority of real zh text).</summary>
    private static bool ContainsHan(string s)
    {
        foreach (char ch in s)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF) return true; // CJK Unified Ideographs
            if (ch >= 0x3400 && ch <= 0x4DBF) return true; // CJK Extension A
            if (ch >= 0xF900 && ch <= 0xFAFF) return true; // CJK Compatibility Ideographs
        }

        return false;
    }

    /// <summary>
    /// Translate the recognized lines. The injected <see cref="ITranslator"/> may itself be a caching
    /// translator; either way we resolve glossary overrides up front, serve cache hits directly, and
    /// only send genuine misses downstream. Cache-hit metrics are read from <see cref="ITranslationCache.HitRate"/>.
    /// </summary>
    private async Task<TranslationResult> TranslateAsync(
        IReadOnlyList<DetectedText> detections, LanguagePair pair, CancellationToken ct)
    {
        var results = new List<TranslatedItem>(detections.Count);
        var misses = new List<TranslationItem>();
        var missNormalized = new Dictionary<string, string>(StringComparer.Ordinal);

        // The ordered set of source lines we end up showing this frame, in detection order. Glossary and
        // cache hits are known immediately; miss slots are reserved here and filled in once the translator
        // returns, so the conversational context reflects every line on screen (recurring cached/glossary
        // lines included) — not just the cache misses.
        var orderedSources = new List<string?>(detections.Count);

        foreach (DetectedText d in detections)
        {
            string normalized = TextNormalizer.Normalize(d.Text);
            if (normalized.Length == 0) continue;

            string id = TextNormalizer.Hash(normalized);

            // Glossary wins outright.
            if (_options.Translation.UseGlossary && _glossary.TryResolve(pair, normalized, out string glossed))
            {
                results.Add(new TranslatedItem { Id = id, Source = d.Text, Target = glossed, FromCache = true });
                orderedSources.Add(d.Text);
                continue;
            }

            // Translation-memory hit.
            if (_cache.TryGet(pair, normalized, out string cached))
            {
                results.Add(new TranslatedItem { Id = id, Source = d.Text, Target = cached, FromCache = true });
                orderedSources.Add(d.Text);
                continue;
            }

            // Genuine miss → batch for the translator. De-dup identical sources within the frame.
            if (missNormalized.TryAdd(id, normalized))
                misses.Add(new TranslationItem { Id = id, Source = d.Text });

            // Reserve an ordered slot for this miss; resolved to a source/null after translation below.
            orderedSources.Add(null);
        }

        if (misses.Count > 0)
        {
            var request = new TranslationRequest
            {
                Items = misses,
                Languages = pair,
                Context = SnapshotContext(),
            };

            TranslationResult fresh = await _translator.TranslateAsync(request, ct).ConfigureAwait(false);

            // Index fresh targets by id so we can both populate the cache and fill the ordered slots.
            var freshTargets = new Dictionary<string, string>(fresh.Items.Count, StringComparer.Ordinal);
            foreach (TranslatedItem item in fresh.Items)
            {
                results.Add(item);
                if (!string.IsNullOrEmpty(item.Target))
                    freshTargets[item.Id] = item.Target;

                if (!item.FromCache && missNormalized.TryGetValue(item.Id, out string? normalized))
                {
                    // Populate the cache so this recurring line is instant next time.
                    if (!string.IsNullOrEmpty(item.Target))
                        _cache.Set(pair, normalized, item.Target);
                }
            }

            // Fill the reserved miss slots in detection order. A miss is only shown (and thus only part of
            // the displayed context) if the translator produced a non-empty target for it.
            int detIndex = 0;
            foreach (DetectedText d in detections)
            {
                string normalized = TextNormalizer.Normalize(d.Text);
                if (normalized.Length == 0) continue;

                if (orderedSources[detIndex] is null)
                {
                    string id = TextNormalizer.Hash(normalized);
                    orderedSources[detIndex] = freshTargets.ContainsKey(id) ? d.Text : null;
                }

                detIndex++;
            }
        }

        // Feed the full ordered set of lines shown this frame (glossary + cache + fresh) into the
        // conversational context once per frame, so recurring lines keep the LLM's context coherent.
        UpdateContext(orderedSources);

        return new TranslationResult(results);
    }

    /// <summary>Compose detections + translations into an <see cref="OverlayFrame"/> keyed by stable id.</summary>
    private static OverlayFrame BuildOverlayFrame(
        IReadOnlyList<DetectedText> detections,
        TranslationResult translation,
        RectI sourceBounds,
        long timestampTicks)
    {
        var byId = new Dictionary<string, string>(translation.Items.Count, StringComparer.Ordinal);
        foreach (TranslatedItem t in translation.Items)
            byId[t.Id] = t.Target;

        var items = new List<OverlayItem>(detections.Count);
        foreach (DetectedText d in detections)
        {
            string normalized = TextNormalizer.Normalize(d.Text);
            if (normalized.Length == 0) continue;
            string id = TextNormalizer.Hash(normalized);

            if (!byId.TryGetValue(id, out string? target) || string.IsNullOrEmpty(target))
                continue; // no translation (suppressed) → don't draw a box

            items.Add(new OverlayItem
            {
                Id = id,
                SourceBox = d.Box,
                Text = target,
                OriginalText = d.Text,
                ForegroundArgb = d.ForegroundArgb,
                BackgroundArgb = d.BackgroundArgb,
            });
        }

        return new OverlayFrame
        {
            Items = items,
            SourceBounds = sourceBounds,
            TimestampTicks = timestampTicks,
        };
    }

    /// <summary>
    /// Run the overlay frame through the stabilizer (cheap, pure logic on the inference thread) then
    /// marshal the actual <see cref="IOverlayRenderer.Present"/> onto the UI thread. The render lane is
    /// never awaited, so inference is never blocked by the compositor.
    /// </summary>
    private void PresentStabilized(OverlayFrame overlay, StageTimer timer)
    {
        OverlayFrame stable = _stabilizer.Stabilize(overlay);

        // Timing the Present marshalling (not the GPU work, which happens async on the UI thread).
        timer.Measure(PipelineStage.Render, () => PostToUi(() =>
        {
            // An in-flight work item may complete after a Pause/Stop posted Clear() to the UI thread.
            // Presenting now would re-show a stale overlay over a paused/torn-down session, so re-check
            // pause + cancellation here (on the UI thread, immediately before the render call) and skip.
            CancellationTokenSource? cts = _runCts;
            if (_paused || cts is null || cts.IsCancellationRequested)
                return;

            // Confirms the render lane actually reached the compositor with N boxes — distinguishes an
            // upstream miss (0 boxes built) from a render/visibility problem (boxes presented, none seen).
            _logger.LogDebug("Presenting {Count} overlay box(es) to the compositor.", stable.Items.Count);
            _renderer.Present(stable);
        }));
    }

    // ===================================================================================
    // Conversational context (prior lines fed to the LLM translator)
    // ===================================================================================

    private IReadOnlyList<string>? SnapshotContext()
    {
        int max = Math.Max(0, _options.Translation.ContextLines);
        if (max == 0) return null;

        lock (_contextGate)
        {
            if (_contextLines.Count == 0) return null;
            int take = Math.Min(max, _contextLines.Count);
            return _contextLines.GetRange(_contextLines.Count - take, take);
        }
    }

    /// <summary>
    /// Append the source lines actually shown this frame (in display order; <c>null</c> entries are
    /// skipped) to the conversational context tail. Called once per frame so the context mirrors every
    /// line on screen — including recurring cached/glossary lines — rather than only fresh cache misses.
    /// </summary>
    private void UpdateContext(IReadOnlyList<string?> sources)
    {
        int max = Math.Max(0, _options.Translation.ContextLines);
        if (max == 0) return;

        lock (_contextGate)
        {
            foreach (string? source in sources)
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                _contextLines.Add(source);
            }

            // Keep only a small tail (bounded to a few multiples of the context size).
            int cap = Math.Max(max * 4, max + 4);
            if (_contextLines.Count > cap)
                _contextLines.RemoveRange(0, _contextLines.Count - cap);
        }
    }

    // ===================================================================================
    // Metrics
    // ===================================================================================

    private void RecordCaptureFrame(long nowTs)
    {
        lock (_metricsGate)
        {
            if (_lastCaptureTs != 0)
            {
                double dtMs = Stopwatch.GetElapsedTime(_lastCaptureTs, nowTs).TotalMilliseconds;
                if (TryFpsSample(dtMs, out double fps)) _captureFps.Add(fps);
            }

            _lastCaptureTs = nowTs;
        }
    }

    /// <summary>
    /// Convert an inter-frame delta into an instantaneous fps sample suitable for the EWMA. Sub-millisecond
    /// deltas (clock jitter, two events landing in the same tick) produce absurd fps spikes (e.g. dt=0.1ms
    /// → 10000 fps) that would poison the smoothed CaptureFps/EffectiveOcrFps for many subsequent samples,
    /// so we drop deltas below <see cref="MinFpsSampleDeltaMs"/> and cap the result at the configured ceiling.
    /// </summary>
    private bool TryFpsSample(double dtMs, out double fps)
    {
        if (dtMs < MinFpsSampleDeltaMs)
        {
            fps = 0;
            return false;
        }

        double maxFps = Math.Max(1, _options.Capture.MaxFps);
        fps = Math.Min(1000.0 / dtMs, maxFps);
        return true;
    }

    private void CommitFrameMetrics(InferenceWorkItem work, bool hadText)
    {
        long now = Stopwatch.GetTimestamp();
        double e2eMs = Stopwatch.GetElapsedTime(work.CaptureTimestampTicks, now).TotalMilliseconds;

        lock (_metricsGate)
        {
            _framesProcessed++;
            _lastTimings = work.Timer.ToTimings();
            _lastE2eMs = e2eMs;
            _e2e.Add(e2eMs);

            if (hadText && _lastProcessedTs != 0)
            {
                double dtMs = Stopwatch.GetElapsedTime(_lastProcessedTs, now).TotalMilliseconds;
                if (TryFpsSample(dtMs, out double fps)) _ocrFps.Add(fps);
            }

            if (hadText) _lastProcessedTs = now;
        }

        if (e2eMs > _options.Pipeline.LatencyBudgetMs)
            _logger.LogDebug("Frame end-to-end {Ms:F1} ms exceeded budget {Budget} ms.",
                e2eMs, _options.Pipeline.LatencyBudgetMs);
    }

    private void UpdateQueuePressure()
    {
        Channel<InferenceWorkItem>? channel = _channel;
        if (channel is null) return;

        int depth = Math.Max(1, _options.Pipeline.InferenceQueueDepth);
        // Reader.Count is supported for bounded channels; clamp defensively.
        int count;
        try { count = channel.Reader.Count; }
        catch (NotSupportedException) { return; }

        _cadence.RecordQueuePressure((double)count / depth);
    }

    private async Task MetricsLoopAsync(CancellationToken ct)
    {
        var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(MetricsIntervalMs));
        try
        {
            while (await ticker.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                UpdateQueuePressure();
                ApplyAdaptiveCadence();
                RaiseMetrics(SnapshotMetrics());
            }
        }
        catch (OperationCanceledException)
        {
            // teardown
        }
        finally
        {
            ticker.Dispose();
        }
    }

    private PipelineMetrics SnapshotMetrics()
    {
        lock (_metricsGate)
        {
            return new PipelineMetrics
            {
                CaptureFps = _captureFps.Value,
                EffectiveOcrFps = _ocrFps.Value,
                LastEndToEndMs = _lastE2eMs,
                P50EndToEndMs = _e2e.P50,
                P95EndToEndMs = _e2e.P95,
                CacheHitRate = _cache.HitRate,
                FramesGated = Interlocked.Read(ref _framesGated),
                FramesProcessed = _framesProcessed,
                LastTimings = _lastTimings,
            };
        }
    }

    private void ResetMetricsAndState()
    {
        lock (_metricsGate)
        {
            _e2e.Reset();
            _captureFps.Reset();
            _ocrFps.Reset();
            Interlocked.Exchange(ref _framesGated, 0);
            _framesProcessed = 0;
            _lastCaptureTs = 0;
            _lastProcessedTs = 0;
            _lastTimings = default;
            _lastE2eMs = 0;
        }
    }

    private void RaiseMetrics(PipelineMetrics metrics)
    {
        try { MetricsUpdated?.Invoke(this, new PipelineMetricsEventArgs(metrics)); }
        catch (Exception ex) { _logger.LogWarning(ex, "MetricsUpdated handler threw."); }
    }

    // ===================================================================================
    // Adaptive cadence
    // ===================================================================================

    /// <summary>
    /// Recompute the target capture FPS and push it to the capturer via <see cref="CaptureSettings"/>.
    /// Restarting a WGC/DXGI session every tick would be wasteful, so we only re-apply when the target
    /// FPS moves by a meaningful step. Capture sources that observe a shared settings object will pick
    /// up the new ceiling without a restart; those that don't are nudged via a lightweight re-start.
    /// </summary>
    private void ApplyAdaptiveCadence()
    {
        // TODO(verify-on-hardware): obtain the real monitor refresh for the target's monitor from the
        // capturer/display info. Until then we keep the configured ceiling as the refresh estimate.
        _cadence.RefreshHz = Math.Max(30, _options.Capture.MaxFps);

        double target = _cadence.TargetFps();
        int rounded = (int)Math.Round(Math.Clamp(target, 1, _options.Capture.MaxFps));

        int previous = Volatile.Read(ref _currentTargetFps);
        // Only act on a >=20% change to avoid thrashing the capture session.
        if (previous != 0 && Math.Abs(rounded - previous) < Math.Max(2, previous / 5))
            return;

        Volatile.Write(ref _currentTargetFps, rounded);
        _logger.LogTrace("Adaptive cadence → {Fps} fps (changeRate={Cr:F2}, pressure={P:F2}).",
            rounded, _cadence.ChangeRate, _cadence.QueuePressure);

        // The capturer reads MaxFps from CaptureSettings at start; many implementations also poll it.
        // We avoid a full restart here (it would drop frames mid-stream); the new value is surfaced via
        // CurrentTargetFps for capturers/hosts that honor live cadence updates.
        // TODO(verify-on-hardware): if the chosen capturer requires a restart to change cadence,
        // implement a debounced restart here guarded by _lifecycleGate.
    }

    private int _currentTargetFps;

    /// <summary>The current adaptive target capture FPS (exposed for capturers/HUD that read it live).</summary>
    public int CurrentTargetFps => Volatile.Read(ref _currentTargetFps);

    private CaptureSettings BuildCaptureSettings()
    {
        int fps = CurrentTargetFps;
        if (fps <= 0) fps = _options.Capture.MaxFps;
        return new CaptureSettings
        {
            MaxFps = Math.Clamp(fps, 1, _options.Capture.MaxFps),
            CaptureCursor = _options.Capture.CaptureCursor,
            ShowCaptureBorder = _options.Capture.ShowCaptureBorder,
            PreferDirtyRegions = _options.Capture.PreferDirtyRegions,
        };
    }

    // ===================================================================================
    // Overlay positioning
    // ===================================================================================

    /// <summary>
    /// Apply overlay style and position it over the target. For region/monitor targets the bounds are
    /// known directly; for window targets we rely on the host/overlay following the window via
    /// SetWinEventHook (per design §7) — here we set an initial best-effort rectangle.
    /// </summary>
    private void ConfigureOverlayForTarget(CaptureTarget target)
    {
        _renderer.Style = _options.Overlay.ToStyle();

        // Position the overlay over the target. Region targets carry their rect in Region; Monitor and
        // Window targets carry their on-screen rect in ScreenBounds (resolved by the App when the target
        // was picked). Without this the overlay window stays 1×1 at (0,0) and nothing is ever visible.
        RectI overlayBounds = target.Mode == CaptureMode.Region ? target.Region : target.ScreenBounds;
        if (!overlayBounds.IsEmpty)
        {
            _logger.LogInformation("Overlay positioned for {Mode} target: {W}x{H} at ({X},{Y}).",
                target.Mode, overlayBounds.Width, overlayBounds.Height, overlayBounds.X, overlayBounds.Y);
            PostToUi(() => _renderer.SetTargetBounds(overlayBounds, DefaultDpi));
        }
        else
            _logger.LogWarning(
                "No on-screen bounds for {Mode} target '{Name}'; overlay cannot be positioned and translations will not be visible.",
                target.Mode, target.DisplayName ?? "(unnamed)");
    }

    private const double DefaultDpi = 96.0;

    // ===================================================================================
    // Engine readiness + teardown + disposal
    // ===================================================================================

    private async Task EnsureEnginesReadyAsync(CancellationToken ct)
    {
        if (!_ocr.IsReady)
        {
            _logger.LogDebug("Initializing OCR engine '{Name}'.", _ocr.Name);
            await _ocr.InitializeAsync(ct).ConfigureAwait(false);
        }

        if (!_translator.IsReady)
        {
            _logger.LogDebug("Initializing translator '{Name}'.", _translator.Name);
            await _translator.InitializeAsync(ct).ConfigureAwait(false);
        }

        if (!_translator.Supports(Languages))
        {
            _logger.LogWarning("Translator '{Name}' does not advertise support for {Pair}; proceeding anyway.",
                _translator.Name, Languages);
        }
    }

    private async Task TeardownAsync()
    {
        // Stop feeding the channel first.
        IScreenCaptureSource? source = _source;
        _source = null;
        if (source is not null)
        {
            source.FrameArrived -= OnFrameArrived;
            source.Error -= OnCaptureError;
            try { await source.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping capture source."); }
            try { await source.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing capture source."); }
        }

        // Complete the channel so workers drain and exit.
        _channel?.Writer.TryComplete();

        // Cancel loops.
        if (_runCts is not null)
        {
            try { await _runCts.CancelAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        // Await workers + metrics loop.
        Task[]? workers = _workers;
        Task? metrics = _metricsLoop;
        _workers = null;
        _metricsLoop = null;

        try
        {
            var pending = new List<Task>();
            if (workers is not null) pending.AddRange(workers);
            if (metrics is not null) pending.Add(metrics);
            if (pending.Count > 0)
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException) { _logger.LogWarning("Pipeline workers did not exit within timeout."); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error awaiting pipeline workers."); }

        // Drain any residual frames.
        if (_channel is not null)
        {
            while (_channel.Reader.TryRead(out InferenceWorkItem? leftover))
                SafeDispose(leftover.Frame);
        }
        _channel = null;

        // Clear + hide the overlay on the UI thread.
        PostToUi(() => { _renderer.Clear(); _renderer.Hide(); });

        _debouncer?.Reset();
        _debouncer = null;

        _runCts?.Dispose();
        _runCts = null;
        Volatile.Write(ref _currentTargetFps, 0);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error during pipeline disposal."); }

        // NOTE: the OCR engine, translator, overlay renderer, capture sources and other collaborators
        // are owned by the DI container (registered by their own modules) and are disposed by it — the
        // pipeline must NOT dispose them here or we would risk double-dispose and ordering hazards. The
        // pipeline only owns the per-run resources released in TeardownAsync (capture source instance,
        // channel, CTS) plus the lifecycle gate below.
        _lifecycleGate.Dispose();

        GC.SuppressFinalize(this);
    }

    // ===================================================================================
    // Helpers
    // ===================================================================================

    private void PostToUi(Action action)
    {
        if (_ui.IsOnUiThread)
        {
            try { action(); }
            catch (Exception ex) { _logger.LogWarning(ex, "UI action threw (inline)."); }
            return;
        }

        _ui.Post(() =>
        {
            try { action(); }
            catch (Exception ex) { _logger.LogWarning(ex, "UI action threw (posted)."); }
        });
    }

    private void SetState(PipelineState state, string? message = null)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _state != state || message is not null;
            _state = state;
        }

        if (!changed) return;

        try { StateChanged?.Invoke(this, new PipelineStateChangedEventArgs(state, message)); }
        catch (Exception ex) { _logger.LogWarning(ex, "StateChanged handler threw."); }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void SafeDispose(IDisposable? d)
    {
        try { d?.Dispose(); }
        catch (Exception ex) { _logger.LogTrace(ex, "Dispose threw (ignored)."); }
    }

    /// <summary>Truncate a possibly-long source/target line for single-line diagnostic logging.</summary>
    private static string Short(string? s) =>
        string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= 60 ? s : s[..60] + "…");
}
