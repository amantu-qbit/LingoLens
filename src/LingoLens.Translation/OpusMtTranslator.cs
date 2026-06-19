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

    private SessionOptions? _sessionOptions;
    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private SentencePieceTokenizer? _sourceTokenizer;
    private SentencePieceTokenizer? _targetTokenizer;
    private int _eosId;
    private int _padId;
    private int _decoderStartId;
    private int _vocabSize;
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
        try
        {
            if (_initialized) return;
            _initialized = true; // mark attempted regardless of outcome

            const string bundle = DefaultModelManifest.OpusMtBundleId;
            if (!_models.IsInstalled(bundle))
            {
                _logger.LogInformation(
                    "Opus-MT bundle '{Bundle}' is not installed; translator is unavailable.", bundle);
                return;
            }

            string encoderPath = _models.GetAssetPath(bundle, "encoder_model.onnx");
            string decoderPath = _models.GetAssetPath(bundle, "decoder_model_merged.onnx");
            string sourceSpm = _models.GetAssetPath(bundle, "source.spm");
            string targetSpm = _models.GetAssetPath(bundle, "target.spm");
            string configPath = _models.GetAssetPath(bundle, "config.json");

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

            _sessionOptions = OrtSessionFactory.CreateSessionOptions(_devices.Selected, _logger);
            _encoder = new InferenceSession(encoderPath, _sessionOptions);
            _decoder = new InferenceSession(decoderPath, _sessionOptions);

            // The merged decoder exposes a boolean 'use_cache_branch' selector; detect it so we can
            // explicitly pick the no-past-cache branch this greedy decoder relies on.
            _decoderWantsUseCacheBranch = _decoder.InputMetadata.ContainsKey("use_cache_branch");

            // Special token ids come from the model config (decoder_start/eos/pad), NOT from the
            // tokenizer's unknown id. Fall back to sensible Marian defaults when config is absent.
            LoadSpecialTokenIdsFromConfig(configPath);

            _ready = true;
            _logger.LogInformation("Opus-MT translator ready on {Device}.", _devices.Selected.Name);
        }
        catch (Exception ex)
        {
            _ready = false;
            _logger.LogError(ex, "Opus-MT initialization failed; translator is unavailable.");
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

        // The encoder/decoder graphs here run one sequence at a time (no batch padding) to keep the
        // implementation robust across exports; the pipeline already batches at a higher level and
        // the cache absorbs repeats. Items are translated sequentially on the inference thread.
        for (int i = 0; i < request.Items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = request.Items[i];
            string target;
            try
            {
                target = await Task.Run(() => TranslateOne(item.Source, ct), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
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
        }

        return new TranslationResult(results);
    }

    private string TranslateOne(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        // 1) Encode source: ids + EOS.
        IReadOnlyList<int> srcIds = _sourceTokenizer!.EncodeToIds(
            source, addBeginningOfSentence: false, addEndOfSentence: true);
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
        int maxOutputTokens = Math.Max(1, Math.Min(_maxOutputTokens, AbsoluteMaxDecoderLength - 1));
        var generated = new List<long>(maxOutputTokens + 1) { _decoderStartId };
        for (int produced = 0; produced < maxOutputTokens; produced++)
        {
            ct.ThrowIfCancellationRequested();
            int nextId = DecodeStep(encoderHidden.AsTensor<float>(), attentionMask, generated);
            if (nextId == _eosId) break;
            generated.Add(nextId);
            // Defensive: never grow the decoder input beyond the model's hard length limit.
            if (generated.Count >= AbsoluteMaxDecoderLength) break;
        }

        // Drop the decoder-start token before detokenizing.
        var outIds = generated.Count > 1
            ? generated.Skip(1).Select(static x => (int)x)
            : Enumerable.Empty<int>();
        return _targetTokenizer!.Decode(outIds).Trim();
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
        // logits shape: [1, seq, vocab]; take the final position.
        int seq = logits.Dimensions[1];
        int vocab = logits.Dimensions[2];
        int best = 0;
        float bestVal = float.NegativeInfinity;
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
        // LlamaTokenizer.Create returns a SentencePieceTokenizer-derived instance configured from the
        // SentencePiece protobuf model. Marian tokenizers add EOS but not BOS.
        var tokenizer = LlamaTokenizer.Create(
            stream, addBeginOfSentence: false, addEndOfSentence: true);
        return tokenizer;
    }

    private static bool IsEnglish(string code) =>
        code.Equals("en", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

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
            DisposeSessions();
        }
        finally
        {
            _initGate.Release();
            _initGate.Dispose();
        }
    }
}
