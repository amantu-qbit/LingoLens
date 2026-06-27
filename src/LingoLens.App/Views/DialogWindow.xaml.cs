using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LingoLens.App.Views;

/// <summary>Visual tone for an <see cref="DialogWindow"/> — drives the header glyph and its colour.</summary>
public enum DialogTone
{
    Info,
    Question,
    Warning,
    Danger,
}

/// <summary>
/// A small, on-brand modal used in place of the system <c>MessageBox</c> so confirmations and errors
/// match the rest of the app. Build one through <see cref="Services.AppDialog"/> rather than directly.
/// </summary>
public partial class DialogWindow : Window
{
    public DialogWindow(string title, string message, string primaryText, string? secondaryText, DialogTone tone)
    {
        InitializeComponent();

        TitleLabel.Text = title;
        MessageLabel.Text = message;
        PrimaryButton.Content = primaryText;

        if (string.IsNullOrEmpty(secondaryText))
            SecondaryButton.Visibility = Visibility.Collapsed;
        else
            SecondaryButton.Content = secondaryText;

        ApplyTone(tone);
    }

    private void ApplyTone(DialogTone tone)
    {
        (string glyph, string brushKey, string wellKey) = tone switch
        {
            DialogTone.Question => ("", "AccentBrush", "AccentSoftBrush"),
            DialogTone.Warning  => ("", "WarningBrush", "AccentSoftBrush"),
            DialogTone.Danger   => ("", "DangerBrush", "DangerSoftBrush"),
            _                   => ("", "AccentBrush", "AccentSoftBrush"),
        };

        IconGlyph.Text = glyph;
        if (TryBrush(brushKey, out var fg)) IconGlyph.Foreground = fg;
        if (TryBrush(wellKey, out var bg)) IconWell.Background = bg;
    }

    private bool TryBrush(string key, out Brush brush)
    {
        if (TryFindResource(key) is Brush b) { brush = b; return true; }
        brush = Brushes.Transparent;
        return false;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch { /* DragMove throws if the button was released mid-gesture; ignore */ }
    }
}
