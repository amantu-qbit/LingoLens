using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using LingoLens.Core.Compute;

namespace LingoLens.Translation.Internal;

/// <summary>
/// Builds ONNX Runtime <see cref="SessionOptions"/> for the currently-selected compute device,
/// mirroring the execution-provider ladder used by the OCR module
/// (TensorRT → CUDA → DirectML → OpenVINO → CPU). Falls back gracefully when a requested EP is
/// unavailable so a session can always be created on at least the CPU provider.
/// </summary>
internal static class OrtSessionFactory
{
    /// <summary>
    /// Creates <see cref="SessionOptions"/> configured for <paramref name="device"/>. The returned
    /// options are owned by the caller and must be disposed.
    /// </summary>
    public static SessionOptions CreateSessionOptions(ComputeDevice device, ILogger logger)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Marian encoder/decoder are small; keep latency low and avoid oversubscription.
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        try
        {
            switch (device.Provider)
            {
                case ExecutionProviderKind.TensorRt:
                    // TensorRT layers on top of CUDA; append both so unsupported subgraphs fall back.
                    options.AppendExecutionProvider_Tensorrt(SafeAdapter(device));
                    options.AppendExecutionProvider_CUDA(SafeAdapter(device));
                    break;
                case ExecutionProviderKind.Cuda:
                    options.AppendExecutionProvider_CUDA(SafeAdapter(device));
                    break;
                case ExecutionProviderKind.DirectMl:
                    // DirectML requires sequential execution and disables memory-pattern planning.
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.EnableMemoryPattern = false;
                    options.AppendExecutionProvider_DML(SafeAdapter(device));
                    break;
                case ExecutionProviderKind.OpenVino:
                    options.AppendExecutionProvider("OpenVINO");
                    break;
                case ExecutionProviderKind.Cpu:
                default:
                    // CPU is always implicitly available; nothing to append.
                    break;
            }
        }
        catch (Exception ex)
        {
            // A driver/runtime mismatch must not be fatal — degrade to CPU.
            logger.LogWarning(ex,
                "Failed to append execution provider {Provider} for device {Device}; using CPU.",
                device.Provider, device.Name);
        }

        return options;
    }

    /// <summary>DXGI adapter index for GPU EPs; clamps the CPU sentinel (-1) to device 0.</summary>
    private static int SafeAdapter(ComputeDevice device) => device.AdapterIndex < 0 ? 0 : device.AdapterIndex;
}
