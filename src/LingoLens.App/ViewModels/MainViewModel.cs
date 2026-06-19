using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingoLens.App.Services;
using LingoLens.App.Views;
using LingoLens.Core.Capture;
using LingoLens.Core.Compute;
using LingoLens.Core.Configuration;
using LingoLens.Core.Pipeline;

namespace LingoLens.App.ViewModels;

/// <summary>Drives the floating control HUD: start/stop, target/device/language readouts, live metrics.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ITranslationPipeline _pipeline;
    private readonly IComputeDeviceManager _devices;
    private readonly LingoLensOptions _options;
    private readonly TargetEnumerator _targets;
    private readonly IServiceProvider _sp;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    private CaptureTarget? _target;

    public MainViewModel(
        ITranslationPipeline pipeline,
        IComputeDeviceManager devices,
        LingoLensOptions options,
        TargetEnumerator targets,
        IServiceProvider sp)
    {
        _pipeline = pipeline;
        _devices = devices;
        _options = options;
        _targets = targets;
        _sp = sp;

        _deviceName = devices.Selected.Name;
        _languagePair = $"{Languages.Name(options.Translation.SourceLanguage)} -> {Languages.Name(options.Translation.TargetLanguage)}";
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

    public string RunLabel => IsRunning ? "Stop" : "Translate";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(RunLabel));

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
            }
            else
            {
                if (_target is null)
                {
                    PickTarget();
                    if (_target is null) return;
                }
                await _pipeline.StartAsync(_target);
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void PickTarget()
    {
        var picker = new TargetPickerWindow(_targets) { Owner = OwnerWindow };
        if (picker.ShowDialog() == true && picker.SelectedTarget is { } t)
        {
            _target = t;
            TargetName = t.DisplayName ?? t.Mode.ToString();
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = (SettingsViewModel)_sp.GetService(typeof(SettingsViewModel))!;
        var win = new SettingsWindow(vm) { Owner = OwnerWindow };
        win.ShowDialog();
        // reflect any language/device change made in settings
        LanguagePair = $"{Languages.Name(_options.Translation.SourceLanguage)} -> {Languages.Name(_options.Translation.TargetLanguage)}";
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
            PipelineState.Running => "Translating",
            PipelineState.Starting => "Starting…",
            PipelineState.Paused => "Paused",
            PipelineState.Error => e.Message ?? "Error",
            _ => "Ready",
        };
    });

    private void OnMetricsUpdated(object? sender, PipelineMetricsEventArgs e) => Post(() =>
    {
        var m = e.Metrics;
        LatencyText = m.LastEndToEndMs > 0 ? $"{m.LastEndToEndMs:0} ms" : "—";
        CacheText = m.CacheHitRate > 0 ? $"cache {m.CacheHitRate * 100:0}%" : "";
    });
}
