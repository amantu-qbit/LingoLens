using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using LingoLens.Core.Compute;

namespace LingoLens.Ocr.Internal;

/// <summary>
/// Builds <see cref="InferenceSession"/> instances configured for the currently-selected compute device,
/// wiring the appropriate ONNX Runtime execution provider (DirectML / CUDA / TensorRT / CPU).
/// </summary>
internal static class OnnxSessionFactory
{
    /// <summary>
    /// Creates a session for <paramref name="modelPath"/> using the execution provider implied by
    /// <paramref name="device"/>. On any EP-append failure the build falls back to CPU so the engine
    /// can still operate (degraded) rather than failing outright.
    /// </summary>
    public static InferenceSession Create(string modelPath, ComputeDevice device, ILogger logger)
    {
        // InferenceSession copies whatever it needs from SessionOptions at construction time and does NOT
        // take ownership of it, so the options must be disposed by us once the session has been built.
        SessionOptions options = BuildOptions(device, logger);
        try
        {
            return new InferenceSession(modelPath, options);
        }
        catch (Exception ex) when (device.Provider != ExecutionProviderKind.Cpu)
        {
            logger.LogWarning(ex,
                "Failed to create ONNX session for '{Model}' on {Provider}; retrying on CPU.",
                modelPath, device.Provider);

            using SessionOptions cpu = BuildOptions(
                device with { Provider = ExecutionProviderKind.Cpu, AdapterIndex = -1 }, logger);
            return new InferenceSession(modelPath, cpu);
        }
        finally
        {
            options.Dispose();
        }
    }

    private static SessionOptions BuildOptions(ComputeDevice device, ILogger logger)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
        };

        // DirectML requires sequential execution and no mem pattern; harmless for other EPs too.
        options.EnableMemoryPattern = device.Provider != ExecutionProviderKind.DirectMl;

        int adapter = device.AdapterIndex < 0 ? 0 : device.AdapterIndex;
        try
        {
            switch (device.Provider)
            {
                case ExecutionProviderKind.TensorRt:
                    // TensorRT EP layers CUDA underneath; append both so unsupported ops fall back to CUDA.
                    options.AppendExecutionProvider_Tensorrt(adapter);
                    options.AppendExecutionProvider_CUDA(adapter);
                    break;
                case ExecutionProviderKind.Cuda:
                    options.AppendExecutionProvider_CUDA(adapter);
                    break;
                case ExecutionProviderKind.DirectMl:
                    // DML adapter index maps to the DXGI adapter ordinal.
                    options.AppendExecutionProvider_DML(adapter);
                    break;
                case ExecutionProviderKind.OpenVino:
                    // OpenVINO device string: "GPU" preferred, "CPU" fallback handled by the runtime.
                    options.AppendExecutionProvider_OpenVINO("GPU");
                    break;
                case ExecutionProviderKind.Cpu:
                default:
                    // CPU EP is always registered last implicitly; nothing to append.
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AppendExecutionProvider for {Provider} failed; session will run on CPU.", device.Provider);
        }

        return options;
    }
}
