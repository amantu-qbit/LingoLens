using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingoLens.App.Services;
using LingoLens.App.Views;
using LingoLens.Core.Capture;
using LingoLens.Core.Compute;
using LingoLens.Core.Configuration;
using LingoLens.Core.Models;
using LingoLens.Core.Pipeline;
using LingoLens.Translation;
using LingoLens.Translation.Models;

namespace LingoLens.App.ViewModels;

/// <summary>Drives the floating control HUD: start/stop, target/device/language readouts, live metrics.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ITranslationPipeline _pipeline;
    private readonly IComputeDeviceManager _devices;
    private readonly LingoLensOptions _options;
    private readonly TargetEnumerator _targets;
    private readonly IModelRepository _models;
    private readonly TranslatorSelector _translator;
    private readonly IServiceProvider _sp;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    private CaptureTarget? _target;

    public MainViewModel(
        ITranslationPipeline pipeline,
        IComputeDeviceManager devices,
        LingoLensOptions options,
        TargetEnumerator targets,
        IModelRepository models,
        TranslatorSelector translator,
        IServiceProvider sp)
    {
        _pipeline = pipeline;
        _devices = devices;
        _options = options;
        _targets = targets;
        _models = models;
        _translator = translator;
        _sp = sp;

        _deviceName = devices.Selected.Name;
        _languagePair = $"{Languages.Name(options.Translation.SourceLanguage)} → {Languages.Name(options.Translation.TargetLanguage)}";
        _statusText = "Ready";
        _targetName = "Choose a target";

        _pipeline.StateChanged += OnStateChanged;
        _pipeline.MetricsUpdated += OnMetricsUpdated;
        _devices.SelectionChanged += (_, _) => Post(() => DeviceName = _devices.Selected.Name);
    }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _targetName;
    [ObservableProperty] private string _deviceName;
    [ObservableProperty] private string _languagePair;
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private string _cacheText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasIssue;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private double _downloadProgress;

    public string RunLabel => IsRunning ? "Stop" : "Translate";

    /// <summary>Show the live latency/cache readout only when running cleanly (no issue, no download).</summary>
    public bool ShowTelemetry => IsRunning && !HasIssue && !IsDownloading;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(RunLabel));
        OnPropertyChanged(nameof(ShowTelemetry));
    }

    partial void OnHasIssueChanged(bool value) => OnPropertyChanged(nameof(ShowTelemetry));
    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(ShowTelemetry));

    [RelayCommand]
    private async Task ToggleRunAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (_pipeline.State is PipelineState.Running or PipelineState.Starting)
            {
                await _pipeline.StopAsync();
                return;
            }

            if (_target is null)
            {
                PickTarget();
                if (_target is null) return;
            }

            // Translation needs a local model. If none is installed, offer the one-time download here
            // so the very first run actually translates instead of silently showing nothing.
            if (!IsTranslationModelInstalled() && !await EnsureTranslationModelAsync())
                return;

            await _pipeline.StartAsync(_target);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void PickTarget()
    {
        var picker = new TargetPickerWindow(_targets) { Owner = OwnerWindow };
        picker.ShowDialog();

        if (picker.DrawRegionRequested)
        {
            // The picker asked us to run the region selector; do it here (top-level, not nested).
            var selector = new RegionSelectorWindow { Owner = OwnerWindow };
            if (selector.ShowDialog() == true && selector.SelectedRegion is { } r && !r.IsEmpty)
                SetTarget(CaptureTarget.ForRegion(r, $"Region {r.Width}×{r.Height}"));
            return;
        }

        if (picker.SelectedTarget is { } t)
            SetTarget(t);
    }

    private void SetTarget(CaptureTarget target)
    {
        _target = target;
        TargetName = target.DisplayName ?? target.Mode.ToString();
    }

    private bool IsTranslationModelInstalled() =>
        _models.IsInstalled(DefaultModelManifest.OpusMtBundleId) ||
        _models.IsInstalled(DefaultModelManifest.Qwen3BundleId);

    /// <summary>
    /// Prompts for, downloads, and activates the default (Fast) translation model. Returns true when a
    /// model is ready to use, false if the user declined or the download failed.
    /// </summary>
    private async Task<bool> EnsureTranslationModelAsync()
    {
        var answer = MessageBox.Show(OwnerWindow,
            "LingoLens needs a translation model before it can translate.\n\n" +
            "Download the Fast model now? (Opus-MT Chinese→English, about 450 MB, one time.\n" +
            "It is saved on this PC and used entirely offline.)\n\n" +
            "You can also pick a higher-quality model later in Settings → Models.",
            "LingoLens — download translation model",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatus = "Preparing…";
        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            DownloadProgress = p.Fraction;
            DownloadStatus = $"Downloading model… {p.Fraction * 100:0}%";
        });

        try
        {
            await _models.EnsureInstalledAsync(DefaultModelManifest.OpusMtBundleId, progress);
            DownloadStatus = "Activating…";
            await _translator.ReloadAsync();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerWindow,
                "The model download did not finish:\n\n" + ex.Message +
                "\n\nCheck your connection and try again, or download it from Settings → Models.",
                "LingoLens", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = (SettingsViewModel)_sp.GetService(typeof(SettingsViewModel))!;
        var win = new SettingsWindow(vm) { Owner = OwnerWindow };
        win.ShowDialog();
        // reflect any language/device change made in settings
        LanguagePair = $"{Languages.Name(_options.Translation.SourceLanguage)} → {Languages.Name(_options.Translation.TargetLanguage)}";
        DeviceName = _devices.Selected.Name;
    }

    private Window? OwnerWindow => Application.Current.MainWindow;

    /// <summary>
    /// Marshals an update to the UI thread without blocking the (high-frequency) caller, and
    /// drops the update once the dispatcher is shutting down so we never throw during exit.
    /// </summary>
    private void Post(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _dispatcher.BeginInvoke(action);
    }

    private void OnStateChanged(object? sender, PipelineStateChangedEventArgs e) => Post(() =>
    {
        IsRunning = e.State is PipelineState.Running or PipelineState.Starting;
        StatusText = e.State switch
        {
            PipelineState.Running => e.Message ?? "Translating",
            PipelineState.Starting => "Starting…",
            PipelineState.Paused => "Paused",
            PipelineState.Error => e.Message ?? "Error",
            _ => "Ready",
        };
        // A non-null message while Running (or any Error) means something needs the user's attention —
        // surfaced prominently in the HUD instead of failing silently.
        HasIssue = e.State == PipelineState.Error || (e.State == PipelineState.Running && e.Message is not null);
    });

    private void OnMetricsUpdated(object? sender, PipelineMetricsEventArgs e) => Post(() =>
    {
        var m = e.Metrics;
        LatencyText = m.LastEndToEndMs > 0 ? $"{m.LastEndToEndMs:0} ms" : "—";
        CacheText = m.CacheHitRate > 0 ? $"cache {m.CacheHitRate * 100:0}%" : "";
    });
}
