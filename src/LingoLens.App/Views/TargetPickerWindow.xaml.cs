using System.Windows;
using System.Windows.Input;
using LingoLens.App.Services;
using LingoLens.Core;
using LingoLens.Core.Capture;

namespace LingoLens.App.Views;

public partial class TargetPickerWindow : Window
{
    private readonly TargetEnumerator _targets;

    public CaptureTarget? SelectedTarget { get; private set; }

    public TargetPickerWindow(TargetEnumerator targets)
    {
        _targets = targets;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowsList.ItemsSource = _targets.EnumerateWindows();
        var monitors = _targets.EnumerateMonitors();
        MonitorsCombo.ItemsSource = monitors;
        MonitorsCombo.DisplayMemberPath = nameof(MonitorCandidate.Name);
        MonitorsCombo.SelectedItem = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnWindowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WindowsList.SelectedItem is WindowCandidate w) Commit(w.ToTarget());
    }

    private void OnUseWindow(object sender, RoutedEventArgs e)
    {
        if (WindowsList.SelectedItem is WindowCandidate w) Commit(w.ToTarget());
        else MessageBox.Show(this, "Select a window from the list first.", "LingoLens");
    }

    private void OnUseMonitor(object sender, RoutedEventArgs e)
    {
        if (MonitorsCombo.SelectedItem is MonitorCandidate m) Commit(m.ToTarget());
    }

    private void OnDrawRegion(object sender, RoutedEventArgs e)
    {
        Hide();
        var selector = new RegionSelectorWindow();
        bool? ok = selector.ShowDialog();
        if (ok == true && selector.SelectedRegion is { } r && !r.IsEmpty)
        {
            Commit(CaptureTarget.ForRegion(r, $"Region {r.Width}×{r.Height}"));
        }
        else
        {
            Show();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Commit(CaptureTarget target)
    {
        SelectedTarget = target;
        DialogResult = true;
        Close();
    }
}
