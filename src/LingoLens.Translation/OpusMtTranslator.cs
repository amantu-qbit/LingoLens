using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using LingoLens.Core.Compute;
using LingoLens.Core.Models;
using LingoLens.Core.Translation;
using LingoLens.Translation.Internal;
using LingoLens.Translation.Models;

namespace LingoLens.Translation;

/// <summary>
/// Marian / Opus-MT zh→en translator backed by ONNX Runtime (separate encoder and decoder graphs)
/// and a SentencePiece tokenizer. Performs greedy (beam=1, temperature 0) autoregressive decoding
/// with a per-item length cap. Handles zh / zh-Hans / zh-Hant → en. When the model files are missing,
/// <see cref="IsReady"/> stays <see langword="false"/> and the translator throws nothing.
/// </summary>
public sealed class OpusMtTranslator : ITranslator
{
    /// <summary>
    /// Hard ceiling on PRODUCED output tokens (excluding the decoder-start token) when the model
    /// config does not specify one. Marian zh→en exports use 512 as <c>max_length</c>.
    /// </summary>
    private const int DefaultMaxOutputTokens = 256;

    /// <summary>Absolute upper bound on the decoder sequence length fed to the graph (start + output).</summary>
    private const int AbsoluteMaxDecoderLength = 512;

    private readonly IModelRepository _models;
    private readonly IComputeDeviceManager _devices;
    private readonly ILogger<OpusMtTranslator> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _inferGate = new(1, 1); // serialize decode vs. reset/teardown

    private SessionOptions? _sessionOptions;
    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private SentencePieceTokenizer? _sourceTokenizer;
    private SentencePieceTokenizer? _targetTokenizer;
    private int _eosId;
    private int _padId;
    private int _decoderStartId;
    private int _vocabSize;
    // HF Marian tokenization is SentencePiece → pieces → vocab.json → model ids. The SentencePiece model's
    // own internal ids differ from these, so we must map pieces through vocab.json or the model receives
    // wrong ids and emits garbage. Built from the bundle's vocab.json at init.
    private Dictionary<string, int>? _pieceToId;
    private string[]? _idToPiece;
    private int _unkId = 1;
    private int _maxOutputTokens = DefaultMaxOutputTokens;
    private bool _decoderWantsUseCacheBranch;
    private bool _initialized;
    private volatile bool _ready;
    private volatile bool _disposed;

    public OpusMtTranslator(
        IModelRepository models,
        IComputeDeviceManager devices,
        ILogger<OpusMtTranslator>? logger = null)
    {
        _models = models;
        _devices = devices;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OpusMtTranslator>.Instance;
    }

    /// <inheritdoc />
    public string Name => "opus-mt";

    /// <inheritdoc />
    public bool IsReady => _ready;

    /// <inheritdoc />
    public string? UnavailableReason => _ready ? null : _unavailableReason;

    private volatile string? _unavailableReason = "Opus-MT has not been initialized yet.";

