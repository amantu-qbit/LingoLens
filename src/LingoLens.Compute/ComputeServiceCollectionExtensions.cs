using Microsoft.Extensions.DependencyInjection;
using LingoLens.Core.Compute;

namespace LingoLens.Compute;

public static class ComputeServiceCollectionExtensions
{
    public static IServiceCollection AddLingoLensCompute(this IServiceCollection services)
    {
        services.AddSingleton<DxgiAdapterEnumerator>();
        services.AddSingleton<OrtProviderProbe>();
        services.AddSingleton<IComputeDeviceManager, ComputeDeviceManager>();
        return services;
    }
}
