using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using LingoLens.Core.Configuration;
using LingoLens.Core.Overlay;
using LingoLens.Overlay.Rendering;
using LingoLens.Overlay.Smoothing;

namespace LingoLens.Overlay;

/// <summary>
/// DI wiring for the DirectComposition overlay module. Registers the renderer and temporal stabilizer against
/// their <c>LingoLens.Core.Overlay</c> contracts.
/// </summary>
public static class OverlayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IOverlayRenderer"/> (singleton DirectComposition renderer) and
    /// <see cref="IOverlayStabilizer"/> (exponential smoother). The renderer's initial style is taken from
    /// <see cref="LingoLensOptions.Overlay"/> when those options are registered.
    /// </summary>
    public static IServiceCollection AddLingoLensOverlay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // One overlay surface per process (it owns a UI thread + GPU device).
        services.TryAddSingleton<IOverlayRenderer>(sp =>
        {
            var options = sp.GetService<LingoLensOptions>();
            var style = options?.Overlay.ToStyle();
            var logger = sp.GetService<ILogger<DirectCompositionOverlay>>();
            return new DirectCompositionOverlay(style, logger);
        });

        // Stabilizer is cheap, pure logic; one per consumer (the pipeline drives it from a single lane).
        services.TryAddTransient<IOverlayStabilizer>(sp =>
        {
            var options = sp.GetService<LingoLensOptions>();
            var stabilizer = new ExponentialOverlayStabilizer();
            if (options is not null)
                stabilizer.SetFadeMilliseconds(options.Overlay.FadeMilliseconds);
            return stabilizer;
        });

        return services;
    }
}