    /// <inheritdoc />
    public bool Supports(LanguagePair pair)
    {
        if (!IsEnglish(pair.Target)) return false;
        string src = pair.Source;
        return string.Equals(src, "zh", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
            || src.Equals("auto", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        string stage = "initializing";
        try
        {
            if (_initialized) return;
            _initialized = true; // mark attempted regardless of outcome

            const string bundle = DefaultModelManifest.OpusMtBundleId;
            if (!_models.IsInstalled(bundle))
            {
                _unavailableReason = "The Fast model isn't fully installed — re-download it in Settings ▸ Models.";
                _logger.LogInformation(
                    "Opus-MT bundle '{Bundle}' is not installed; translator is unavailable.", bundle);
                return;
            }

            string encoderPath = _models.GetAssetPath(bundle, "encoder_model.onnx");
            string decoderPath = _models.GetAssetPath(bundle, "decoder_model_merged.onnx");
            string sourceSpm = _models.GetAssetPath(bundle, "source.spm");
            string targetSpm = _models.GetAssetPath(bundle, "target.spm");
            string configPath = _models.GetAssetPath(bundle, "config.json");

            stage = "loading the tokenizer";
            _sourceTokenizer = LoadTokenizer(sourceSpm);
            if (File.Exists(targetSpm))
            {
                _targetTokenizer = LoadTokenizer(targetSpm);
            }
            else
            {
                _targetTokenizer = _sourceTokenizer;
                _logger.LogWarning(
                    "Opus-MT target tokenizer '{TargetSpm}' is missing; falling back to the source tokenizer. " +
                    "Detokenized output may be incorrect.", targetSpm);
            }

            // Build the encoder/decoder sessions. Translation is pinned to the CPU execution provider
            // (see CreateSessions) — this small autoregressive model decodes far faster there than on
            // DirectML, where per-token GPU dispatch overhead dominated and stalled full-screen frames.
            stage = "loading the model";
            (_encoder, _decoder, _sessionOptions) = CreateSessions(encoderPath, decoderPath);

            // The merged decoder exposes a boolean 'use_cache_branch' selector; detect it so we can
            // explicitly pick the no-past-cache branch this greedy decoder relies on.
            _decoderWantsUseCacheBranch = _decoder.InputMetadata.ContainsKey("use_cache_branch");

            // Special token ids come from the model config (decoder_start/eos/pad), NOT from the
            // tokenizer's unknown id. Fall back to sensible Marian defaults when config is absent.
            stage = "reading the model config";
            LoadSpecialTokenIdsFromConfig(configPath);

            stage = "loading the vocabulary";
            LoadVocab(_models.GetAssetPath(bundle, "vocab.json"));

            _ready = true;
            _unavailableReason = null;
            _logger.LogInformation(
                "Opus-MT translator ready (decoding on CPU; system compute device {Device}).", _devices.Selected.Name);
        }
        catch (Exception ex)
        {
            _ready = false;
            _unavailableReason = $"failed while {stage} — {ex.GetType().Name}: {Short(ex.Message)}";
            _logger.LogError(ex, "Opus-MT initialization failed while {Stage}; translator is unavailable.", stage);
            DisposeSessions();
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Resolves <c>decoder_start_token_id</c>, <c>eos_token_id</c> and <c>pad_token_id</c> from the
    /// model's <c>config.json</c>, falling back to Marian conventions (pad/decoder-start = id 0, eos =
    /// the tokenizer's end-of-sentence id) when the file is missing or a field is absent. All resolved
    /// ids are validated against the decoder vocabulary so a malformed config cannot index out of range.
    /// </summary>
    private void LoadSpecialTokenIdsFromConfig(string configPath)
    {
        // Marian defaults: <pad>/<unk> is id 0 and also serves as the decoder start token.
        int eos = _targetTokenizer!.EndOfSentenceId;
        int pad = 0;
        int decoderStart = 0;

        // Determine the vocabulary size from the decoder's logits output for range validation.
        _vocabSize = GetDecoderVocabSize();

        if (File.Exists(configPath))
        {
            try
            {
                using var stream = File.OpenRead(configPath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                eos = ReadInt(root, "eos_token_id", eos);
                pad = ReadInt(root, "pad_token_id", pad);
                // Marian uses pad as the start token; honour an explicit override when present.
                decoderStart = ReadInt(root, "decoder_start_token_id", pad);

                if (TryReadInt(root, "max_length", out int maxLen) && maxLen > 0)
                    _maxOutputTokens = maxLen;
                if (TryReadInt(root, "vocab_size", out int cfgVocab) && cfgVocab > 0)
                    _vocabSize = _vocabSize > 0 ? Math.Min(_vocabSize, cfgVocab) : cfgVocab;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to parse Opus-MT config '{Config}'; using Marian default token ids.", configPath);
            }
        }
        else
        {
            _logger.LogWarning(
                "Opus-MT config '{Config}' is missing; using Marian default token ids " +
                "(pad/decoder_start=0, eos={Eos}).", configPath, eos);
        }

        _eosId = ValidateId(eos, "eos_token_id", _targetTokenizer.EndOfSentenceId);
        _padId = ValidateId(pad, "pad_token_id", 0);
        _decoderStartId = ValidateId(decoderStart, "decoder_start_token_id", _padId);

        _logger.LogInformation(
            "Opus-MT token ids resolved: decoder_start={Start}, eos={Eos}, pad={Pad} (vocab={Vocab}).",
            _decoderStartId, _eosId, _padId, _vocabSize);
    }

    /// <summary>Validates that <paramref name="id"/> is a non-negative, in-vocabulary id; else falls back.</summary>
    private int ValidateId(int id, string name, int fallback)
    {
        bool inRange = id >= 0 && (_vocabSize <= 0 || id < _vocabSize);
        if (inRange) return id;

        _logger.LogWarning(
            "Opus-MT {Name}={Id} is out of range [0,{Vocab}); falling back to {Fallback}.",
            name, id, _vocabSize, fallback);
        return fallback;
    }

    /// <summary>Reads the decoder's vocabulary size from its logits output metadata, or 0 if unknown.</summary>
    private int GetDecoderVocabSize()
    {
        try
        {
            foreach (var output in _decoder!.OutputMetadata.Values)
            {
                // Marian decoder logits are [batch, seq, vocab]; the trailing dim is the vocab size.
                var dims = output.Dimensions;
                if (dims is { Length: 3 } && dims[2] > 0)
                    return dims[2];
            }
        }
        catch
        {
            // Output metadata is best-effort; absence just disables the upper-bound id check.
        }
        return 0;
    }

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        TryReadInt(root, name, out int value) ? value : fallback;

    private static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        if (request.Items.Count == 0) return TranslationResult.Empty;
        if (!_initialized) await InitializeAsync(ct).ConfigureAwait(false);
        if (!_ready || _encoder is null || _decoder is null || _sourceTokenizer is null || _targetTokenizer is null)
            return TranslationResult.Empty;

        var results = new TranslatedItem[request.Items.Count];

        // Hold the inference gate across the whole batch so a concurrent ResetAsync/DisposeAsync cannot
        // free the ONNX sessions out from under an in-flight decode.
        await _inferGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Translate the lines concurrently across cores. Each decode is sequential within a line, but
            // InferenceSession.Run is thread-safe, so a screenful of lines runs in parallel instead of
            // one-at-a-time — the difference between ~12 s and ~1 s for a full screen. The sessions are
            // pinned to single-threaded intra-op (see CreateSessions) so these parallel decodes don't fight
            // over the same thread pool.
            // Parallel.For (not Parallel.ForAsync) — the body is synchronous CPU work, and ForAsync with a
            // synchronously-completing body collapses onto a single worker, so it never actually parallelized.
            // Run it on the thread pool so we don't block the caller while the cores chew through the lines.
            int dop = Math.Clamp(Environment.ProcessorCount, 1, 16);
            await Task.Run(() => Parallel.For(0, request.Items.Count,
                new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                i =>
                {
                    var item = request.Items[i];
                    string target;
                    try
                    {
                        target = TranslateOne(item.Source, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Opus-MT failed on item {Id}; returning source verbatim.", item.Id);
                        target = item.Source;
                    }

                    results[i] = new TranslatedItem
                    {
                        Id = item.Id,
                        Source = item.Source,
                        Target = target,
                        FromCache = false,
                        Confidence = 1.0,
                    };
                }), ct).ConfigureAwait(false);
        }
        finally
        {
            _inferGate.Release();
        }

        return new TranslationResult(results);
    }

    /// <summary>
    /// Drops the loaded sessions and clears the "already attempted" latch so a later
    /// <see cref="InitializeAsync"/> re-probes the model files. Called after the model bundle is
    /// (re)installed. Waits for any in-flight decode to finish before tearing down.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _inferGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DisposeSessions();
                _sourceTokenizer = null;
                _targetTokenizer = null;
                _ready = false;
                _initialized = false;
            }
            finally
            {
                _inferGate.Release();
            }
        }
        finally
        {
            _initGate.Release();
        }
    }

    private string TranslateOne(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        // 1) Encode source into model (vocab.json) ids, then append EOS.
        IReadOnlyList<int> srcIds = EncodeToVocabIds(source);
        if (srcIds.Count == 0) return string.Empty;

        int srcLen = srcIds.Count;
        long[] inputIds = new long[srcLen];
        long[] attentionMask = new long[srcLen];
        for (int i = 0; i < srcLen; i++)
        {
            inputIds[i] = srcIds[i];
            attentionMask[i] = 1;
        }

        var encoderShape = new int[] { 1, srcLen };
        using var encoderOutputs = RunEncoder(inputIds, attentionMask, encoderShape, ct);
        string hiddenName = EncoderHiddenName(encoderOutputs);
        var encoderHidden = encoderOutputs.First(v => v.Name == hiddenName);

        // 2) Greedy decode.
        // 'generated' always holds [decoder_start, produced_0, produced_1, ...]; the loop appends one
        // produced token per iteration. We cap on the number of PRODUCED output tokens and also clamp
        // so the decoder input sequence (start token + produced tokens) never exceeds the model's
        // maximum decoder length — that keeps positional embeddings in range across exports.
        // Cap the produced-token budget relative to the source length. A translation is rarely longer
        // than ~2x its source; without this, a line that never emits EOS runs the full 256-token ceiling,
        // and because this greedy loop keeps no KV cache, each step re-decodes the whole growing sequence
        // on DirectML — tens of seconds per line. Bounding to the source length keeps every line to a
        // handful of decoder runs (the real reason full-screen translation never completed).
        int lengthCap = srcLen * 2 + 8;
        int maxOutputTokens = Math.Clamp(Math.Min(_maxOutputTokens, lengthCap), 1, AbsoluteMaxDecoderLength - 1);
        var generated = new List<long>(maxOutputTokens + 1) { _decoderStartId };
        for (int produced = 0; produced < maxOutputTokens; produced++)
        {
            ct.ThrowIfCancellationRequested();
            int nextId = DecodeStep(encoderHidden.AsTensor<float>(), attentionMask, generated);
            if (nextId == _eosId) break;
            generated.Add(nextId);
            // Stop degenerate greedy loops early ("were were were…" / "A B A B…"). They otherwise run to the
            // length cap, producing repeated garbage and burning decode time; cutting them short fixes both.
            if (IsDegenerateTail(generated)) break;
            // Defensive: never grow the decoder input beyond the model's hard length limit.
            if (generated.Count >= AbsoluteMaxDecoderLength) break;
        }

        // Map the produced model ids (skipping the decoder-start token) → pieces → text via vocab.json.
        return DecodeVocabIds(generated);
    }

    /// <summary>
    /// Tokenize <paramref name="source"/> with SentencePiece, then map each piece to its model id via
    /// vocab.json (HF Marian's vocabulary), appending the end-of-sentence id. Pieces absent from the vocab
    /// fall back to the unknown id. This is the mapping the ONNX model was exported against — using the
    /// SentencePiece model's own internal ids instead produces wrong inputs and garbage translations.
    /// </summary>
    private List<int> EncodeToVocabIds(string source)
    {
        var ids = new List<int>();
        Dictionary<string, int>? map = _pieceToId;
        if (map is null) return ids;

        IReadOnlyList<EncodedToken> tokens = _sourceTokenizer!.EncodeToTokens(source, out _);
        foreach (EncodedToken token in tokens)
        {
            string piece = token.Value;
            if (map.TryGetValue(piece, out int id) ||
                map.TryGetValue('▁' + piece, out id)) // tolerate a missing word-boundary marker
            {
                ids.Add(id);
            }
            else
            {
                ids.Add(_unkId);
            }
        }

        ids.Add(_eosId);
        return ids;
    }

    /// <summary>
    /// Map produced model ids back to SentencePiece pieces via vocab.json and detokenize: drop special
    /// tokens, concatenate, and turn the ▁ word-boundary marker into a space.
    /// </summary>
    private string DecodeVocabIds(List<long> generated)
    {
        string[]? idToPiece = _idToPiece;
        if (idToPiece is null || generated.Count <= 1) return string.Empty;

        var sb = new StringBuilder(generated.Count * 4);
        // Skip index 0 (the decoder-start token).
        for (int i = 1; i < generated.Count; i++)
        {
            long id = generated[i];
            if (id == _eosId || id == _padId || id == _decoderStartId || id == _unkId) continue;
            if (id < 0 || id >= idToPiece.Length) continue;
            string? piece = idToPiece[id];
            if (string.IsNullOrEmpty(piece) || piece.Length == 0) continue;
            if (piece[0] == '<' && (piece == "<unk>" || piece == "<s>" || piece == "</s>" || piece == "<pad>")) continue;
            sb.Append(piece);
        }

        return sb.Replace('▁', ' ').ToString().Trim();
    }

    /// <summary>Loads the HF Marian vocab.json (piece → id) and builds the reverse id → piece table.</summary>
    private void LoadVocab(string vocabPath)
    {
        using FileStream stream = File.OpenRead(vocabPath);
        using JsonDocument doc = JsonDocument.Parse(stream);

        var pieceToId = new Dictionary<string, int>(StringComparer.Ordinal);
        int maxId = 0;
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int id))
            {
                pieceToId[prop.Name] = id;
                if (id > maxId) maxId = id;
            }
        }

        var idToPiece = new string[maxId + 1];
        foreach (KeyValuePair<string, int> kv in pieceToId)
        {
            if (kv.Value >= 0 && kv.Value < idToPiece.Length) idToPiece[kv.Value] = kv.Key;
        }

        _pieceToId = pieceToId;
        _idToPiece = idToPiece;
        _unkId = pieceToId.TryGetValue("<unk>", out int u) ? u : 1;
        _logger.LogInformation("Opus-MT vocab.json loaded: {Count} pieces (unk={Unk}).", pieceToId.Count, _unkId);
    }

    /// <summary>True when the decoded tail collapsed into a short repeating cycle (greedy degeneration).</summary>
    private static bool IsDegenerateTail(List<long> g)
    {
        int n = g.Count;
        // period-1: the last 4 produced tokens are identical ("were were were were").
        if (n >= 5 && g[n - 1] == g[n - 2] && g[n - 2] == g[n - 3] && g[n - 3] == g[n - 4]) return true;
        // period-2: the last 6 tokens repeat an "A B" pair three times.
        if (n >= 7 && g[n - 1] == g[n - 3] && g[n - 3] == g[n - 5] && g[n - 2] == g[n - 4] && g[n - 4] == g[n - 6]) return true;
        // period-3: the last 6 tokens repeat an "A B C" triple twice.
        if (n >= 7 && g[n - 1] == g[n - 4] && g[n - 2] == g[n - 5] && g[n - 3] == g[n - 6]) return true;
        return false;
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunEncoder(
        long[] inputIds, long[] attentionMask, int[] shape, CancellationToken ct)
    {
        var feeds = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, shape)),
        };
        return _encoder!.Run(feeds);
    }

    private int DecodeStep(Tensor<float> encoderHidden, long[] encoderMask, List<long> generated)
    {
        int decLen = generated.Count;
        long[] decoderInput = generated.ToArray();
        var decShape = new int[] { 1, decLen };
        var encMask = new long[encoderMask.Length];
        Array.Copy(encoderMask, encMask, encoderMask.Length);

        var feeds = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(decoderInput, decShape)),
            NamedOnnxValue.CreateFromTensor("encoder_attention_mask",
                new DenseTensor<long>(encMask, new int[] { 1, encMask.Length })),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHidden),
        };

        // The merged decoder needs an explicit branch selector; we always run the non-cached branch
        // since this greedy loop re-feeds the full sequence each step (no past-key-values supplied).
        if (_decoderWantsUseCacheBranch)
        {
            feeds.Add(NamedOnnxValue.CreateFromTensor(
                "use_cache_branch", new DenseTensor<bool>(new[] { false }, new int[] { 1 })));
        }

        using var outputs = _decoder!.Run(feeds);
        var logits = outputs.First().AsTensor<float>(); // [1, decLen, vocab]
        return ArgMaxLastStep(logits);
    }

    private static int ArgMaxLastStep(Tensor<float> logits)
    {
        // logits shape: [1, seq, vocab]; take the final position. The multi-dimensional indexer recomputes
        // strides on every access, which is wasteful across a ~65k-wide vocab on every decode step — read
        // the final row straight from the backing buffer when the tensor is dense (it always is here).
        int seq = logits.Dimensions[1];
        int vocab = logits.Dimensions[2];
        int best = 0;
        float bestVal = float.NegativeInfinity;

        if (logits is DenseTensor<float> dense)
        {
            ReadOnlySpan<float> row = dense.Buffer.Span.Slice((seq - 1) * vocab, vocab);
            for (int v = 0; v < vocab; v++)
            {
                if (row[v] > bestVal) { bestVal = row[v]; best = v; }
            }

            return best;
        }

        for (int v = 0; v < vocab; v++)
        {
            float val = logits[0, seq - 1, v];
            if (val > bestVal)
            {
                bestVal = val;
                best = v;
            }
        }

        return best;
    }

    private static string EncoderHiddenName(IReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        foreach (var o in outputs)
        {
            if (o.Name.Contains("hidden", StringComparison.OrdinalIgnoreCase) ||
                o.Name.Equals("last_hidden_state", StringComparison.OrdinalIgnoreCase))
                return o.Name;
        }
        return outputs.First().Name;
    }

    private static SentencePieceTokenizer LoadTokenizer(string spmPath)
    {
        using var stream = File.OpenRead(spmPath);
        // Opus-MT / Marian ships a *Unigram* SentencePiece model. SentencePieceTokenizer.Create (added in
        // Microsoft.ML.Tokenizers 2.0.0) loads both Unigram and BPE models; LlamaTokenizer.Create accepted
        // BPE only and threw "The model type is not Bpe." on Marian's unigram model — the bug that kept this
        // translator permanently unavailable. Marian tokenizers add EOS but not BOS.
        // We only use SentencePiece for segmentation into pieces; ids and EOS are applied via vocab.json
        // (see EncodeToVocabIds), so don't let the tokenizer auto-insert BOS/EOS pieces here.
        return SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
    }

    private static string Short(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= 160 ? s : s[..160] + "…");

    private static bool IsEnglish(string code) =>
        code.Equals("en", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the encoder + decoder sessions on the ORT CPU execution provider. Opus-MT is a small
    /// autoregressive model and this greedy loop keeps no KV cache, so it issues one full decoder Run per
    /// output token, re-uploading <c>encoder_hidden_states</c> each step. On DirectML the per-Run GPU
    /// dispatch and host↔device transfer dwarf the tiny compute and make a screenful of lines take
    /// minutes; the CPU EP runs these short single-sequence steps with far lower per-call latency and is
    /// dramatically faster here. OCR and the overlay keep the GPU — only translation is pinned to CPU.
    /// </summary>
    private (InferenceSession encoder, InferenceSession decoder, SessionOptions options) CreateSessions(
        string encoderPath, string decoderPath)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        // Keep each decode single-threaded so we can instead translate many lines concurrently across cores
        // (see TranslateAsync). This model's per-token cost is dominated by fixed per-Run overhead, not by
        // threaded compute, so single-threaded decode is barely slower per line — but it lets a full screen
        // of lines run in parallel (≈12 s → ≈1 s) instead of one-at-a-time.
        options.IntraOpNumThreads = 1;
        options.InterOpNumThreads = 1;
        InferenceSession? encoder = null, decoder = null;
        try
        {
            encoder = new InferenceSession(encoderPath, options);
            decoder = new InferenceSession(decoderPath, options);
            return (encoder, decoder, options);
        }
        catch
        {
            encoder?.Dispose();
            decoder?.Dispose();
            options.Dispose();
            throw;
        }
    }

    private void DisposeSessions()
    {
        _encoder?.Dispose();
        _decoder?.Dispose();
        _sessionOptions?.Dispose();
        _encoder = null;
        _decoder = null;
        _sessionOptions = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;

        // Acquire the init gate so any in-flight InitializeAsync completes before we tear down the
        // sessions and dispose the semaphore — avoids ObjectDisposedException at shutdown.
        try
        {
            await _initGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // Already disposed by a concurrent caller.
        }

        try
        {
            // Drain any in-flight decode before freeing the sessions and the gate.
            await _inferGate.WaitAsync().ConfigureAwait(false);
            try
            {
                DisposeSessions();
            }
            finally
            {
                _inferGate.Release();
                _inferGate.Dispose();
            }
        }
        finally
        {
            _initGate.Release();
            _initGate.Dispose();
        }
    }
}
