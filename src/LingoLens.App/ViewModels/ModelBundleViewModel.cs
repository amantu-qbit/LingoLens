using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingoLens.Core.Models;

namespace LingoLens.App.ViewModels;

/// <summary>
/// One downloadable model bundle shown in Settings → Models. Wraps <see cref="IModelRepository"/> to
/// report install state, stream download progress, and (optionally) activate the model in the running
/// app once it lands — no restart required for translators.
/// </summary>
public sealed partial class ModelBundleViewModel : ObservableObject
{
    private readonly IModelRepository _models;
    private readonly Func<CancellationToken, Task>? _activateAsync;
    private CancellationTokenSource? _cts;

    public string BundleId { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Badge { get; }
    public string SizeText { get; }
    public bool AppliesAfterRestart { get; }

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";

    public ModelBundleViewModel(
        IModelRepository models,
        string bundleId,
        string title,
        string subtitle,
        string badge,
        long sizeBytes,
        bool appliesAfterRestart,
        Func<CancellationToken, Task>? activateAsync)
    {
        _models = models;
        BundleId = bundleId;
        Title = title;
        Subtitle = subtitle;
        Badge = badge;
        AppliesAfterRestart = appliesAfterRestart;
        _activateAsync = activateAsync;
        SizeText = FormatSize(sizeBytes);

        _isInstalled = models.IsInstalled(bundleId);
        _statusText = _isInstalled ? "Installed" : "";
    }

    /// <summary>Offer the download button: not installed and nothing in progress.</summary>
    public bool CanDownload => !IsInstalled && !IsDownloading;

    /// <summary>Show the installed confirmation: installed and not currently (re)downloading.</summary>
    public bool ShowInstalled => IsInstalled && !IsDownloading;

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(ShowInstalled));
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(ShowInstalled));
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsDownloading || IsInstalled) return;

        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Progress = 0;
        StatusText = "Starting…";

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            Progress = p.Fraction;
            StatusText = $"Downloading… {p.Fraction * 100:0}%";
        });

        try
        {
            await _models.EnsureInstalledAsync(BundleId, progress, _cts.Token);
            IsInstalled = true;

            if (_activateAsync is not null)
            {
                StatusText = "Activating…";
                await _activateAsync(CancellationToken.None);
            }

            StatusText = AppliesAfterRestart ? "Installed · restart to apply" : "Installed";
        }
        catch (OperationCanceledException)
        {
            // Cancelling your own download is benign — return the row to its idle "Download" state
            // rather than leaving an alarm-red "Cancelled" message behind.
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = "Failed — " + Truncate(ex.Message, 90);
        }
        finally
        {
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double mb = bytes / (1024d * 1024d);
        return mb >= 1024 ? $"{mb / 1024d:0.0} GB" : $"{mb:0} MB";
    }
}
