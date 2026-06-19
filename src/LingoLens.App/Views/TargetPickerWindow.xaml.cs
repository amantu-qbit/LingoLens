using System.Windows;
using System.Windows.Input;
using LingoLens.App.Services;
using LingoLens.Core;
using LingoLens.Core.Capture;

namespace LingoLens.App.Views;

public partial class TargetPickerWindow : Window
{
    private readonly TargetEnumerator _targets;

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
        // Hand the region flow back to the caller instead of nesting a dialog inside this one.
        DrawRegionRequested = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Commit(CaptureTarget target)
    {
        SelectedTarget = target;
        Close();
    }
}
