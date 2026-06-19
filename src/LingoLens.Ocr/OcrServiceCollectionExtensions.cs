using Microsoft.Extensions.DependencyInjection;
using LingoLens.Core.Ocr;

namespace LingoLens.Ocr;

/// <summary>DI registration for the OCR module.</summary>
public static class OcrServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OCR engines (PP-OCRv5 ONNX + Windows.Media.Ocr fallback) and exposes the
    /// <see cref="OcrEngineSelector"/> as the single <see cref="IOcrEngine"/> the pipeline resolves.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddLingoLensCompute</c> (for <see cref="LingoLens.Core.Compute.IComputeDeviceManager"/>),
    /// a registered <see cref="LingoLens.Core.Models.IModelRepository"/>, and a singleton
    /// <see cref="LingoLens.Core.Configuration.LingoLensOptions"/>.
    /// </remarks>
    public static IServiceCollection AddLingoLensOcr(this IServiceCollection services)
    {
        services.AddSingleton<PaddleOcrV5Engine>();
        services.AddSingleton<WindowsMediaOcrEngine>();
        services.AddSingleton<OcrEngineSelector>();
        services.AddSingleton<IOcrEngine>(sp => sp.GetRequiredService<OcrEngineSelector>());
        return services;
    }
}
