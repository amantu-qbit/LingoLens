using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LingoLens.Core.Pipeline;

namespace LingoLens.Pipeline;

/// <summary>DI registration for the LingoLens orchestration pipeline.</summary>
public static class PipelineServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ITranslationPipeline"/> (implemented by <see cref="TranslationPipeline"/>) as
    /// a singleton, plus a default <see cref="IUiDispatcher"/> if the host has not supplied one.
    /// </summary>
    /// <remarks>
    /// The pipeline depends on the Core capture/OCR/translation/overlay/compute interfaces and
    /// <see cref="LingoLens.Core.Configuration.LingoLensOptions"/>; register those (and a
    /// logging provider) from their respective modules before resolving the pipeline. Call
    /// <c>AddLingoLensPipeline</c> on the UI thread (or supply an <see cref="IUiDispatcher"/>) so
    /// the default dispatcher captures the correct render-thread <see cref="SynchronizationContext"/>.
    /// </remarks>
    public static IServiceCollection AddLingoLensPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Default UI dispatcher: captures the current SynchronizationContext when first resolved.
        // The composition root may override this with a WPF Dispatcher-backed implementation by
        // registering its own IUiDispatcher before calling this method.
        services.TryAddSingleton<IUiDispatcher>(_ => new SynchronizationContextUiDispatcher());

        services.TryAddSingleton<TranslationPipeline>();
        services.TryAddSingleton<ITranslationPipeline>(sp => sp.GetRequiredService<TranslationPipeline>());

        return services;
    }
}
