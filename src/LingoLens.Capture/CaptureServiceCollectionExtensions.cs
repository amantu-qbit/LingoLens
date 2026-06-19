using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using LingoLens.Core.Capture;

namespace LingoLens.Capture;

/// <summary>DI registration for the LingoLens capture stack.</summary>
public static class CaptureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the capture services: the shared <see cref="DeviceResources"/> D3D11 device, the
    /// <see cref="ICaptureSourceFactory"/> that selects WGC/DXGI/region sources, and a transient
    /// <see cref="IChangeDetector"/> (one per capture/gate thread).
    /// </summary>
    public static IServiceCollection AddLingoLensCapture(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // One shared GPU device for the whole capture stack. Use an explicit factory so the optional
        // adapter constructor argument resolves to null (auto-select default adapter).
        services.TryAddSingleton(sp => new DeviceResources(
            sp.GetRequiredService<ILogger<DeviceResources>>()));

        services.TryAddSingleton<ICaptureSourceFactory, CaptureSourceFactory>();

        // Change detectors hold per-frame state; each consumer gets its own instance.
        services.TryAddTransient<IChangeDetector, TileHashChangeDetector>();

        return services;
    }
}
