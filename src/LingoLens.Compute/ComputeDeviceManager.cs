using Microsoft.Extensions.Logging;
using LingoLens.Core.Compute;
using LingoLens.Core.Configuration;

namespace LingoLens.Compute;

/// <summary>
/// Combines DXGI adapter enumeration with ONNX Runtime provider probing to produce a ranked list of
/// selectable compute devices, auto-selects the best, and recommends a model tier. Supports user
/// override (specific device or "auto").
/// </summary>
public sealed class ComputeDeviceManager : IComputeDeviceManager
{
    private const long Gb = 1024L * 1024 * 1024;

    private readonly DxgiAdapterEnumerator _adapters;
    private readonly OrtProviderProbe _providers;
    private readonly ComputeOptions _options;
    private readonly ILogger<ComputeDeviceManager> _logger;

    private List<ComputeDevice> _devices = new();
    private ComputeDevice _selected;
    private bool _isAuto = true;

    public ComputeDeviceManager(
        DxgiAdapterEnumerator adapters,
        OrtProviderProbe providers,
        LingoLensOptions options,
        ILogger<ComputeDeviceManager>? logger = null)
    {
        _adapters = adapters;
        _providers = providers;
        _options = options.Compute;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ComputeDeviceManager>.Instance;
        _selected = CpuDevice();
        Refresh();
        ApplyOptionSelection();
    }

    public IReadOnlyList<ComputeDevice> AvailableDevices => _devices;
    public ComputeDevice Selected => _selected;
    public bool IsAuto => _isAuto;
    public event EventHandler? SelectionChanged;

    public ModelTier RecommendedTier
    {
        get
        {
            ModelTier tier = _selected.MaxTier;
            ModelTier? cap = _options.MaxTier?.ToLowerInvariant() switch
            {
                "light" => ModelTier.Light,
                "balanced" => ModelTier.Balanced,
                "quality" => ModelTier.Quality,
                _ => null, // "auto" / null
            };
            if (cap is { } c && c < tier) tier = c;
            return tier;
        }
    }

    public void Refresh()
    {
        var available = _providers.Available;
        var gpus = _adapters.Enumerate();
        var devices = new List<ComputeDevice>();

        foreach (var gpu in gpus)
        {
            ExecutionProviderKind? ep = ChooseGpuProvider(gpu, available);
            if (ep is not { } provider) continue; // no usable GPU EP for this adapter

            long vram = gpu.DedicatedVideoMemory;
            devices.Add(new ComputeDevice
            {
                Id = $"gpu:{gpu.AdapterIndex}",
                Name = gpu.Name,
                Provider = provider,
                VramBytes = vram,
                AdapterIndex = gpu.AdapterIndex,
                MaxTier = TierFor(vram, isGpu: true),
                Priority = ((int)provider * 1_000_000) + (int)(vram / Gb),
            });
        }

        devices.Add(CpuDevice());
        devices.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        _devices = devices;

        _logger.LogInformation("Compute devices: {Devices}",
            string.Join(", ", _devices.Select(d => $"{d.Name}[{d.Provider},{d.VramBytes / Gb}GB,{d.MaxTier}]")));

        // Re-resolve the current selection against the refreshed list.
        if (_isAuto) _selected = _devices[0];
        else _selected = _devices.FirstOrDefault(d => d.Id == _selected.Id, _devices[0]);
    }

    public void Select(string deviceId)
    {
        if (string.Equals(deviceId, "auto", StringComparison.OrdinalIgnoreCase))
        {
            SelectAuto();
            return;
        }
        var match = _devices.FirstOrDefault(d => d.Id == deviceId);
        if (match is null)
        {
            _logger.LogWarning("Requested device '{Id}' not found; keeping {Current}.", deviceId, _selected.Name);
            return;
        }
        _isAuto = false;
        SetSelected(match);
    }

    public void SelectAuto()
    {
        _isAuto = true;
        SetSelected(_devices[0]);
    }

    private void ApplyOptionSelection()
    {
        if (string.IsNullOrWhiteSpace(_options.Device) ||
            string.Equals(_options.Device, "auto", StringComparison.OrdinalIgnoreCase))
            SelectAuto();
        else
            Select(_options.Device);
    }

    private void SetSelected(ComputeDevice device)
    {
        if (device.Id == _selected.Id) { _selected = device; return; }
        _selected = device;
        _logger.LogInformation("Selected compute device: {Name} ({Provider}), tier {Tier}.",
            device.Name, device.Provider, RecommendedTier);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private ExecutionProviderKind? ChooseGpuProvider(GpuInfo gpu, IReadOnlySet<ExecutionProviderKind> available)
    {
        bool allowCuda = _options.AllowCudaUpgrade;
        if (gpu.Vendor == GpuVendor.Nvidia && allowCuda)
        {
            if (available.Contains(ExecutionProviderKind.TensorRt)) return ExecutionProviderKind.TensorRt;
            if (available.Contains(ExecutionProviderKind.Cuda)) return ExecutionProviderKind.Cuda;
        }
        if (available.Contains(ExecutionProviderKind.DirectMl)) return ExecutionProviderKind.DirectMl;
        if (gpu.Vendor == GpuVendor.Intel && available.Contains(ExecutionProviderKind.OpenVino))
            return ExecutionProviderKind.OpenVino;
        return null;
    }

    private static ModelTier TierFor(long vramBytes, bool isGpu)
    {
        if (!isGpu) return ModelTier.Light;
        if (vramBytes >= 6 * Gb) return ModelTier.Quality;
        if (vramBytes >= 2 * Gb) return ModelTier.Balanced;
        return ModelTier.Light;
    }

    private static ComputeDevice CpuDevice() => new()
    {
        Id = "cpu",
        Name = $"CPU ({Environment.ProcessorCount} threads)",
        Provider = ExecutionProviderKind.Cpu,
        VramBytes = 0,
        AdapterIndex = -1,
        MaxTier = ModelTier.Light,
        Priority = 0,
    };
}
