using System.Windows;

namespace LingoLens.App.Ui;

/// <summary>
/// Attached property that gives a templated <see cref="System.Windows.Controls.TextBox"/> a watermark.
/// The styled TextBox template renders <see cref="TextProperty"/> behind the caret while the field is empty.
/// </summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder), new PropertyMetadata(string.Empty));

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);
}
