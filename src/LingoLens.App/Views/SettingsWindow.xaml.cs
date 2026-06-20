using System.Windows;
using System.Windows.Input;
using LingoLens.App.ViewModels;

namespace LingoLens.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        // Drag the window from any empty area of its chrome. Interactive controls handle their own mouse
        // input, so this only fires for clicks on the background, labels and section spacing.
        if (e.ButtonState != MouseButtonState.Pressed || e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch { /* DragMove throws if the button was already released; ignore */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
