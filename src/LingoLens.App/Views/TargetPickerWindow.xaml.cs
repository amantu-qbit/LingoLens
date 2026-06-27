using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LingoLens.App.Services;
using LingoLens.Core.Capture;

namespace LingoLens.App.Views;

public partial class TargetPickerWindow : Window
{
    private readonly TargetEnumerator _targets;
    private readonly ObservableCollection<WindowCandidate> _windows = new();
    private ICollectionView? _view;
    private bool _loaded;

    /// <summary>The target the user committed to, or null if they cancelled.</summary>
    public CaptureTarget? SelectedTarget { get; private set; }

    /// <summary>
    /// True when the user asked to draw a screen region. The caller shows the region selector itself
    /// after this window closes — we deliberately do NOT open a nested dialog here (hiding a modal
    /// window and then setting <c>DialogResult</c> is what used to crash the app).
    /// </summary>
    public bool DrawRegionRequested { get; private set; }

    public TargetPickerWindow(TargetEnumerator targets)
    {
        _targets = targets;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Monitors are a cheap, synchronous lookup.
        var monitors = _targets.EnumerateMonitors();
        MonitorsCombo.ItemsSource = monitors;
        MonitorsCombo.DisplayMemberPath = nameof(MonitorCandidate.Name);
        MonitorsCombo.SelectedItem = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();

        // Windows are filtered live through a collection view.
        _view = CollectionViewSource.GetDefaultView(_windows);
        _view.Filter = FilterWindow;
        WindowsList.ItemsSource = _view;

        // Enumeration (process list + per-window icons) runs off the UI thread so the picker is instant.
        try
        {
            var windows = await _targets.EnumerateWindowsAsync();
            foreach (var w in windows) _windows.Add(w);
        }
        catch { /* best-effort; the screen/region paths still work */ }
        finally
        {
            _loaded = true;
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (_windows.Count > 0) WindowsList.SelectedIndex = 0;
            UpdateEmptyState();
            SearchBox.Focus();
        }
    }

    private bool FilterWindow(object item)
    {
        if (item is not WindowCandidate w) return false;
        var q = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return w.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || w.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        if (_view is not null && WindowsList.SelectedItem is null && !_view.IsEmpty)
            WindowsList.SelectedIndex = 0;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (!_loaded) return;
        bool any = _view is not null && !_view.IsEmpty;
        EmptyPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        EmptyLabel.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? "No capturable windows found."
            : "No windows match your search.";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Enter from the search box commits the selected (or first) window.
        if (e.Key == Key.Enter && SearchBox.IsKeyboardFocused)
        {
            CommitSelectedOrFirst();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && WindowsList.SelectedItem is WindowCandidate w)
        {
            Commit(w.ToTarget());
            e.Handled = true;
        }
    }

    private void OnWindowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WindowsList.SelectedItem is WindowCandidate w) Commit(w.ToTarget());
    }

    private void OnUseWindow(object sender, RoutedEventArgs e) => CommitSelectedOrFirst(prompt: true);

    private void CommitSelectedOrFirst(bool prompt = false)
    {
        if (WindowsList.SelectedItem is WindowCandidate selected)
        {
            Commit(selected.ToTarget());
            return;
        }

        var first = _view?.Cast<object>().OfType<WindowCandidate>().FirstOrDefault();
        if (first is not null)
        {
            Commit(first.ToTarget());
            return;
        }

        if (prompt)
            AppDialog.Notify(this, "Pick a window",
                "Select a window from the list, or capture a screen or region below.");
    }

    private void OnUseMonitor(object sender, RoutedEventArgs e)
    {
        if (MonitorsCombo.SelectedItem is MonitorCandidate m) Commit(m.ToTarget());
    }

    private void OnDrawRegion(object sender, RoutedEventArgs e)
    {
        // Hand the region flow back to the caller instead of nesting a dialog inside this one.
        DrawRegionRequested = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        // Drag from any empty area; interactive controls handle their own clicks.
        if (e.ButtonState != MouseButtonState.Pressed || e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch { /* DragMove throws if the button was already released; ignore */ }
    }

    private void Commit(CaptureTarget target)
    {
        SelectedTarget = target;
        Close();
    }
}
