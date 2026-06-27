using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingoLens.App.Services;
using LingoLens.Core.Compute;
using LingoLens.Core.Configuration;
using LingoLens.Core.Models;
using LingoLens.Core.Overlay;
using LingoLens.Core.Pipeline;
using LingoLens.Core.Translation;
using LingoLens.Ocr;
using LingoLens.Translation;
using LingoLens.Translation.Models;

namespace LingoLens.App.ViewModels;

public sealed record DeviceItem(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Backs the Settings window: compute device, languages, overlay look, and glossary.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IComputeDeviceManager _devices;
    private readonly LingoLensOptions _options;
    private readonly IGlossary _glossary;
    private readonly ITranslationPipeline _pipeline;
    private readonly IModelRepository _models;
    private readonly TranslatorSelector _translator;

    public SettingsViewModel(
        IComputeDeviceManager devices,
        LingoLensOptions options,
        IGlossary glossary,
        ITranslationPipeline pipeline,
        IModelRepository models,
        TranslatorSelector translator)
    {
        _devices = devices;
        _options = options;
        _glossary = glossary;
        _pipeline = pipeline;
        _models = models;
        _translator = translator;

        DeviceItems = new ObservableCollection<DeviceItem>(
            new[] { new DeviceItem("auto", $"Auto ({devices.Selected.Name})") }
            .Concat(devices.AvailableDevices.Select(d =>
                new DeviceItem(d.Id, $"{d.Name} · {d.Provider} · {(d.VramBytes > 0 ? $"{d.VramBytes / (1024 * 1024 * 1024)} GB" : "—")}"))));
        _selectedDevice = devices.IsAuto ? DeviceItems[0] : DeviceItems.FirstOrDefault(d => d.Id == devices.Selected.Id, DeviceItems[0]);
        _tierText = devices.RecommendedTier.ToString();

        Sources = Languages.Sources;
        Targets = Languages.Targets;
        _selectedSource = Sources.FirstOrDefault(l => l.Code == options.Translation.SourceLanguage, Sources[0]);
        _selectedTarget = Targets.FirstOrDefault(l => l.Code == options.Translation.TargetLanguage, Targets[0]);

        OverlayStyles = Enum.GetValues<OverlayStyleKind>();
        _selectedStyle = options.Overlay.Style;
        _backplateOpacity = options.Overlay.BackplateOpacity;
        _autoContrast = options.Overlay.AutoContrast;
        _minFontScale = options.Overlay.MinFontScale;

        GlossaryEntries = new ObservableCollection<GlossaryEntry>(_glossary.Entries);

        Models = BuildModelCatalog();
    }

    /// <summary>Short product version (e.g. "v0.2.0") shown in the About card.</summary>
    public string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // ---- Models ----
    public ObservableCollection<ModelBundleViewModel> Models { get; }

    private ObservableCollection<ModelBundleViewModel> BuildModelCatalog()
    {
        // Translators hot-reload once installed; the OCR upgrade is picked up on the next launch.
        Func<CancellationToken, Task> reloadTranslator = ct => _translator.ReloadAsync(ct);

        return new ObservableCollection<ModelBundleViewModel>
        {
            new(_models, DefaultModelManifest.OpusMtBundleId,
                title: "Fast translator",
                subtitle: "Opus-MT Chinese → English. Light and quick; great default.",
                badge: "Fast", sizeBytes: 449_136_569L, appliesAfterRestart: false, activateAsync: reloadTranslator),

            new(_models, DefaultModelManifest.Qwen3BundleId,
                title: "Quality translator",
                subtitle: "Qwen3-4B. Best fluency and broad language coverage; larger download, GPU recommended.",
                badge: "Quality", sizeBytes: 2_497_281_312L, appliesAfterRestart: false, activateAsync: reloadTranslator),

            new(_models, OcrModelBundles.PpOcrV5Mobile,
                title: "High-accuracy text reader",
                subtitle: "PP-OCRv5 mobile. Sharper than the built-in reader for dense or stylised text.",
                badge: "OCR", sizeBytes: 22_543_402L, appliesAfterRestart: true, activateAsync: null),
        };
    }

    // ---- Compute ----
    public ObservableCollection<DeviceItem> DeviceItems { get; }
    [ObservableProperty] private DeviceItem _selectedDevice;
    [ObservableProperty] private string _tierText;

    partial void OnSelectedDeviceChanged(DeviceItem value)
    {
        if (value is null) return;
        _devices.Select(value.Id);
        _options.Compute.Device = value.Id;
        TierText = _devices.RecommendedTier.ToString();
    }

    // ---- Languages ----
    public IReadOnlyList<LanguageOption> Sources { get; }
    public IReadOnlyList<LanguageOption> Targets { get; }
    [ObservableProperty] private LanguageOption _selectedSource;
    [ObservableProperty] private LanguageOption _selectedTarget;

    partial void OnSelectedSourceChanged(LanguageOption value)
    {
        _options.Translation.SourceLanguage = value?.Code ?? "zh";
        ApplyLanguagePair();
    }

    partial void OnSelectedTargetChanged(LanguageOption value)
    {
        _options.Translation.TargetLanguage = value?.Code ?? "en";
        ApplyLanguagePair();
    }

    // Push the language change through to the running pipeline; the pipeline snapshots
    // its language pair, so mutating _options alone never reaches it.
    private void ApplyLanguagePair() =>
        _pipeline.Languages = new LanguagePair(_options.Translation.SourceLanguage, _options.Translation.TargetLanguage);

    // ---- Overlay ----
    public OverlayStyleKind[] OverlayStyles { get; }
    [ObservableProperty] private OverlayStyleKind _selectedStyle;
    [ObservableProperty] private double _backplateOpacity;
    [ObservableProperty] private bool _autoContrast;
    [ObservableProperty] private double _minFontScale;

    partial void OnSelectedStyleChanged(OverlayStyleKind value)
    {
        _options.Overlay.Style = value;
        OnPropertyChanged(nameof(SelectedStyleDescription));
    }

    /// <summary>One-line explanation of the selected overlay style, shown under the picker.</summary>
    public string SelectedStyleDescription => SelectedStyle switch
    {
        OverlayStyleKind.FloatingPanel => "Translations appear in a panel beside the original, which stays visible.",
        OverlayStyleKind.OnDemand => "Translations show only when you hover a line or press the hotkey.",
        _ => "English is drawn over the original text on a frosted, auto-fitting backplate.",
    };
    partial void OnBackplateOpacityChanged(double value) => _options.Overlay.BackplateOpacity = value;
    partial void OnAutoContrastChanged(bool value) => _options.Overlay.AutoContrast = value;
    partial void OnMinFontScaleChanged(double value) => _options.Overlay.MinFontScale = value;

    // ---- Glossary ----
    public ObservableCollection<GlossaryEntry> GlossaryEntries { get; }
    [ObservableProperty] private string _newGlossarySource = "";
    [ObservableProperty] private string _newGlossaryTarget = "";

    [RelayCommand]
    private void AddGlossary()
    {
        if (string.IsNullOrWhiteSpace(NewGlossarySource) || string.IsNullOrWhiteSpace(NewGlossaryTarget)) return;
        var pair = new LanguagePair(_options.Translation.SourceLanguage, _options.Translation.TargetLanguage);
        _glossary.AddOrUpdate(pair, NewGlossarySource, NewGlossaryTarget);
        GlossaryEntries.Add(new GlossaryEntry(pair, NewGlossarySource.Trim(), NewGlossaryTarget.Trim()));
        NewGlossarySource = "";
        NewGlossaryTarget = "";
    }

    [RelayCommand]
    private void RemoveGlossary(GlossaryEntry? entry)
    {
        if (entry is null) return;
        _glossary.Remove(entry.Pair, entry.Source);
        GlossaryEntries.Remove(entry);
    }
}
