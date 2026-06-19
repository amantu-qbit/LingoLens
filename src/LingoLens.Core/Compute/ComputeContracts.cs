namespace LingoLens.Core.Compute;

/// <summary>ONNX Runtime execution providers, ordered loosely by performance preference.</summary>
public enum ExecutionProviderKind
{
    Cpu = 0,
    OpenVino = 10,
    DirectMl = 20,
    Cuda = 30,
    TensorRt = 40,
}

/// <summary>Model size/quality tier auto-selected from available compute, overridable by the user.</summary>
public enum ModelTier
{
    /// <summary>CPU-only or weak GPU: smallest models, INT8, Windows OCR fallback.</summary>
    Light = 0,
    /// <summary>Any DX12 GPU: mobile OCR FP16 + small LLM/NMT.</summary>
    Balanced = 1,
    /// <summary>Strong GPU (>=6 GB VRAM): full OCR + 4B LLM translator.</summary>
    Quality = 2,
}

/// <summary>A selectable compute device (a GPU adapter + its best execution provider, or the CPU).</summary>
public sealed record ComputeDevice
{
    /// <summary>Stable identifier (e.g. "gpu:0", "cpu", or the adapter LUID).</summary>
    public required string Id { get; init; }

    /// <summary>Friendly name (GPU model name, or "CPU").</summary>
    public required string Name { get; init; }

    public ExecutionProviderKind Provider { get; init; }

    public long VramBytes { get; init; }

    /// <summary>DXGI adapter index, or -1 for CPU.</summary>
    public int AdapterIndex { get; init; } = -1;

    public bool IsGpu => Provider != ExecutionProviderKind.Cpu;

    /// <summary>Higher = more preferred when auto-selecting.</summary>
    public int Priority { get; init; }

    /// <summary>Best model tier this device can comfortably run.</summary>
    public ModelTier MaxTier { get; init; } = ModelTier.Light;
}

/// <summary>
/// Enumerates compute devices, lets the user pick one (or Auto), and recommends a model tier.
/// Implemented in LingoLens.Compute (DXGI adapter enumeration + ORT provider probing).
/// </summary>
public interface IComputeDeviceManager
{
    IReadOnlyList<ComputeDevice> AvailableDevices { get; }

    /// <summary>The effective device currently in use (resolved even when in Auto mode).</summary>
    ComputeDevice Selected { get; }

    bool IsAuto { get; }

    /// <summary>Recommended tier for the <see cref="Selected"/> device (may be capped by user options).</summary>
    ModelTier RecommendedTier { get; }

    /// <summary>Re-enumerate devices (e.g. after a GPU hot-plug or driver change).</summary>
    void Refresh();

    /// <summary>Select a specific device by id, or pass "auto".</summary>
    void Select(string deviceId);

    void SelectAuto();

    event EventHandler? SelectionChanged;
}
