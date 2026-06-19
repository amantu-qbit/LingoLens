using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using LingoLens.Core.Compute;

namespace LingoLens.Compute;

/// <summary>Probes which ONNX Runtime execution providers are available in this build/runtime.</summary>
public sealed class OrtProviderProbe
{
    private readonly ILogger<OrtProviderProbe> _logger;
    private IReadOnlySet<ExecutionProviderKind>? _cached;

    public OrtProviderProbe(ILogger<OrtProviderProbe>? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrtProviderProbe>.Instance;

    public IReadOnlySet<ExecutionProviderKind> Available
    {
        get
        {
            if (_cached is not null) return _cached;
            var set = new HashSet<ExecutionProviderKind> { ExecutionProviderKind.Cpu };
            try
            {
                foreach (string name in OrtEnv.Instance().GetAvailableProviders())
                {
                    var kind = Map(name);
                    if (kind is { } k) set.Add(k);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ONNX Runtime provider probe failed; assuming CPU only.");
            }
            return _cached = set;
        }
    }

    private static ExecutionProviderKind? Map(string ortName) => ortName switch
    {
        "TensorrtExecutionProvider" => ExecutionProviderKind.TensorRt,
        "CUDAExecutionProvider" => ExecutionProviderKind.Cuda,
        "DmlExecutionProvider" => ExecutionProviderKind.DirectMl,
        "OpenVINOExecutionProvider" => ExecutionProviderKind.OpenVino,
        "CPUExecutionProvider" => ExecutionProviderKind.Cpu,
        _ => null,
    };
}
