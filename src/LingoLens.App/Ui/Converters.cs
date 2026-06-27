using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LingoLens.App.Ui;

/// <summary>Bool → <see cref="Visibility"/>. Pass ConverterParameter="Invert" to flip the sense.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (IsInvert(parameter)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is Visibility.Visible;
        return IsInvert(parameter) ? !visible : visible;
    }

    private static bool IsInvert(object? parameter) =>
        parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Negates a boolean.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Null / empty / whitespace string → <see cref="Visibility"/>. By default an empty value is
/// <see cref="Visibility.Visible"/> (used for placeholders / empty-state hints). Pass
/// ConverterParameter="Invert" to show only when the string has content.
/// </summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isEmpty = string.IsNullOrWhiteSpace(value as string);
        bool invert = parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase);
        bool show = invert ? !isEmpty : isEmpty;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Item count → <see cref="Visibility"/>. Zero is Visible (empty-state); "Invert" flips it.</summary>
public sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value switch
        {
            int i => i,
            null => 0,
            _ => 1,
        };
        bool isZero = count == 0;
        bool invert = parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase);
        bool show = invert ? !isZero : isZero;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
