using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CelMap.Core;

namespace CelMap.App;

/// <summary>Colours the match-tier chip: certainties (Qualified/Exact/Alias) read
/// differently from a coincidental Fuzzy score.</summary>
public sealed class KindToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Qualified = new(Color.FromRgb(0x6D, 0x28, 0xD9)); // purple
    private static readonly SolidColorBrush Exact     = new(Color.FromRgb(0x15, 0x7A, 0x3C)); // green
    private static readonly SolidColorBrush Alias     = new(Color.FromRgb(0x0E, 0x6B, 0xA8)); // blue
    private static readonly SolidColorBrush Fuzzy     = new(Color.FromRgb(0x6B, 0x72, 0x80)); // grey

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MatchKind k
            ? k switch
            {
                MatchKind.Qualified => Qualified,
                MatchKind.Exact => Exact,
                MatchKind.Alias => Alias,
                _ => Fuzzy
            }
            : Fuzzy;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible when the bound string equals the converter parameter — used to switch a
/// target slot's body between its "Blank", "Picker" and "Preview" states.</summary>
public sealed class StringMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        string? s = parameter as string;
        if (s is not null && s.Contains("invert", StringComparison.OrdinalIgnoreCase))
            b = !b;
        // "hidden": reserve layout space when not visible, so showing/hiding never shifts siblings.
        var off = s is not null && s.Contains("hidden", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Hidden : Visibility.Collapsed;
        return b ? Visibility.Visible : off;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
