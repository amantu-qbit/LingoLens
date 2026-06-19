using Microsoft.Extensions.Logging;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace LingoLens.Compute;

/// <summary>Enumerates physical GPU adapters via DXGI (hardware adapters only).</summary>
public sealed class DxgiAdapterEnumerator
{
    private readonly ILogger<DxgiAdapterEnumerator> _logger;

    public DxgiAdapterEnumerator(ILogger<DxgiAdapterEnumerator>? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DxgiAdapterEnumerator>.Instance;

    public IReadOnlyList<GpuInfo> Enumerate()
    {
        var result = new List<GpuInfo>();
        try
        {
            using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success && adapter is not null; i++)
            {
                using (adapter)
                {
                    AdapterDescription1 desc = adapter.Description1;
                    bool isSoftware = (desc.Flags & AdapterFlags.Software) != 0;
                    if (isSoftware)
                        continue; // skip Microsoft Basic Render Driver

                    long vram = unchecked((long)(ulong)desc.DedicatedVideoMemory);
                    result.Add(new GpuInfo(
                        AdapterIndex: (int)i,
                        Name: (desc.Description ?? "GPU").Trim(),
                        DedicatedVideoMemory: vram,
                        VendorId: (uint)desc.VendorId,
                        IsSoftware: false));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DXGI adapter enumeration failed; falling back to CPU only.");
        }
        return result;
    }
}
