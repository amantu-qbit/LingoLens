using Microsoft.Extensions.DependencyInjection;
using LingoLens.Core.Models;
using LingoLens.Core.Translation;
using LingoLens.Translation.Models;

namespace LingoLens.Translation;

/// <summary>
/// DI wiring for the translation module. Registers the model repository, both translation backends,
/// the tier/engine-aware selector and the caching+glossary <see cref="ITranslator"/> the pipeline
/// consumes. Depends on <see cref="ITranslationCache"/> and <see cref="IGlossary"/> registered by
/// <c>AddLingoLensCore</c> and on <c>IComputeDeviceManager</c> from
/// <c>AddLingoLensCompute</c>.
/// </summary>
public static class TranslationServiceCollectionExtensions
{
    /// <summary>
    /// Registers translation services. Call after <c>AddLingoLensCore</c> and
    /// <c>AddLingoLensCompute</c>.
    /// </summary>
    public static IServiceCollection AddLingoLensTranslation(this IServiceCollection services)
    {
        // Model repository (shared with the OCR module via the IModelRepository contract).
        services.AddSingleton<IModelRepository, ModelRepository>();

        // Concrete backends — registered as themselves so the selector can probe/initialize each.
        services.AddSingleton<OpusMtTranslator>();
        services.AddSingleton<Qwen3Translator>();

        // The selector is the public ITranslator the pipeline consumes; it lazily picks the best
        // ready backend and wraps it in caching + glossary.
        services.AddSingleton<TranslatorSelector>();
        services.AddSingleton<ITranslator>(sp => sp.GetRequiredService<TranslatorSelector>());

        return services;
    }
}
