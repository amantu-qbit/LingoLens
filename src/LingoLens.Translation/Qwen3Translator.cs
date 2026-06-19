using System.Text;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using LingoLens.Core.Compute;
using LingoLens.Core.Configuration;
using LingoLens.Core.Models;
using LingoLens.Core.Translation;
using LingoLens.Translation.Models;

namespace LingoLens.Translation;

/// <summary>
/// Multi-language LLM translator backed by LLamaSharp running a quantized Qwen3 GGUF model. Builds a
/// concise instruction prompt (system line + optional conversational context + the lines to
/// translate), decodes greedily (temperature 0) and parses one translation per input line. When the
/// model file or a LLamaSharp native backend is unavailable, <see cref="IsReady"/> stays
/// <see langword="false"/> and no exception escapes construction or initialization.
/// </summary>
/// <remarks>
/// A LLamaSharp backend package (e.g. <c>LLamaSharp.Backend.Cpu</c> / <c>.Cuda</c> / <c>.Vulkan</c>)
/// is added at packaging time; if it is missing the native library fails to load and this translator
/// reports itself not-ready rather than throwing.
/// </remarks>
public sealed class Qwen3Translator : ITranslator
{
    private const int MaxTokensPerRequest = 1024;

    /// <summary>
    /// The llama.cpp main-GPU ordinal. The DXGI <see cref="ComputeDevice.AdapterIndex"/> is NOT a valid
    /// llama.cpp device ordinal (different enumeration, may exclude adapters), so we deliberately do not
    /// forward it. We always use device 0 — the first device llama.cpp enumerates — which is safe whether
    /// a GPU is selected (offloading targets the first backend device) or the model runs CPU-only (the
    /// value is ignored when no layers are offloaded). This avoids pointing the backend at a missing GPU.
    /// </summary>
    private const int MainGpuOrdinal = 0;

    private readonly IModelRepository _models;
    private readonly IComputeDeviceManager _devices;
    private readonly TranslationOptions _options;
    private readonly ILogger<Qwen3Translator> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _inferGate = new(1, 1); // executor is single-threaded

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private bool _initialized;
    private volatile bool _ready;
    private volatile bool _disposed;

    public Qwen3Translator(
        IModelRepository models,
        IComputeDeviceManager devices,
        LingoLensOptions options,
        ILogger<Qwen3Translator>? logger = null)
    {
        _models = models;
        _devices = devices;
        _options = options.Translation;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Qwen3Translator>.Instance;
    }

    /// <inheritdoc />
    public string Name => "qwen3";

    /// <inheritdoc />
    public bool IsReady => _ready;

