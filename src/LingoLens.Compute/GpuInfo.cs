namespace LingoLens.Compute;

/// <summary>Raw info about one physical GPU adapter (from DXGI).</summary>
public readonly record struct GpuInfo(
    int AdapterIndex,
    string Name,
    long DedicatedVideoMemory,
    uint VendorId,
    bool IsSoftware)
{
    public GpuVendor Vendor => VendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        _ => GpuVendor.Other,
    };
}

public enum GpuVendor { Other, Nvidia, Amd, Intel }
