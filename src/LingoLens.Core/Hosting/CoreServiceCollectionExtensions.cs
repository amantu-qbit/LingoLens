using Microsoft.Extensions.DependencyInjection;
using LingoLens.Core.Configuration;
using LingoLens.Core.Translation;

namespace LingoLens.Core.Hosting;

/// <summary>
/// Registers the platform-neutral defaults (cache, glossary, options). Platform modules
/// (Capture/Ocr/Overlay/Compute/Pipeline) add their own registrations on top.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddLingoLensCore(
        this IServiceCollection services,
        Action<LingoLensOptions>? configure = null)
    {
        var options = new LingoLensOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<ITranslationCache>(_ =>
            new LruTranslationCache(options.Translation.CacheCapacity));
        services.AddSingleton<IGlossary, InMemoryGlossary>();

        return services;
    }
}