    /// <summary>Qwen3 is broadly multilingual; accept any pair where the source and target differ.</summary>
    public bool Supports(LanguagePair pair) =>
        !string.IsNullOrWhiteSpace(pair.Source) &&
        !string.IsNullOrWhiteSpace(pair.Target) &&
        (pair.IsAutoSource || !string.Equals(pair.Source, pair.Target, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            _initialized = true;

            const string bundle = DefaultModelManifest.Qwen3BundleId;
            if (!_models.IsInstalled(bundle))
            {
                _logger.LogInformation("Qwen3 bundle '{Bundle}' not installed; translator unavailable.", bundle);
                return;
            }

            string ggufPath = _models.GetAssetPath(bundle, "Qwen3-4B-Q4_K_M.gguf");

            var parameters = new ModelParams(ggufPath)
            {
                ContextSize = 4096,
                // Offload as much as possible to the GPU when one is selected; CPU otherwise.
                GpuLayerCount = _devices.Selected.IsGpu ? 999 : 0,
                // NOTE: ComputeDevice.AdapterIndex is a DXGI adapter ordinal, which does NOT correspond
                // to llama.cpp's device ordinal (its enumeration order differs and may exclude adapters).
                // Pointing MainGpu at a stale/non-existent device can crash the native backend, so we do
                // not reuse it: default to device 0 (the first llama.cpp device) for any GPU selection.
                MainGpu = MainGpuOrdinal,
                // Leave a thread free for the UI/render lane.
                Threads = Math.Max(1, Environment.ProcessorCount - 1),
            };

            // LoadFromFileAsync surfaces native-backend load failures here, where we degrade to
            // not-ready instead of throwing.
            _weights = await LLamaWeights.LoadFromFileAsync(parameters, ct).ConfigureAwait(false);
            _executor = new StatelessExecutor(_weights, parameters);

            _ready = true;
            _logger.LogInformation("Qwen3 translator ready on {Device}.", _devices.Selected.Name);
        }
        catch (Exception ex)
        {
            // Missing native backend (DllNotFoundException), unsupported model, OOM, etc.
            _ready = false;
            _logger.LogWarning(ex, "Qwen3 initialization failed; translator unavailable.");
            _weights?.Dispose();
            _weights = null;
            _executor = null;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        if (request.Items.Count == 0) return TranslationResult.Empty;
        if (!_initialized) await InitializeAsync(ct).ConfigureAwait(false);
        if (!_ready || _executor is null)
            return TranslationResult.Empty;

        string prompt = BuildPrompt(request);

        string raw;
        await _inferGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            raw = await RunInferenceAsync(prompt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qwen3 inference failed; returning sources verbatim.");
            return Fallback(request);
        }
        finally
        {
            _inferGate.Release();
        }

        return Parse(request, raw);
    }

    private async Task<string> RunInferenceAsync(string prompt, CancellationToken ct)
    {
        var inferenceParams = new InferenceParams
        {
            MaxTokens = MaxTokensPerRequest,
            // Greedy / deterministic: always take the arg-max token (temperature 0, beam 1).
            SamplingPipeline = new GreedySamplingPipeline(),
            AntiPrompts = new[] { "<|im_end|>", "<|endoftext|>" },
        };

        var sb = new StringBuilder();
        await foreach (string token in _executor!.InferAsync(prompt, inferenceParams, ct).ConfigureAwait(false))
        {
            sb.Append(token);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a chat-formatted prompt using Qwen's ChatML markers: a translator system instruction,
    /// optional recent context, then the numbered lines to translate.
    /// </summary>
    private string BuildPrompt(TranslationRequest request)
    {
        string src = LanguageName(request.Languages.Source);
        string tgt = LanguageName(request.Languages.Target);

        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n");
        sb.Append("You are a translator. Translate the user's text from ").Append(src)
          .Append(" to ").Append(tgt).Append(". ");
        sb.Append("Output ONLY the translation. Keep emoji and tone. Do not explain.\n");
        sb.Append("Each input line is numbered; reply with the same numbering, one translation per line.");
        sb.Append("<|im_end|>\n");

        sb.Append("<|im_start|>user\n");

        int contextLines = Math.Max(0, _options.ContextLines);
        if (request.Context is { Count: > 0 } && contextLines > 0)
        {
            int take = Math.Min(contextLines, request.Context.Count);
            sb.Append("Conversation so far (context, do not translate):\n");
            for (int i = request.Context.Count - take; i < request.Context.Count; i++)
                sb.Append("- ").Append(request.Context[i]).Append('\n');
            sb.Append('\n');
        }

        sb.Append("Translate these ").Append(request.Items.Count).Append(" line(s):\n");
        for (int i = 0; i < request.Items.Count; i++)
            sb.Append(i + 1).Append(". ").Append(request.Items[i].Source).Append('\n');
        sb.Append("<|im_end|>\n");

        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    /// <summary>Maps the model output back onto items by their leading line number, in order.</summary>
    private TranslationResult Parse(TranslationRequest request, string raw)
    {
        int count = request.Items.Count;
        var byIndex = new string?[count];

        // Collect the cleaned, non-empty output lines once so we can both parse numbering and, if the
        // model dropped the numbering, fall back to assigning lines positionally.
        var cleanedLines = new List<string>();
        int parsedNumbered = 0;

        foreach (string lineRaw in raw.Split('\n'))
        {
            string line = StripChatMarkers(lineRaw).Trim();
            if (line.Length == 0) continue;
            cleanedLines.Add(line);

            if (TryParseNumberedLine(line, out int number, out string text) &&
                number >= 1 && number <= byIndex.Length)
            {
                if (byIndex[number - 1] is null)
                {
                    byIndex[number - 1] = text;
                    parsedNumbered++;
                }
            }
        }

        // Fallback for any unparsed line: if exactly one item, use the whole cleaned output.
        if (count == 1 && byIndex[0] is null)
        {
            byIndex[0] = StripChatMarkers(raw).Trim();
        }
        else if (count > 1 && parsedNumbered == 0 && cleanedLines.Count == count)
        {
            // The model replied with N unnumbered lines (one per request item). Assign them in order
            // rather than letting every item fall back to its verbatim source.
            for (int i = 0; i < count; i++)
                byIndex[i] = cleanedLines[i];
        }

        var items = new TranslatedItem[count];
        for (int i = 0; i < count; i++)
        {
            string target = byIndex[i] ?? request.Items[i].Source;
            items[i] = new TranslatedItem
            {
                Id = request.Items[i].Id,
                Source = request.Items[i].Source,
                Target = target,
                FromCache = false,
                Confidence = byIndex[i] is null ? 0.0 : 1.0,
            };
        }
        return new TranslationResult(items);
    }

    private static bool TryParseNumberedLine(string line, out int number, out string text)
    {
        number = 0;
        text = line;
        int i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i == 0) return false; // no leading number
        if (!int.TryParse(line.AsSpan(0, i), out number)) return false;

        // Skip a following '.', ')' or ':' and whitespace.
        int j = i;
        while (j < line.Length && (line[j] is '.' or ')' or ':' or ' ' or '\t')) j++;
        text = line[j..].Trim();
        return text.Length > 0;
    }

    private static string StripChatMarkers(string s) => s
        .Replace("<|im_end|>", string.Empty, StringComparison.Ordinal)
        .Replace("<|im_start|>", string.Empty, StringComparison.Ordinal)
        .Replace("<|endoftext|>", string.Empty, StringComparison.Ordinal);

    private static TranslationResult Fallback(TranslationRequest request)
    {
        var items = new TranslatedItem[request.Items.Count];
        for (int i = 0; i < request.Items.Count; i++)
            items[i] = new TranslatedItem
            {
                Id = request.Items[i].Id,
                Source = request.Items[i].Source,
                Target = request.Items[i].Source,
                FromCache = false,
                Confidence = 0.0,
            };
        return new TranslationResult(items);
    }

    private static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "zh" or "zh-hans" or "zh-cn" => "Chinese (Simplified)",
        "zh-hant" or "zh-tw" or "zh-hk" => "Chinese (Traditional)",
        "en" or "en-us" or "en-gb" => "English",
        "ja" => "Japanese",
        "ko" => "Korean",
        "auto" => "the detected source language",
        _ => code,
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;

        // Drain in-flight init and inference before tearing down so we never dispose a semaphore that is
        // still being awaited, nor free the native weights while the executor is mid-decode.
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
            await _inferGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _executor = null;
                _weights?.Dispose();
                _weights = null;
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
