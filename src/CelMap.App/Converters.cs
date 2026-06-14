using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CelMap.Core;

namespace CelMap.App;

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

/// <summary>Source columns offered in a target's "Map" submenu: every non-empty, labeled
/// source that isn't already mapped elsewhere, plus the one currently mapped to THIS target
/// (so re-opening the menu still shows the active pick). As more targets get mapped, the
/// remaining options shrink.</summary>
public sealed class AvailableSourcesForRowConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is IEnumerable<SourceColumnViewModel> sources &&
            values[1] is MappingRowViewModel row)
        {
            int? currentIdx = row.LinkedSource?.ColumnIndex;
            return sources.Where(s =>
                !s.IsEmpty &&
                !string.IsNullOrWhiteSpace(s.Label) &&
                (!s.IsLinked || s.Column.ColumnIndex == currentIdx)
            ).ToList();
        }
        return Enumerable.Empty<SourceColumnViewModel>();
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ExcludeCurrentItemConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is IEnumerable<MappingRowViewModel> allRows && values[1] is MappingRowViewModel currentRow)
        {
            return allRows.Where(r => !ReferenceEquals(r, currentRow)).ToList();
        }
        return Enumerable.Empty<MappingRowViewModel>();
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible when the bound integer count is greater than zero; collapsed otherwise.
/// Used to hide tier chips (e.g. the weak-fuzzy badge) when nothing falls in that tier.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a mapped column's link index to one of 20 distinguishable font colours, so the
/// "→ target" / "← source" label strips that announce a mapping are colour-matched: the same
/// link pair reads in the same colour on both the source and target grids. The colour is keyed by
/// the source column index (modulo the palette) so a source keeps one colour across every target
/// it feeds. A negative/unmapped index yields the neutral muted colour. Colour applies to the font
/// only. The palette is mid-tone — saturated enough to stay legible as text, not pale washes.</summary>
public sealed class LinkIndexToBrushConverter : IValueConverter
{
    // 20 distinguishable hues, picked for legibility as label text (not pale fills).
    private static readonly SolidColorBrush[] Palette =
    {
        Make(0xC4, 0x33, 0x2D), Make(0xC9, 0x5A, 0x1B), Make(0xB7, 0x84, 0x06), Make(0x8A, 0x8F, 0x00),
        Make(0x4F, 0x8F, 0x1E), Make(0x18, 0x8A, 0x42), Make(0x10, 0x8B, 0x6F), Make(0x0E, 0x86, 0x97),
        Make(0x1B, 0x70, 0xB0), Make(0x2C, 0x4F, 0xC0), Make(0x4B, 0x3C, 0xC8), Make(0x6E, 0x33, 0xC0),
        Make(0x93, 0x2A, 0xB8), Make(0xB0, 0x27, 0x9E), Make(0xBC, 0x2A, 0x6E), Make(0x8A, 0x55, 0x2A),
        Make(0x5E, 0x7C, 0x4A), Make(0x3A, 0x6E, 0x8C), Make(0x7A, 0x6A, 0x2E), Make(0x6B, 0x4A, 0x8A),
    };

    private static readonly SolidColorBrush Unmapped = new(Color.FromRgb(0x6E, 0x80, 0x78)); // MutedText

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i && i >= 0)
            return Palette[i % Palette.Length];
        return Unmapped;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringToUpperConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToUpperInvariant();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}


