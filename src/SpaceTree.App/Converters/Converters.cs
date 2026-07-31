using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SpaceTree.App.Controls;
using SpaceTree.App.Services;

namespace SpaceTree.App.Converters;

/// <summary>Turns a 0..1 fraction into a star GridLength, for the filled part of a size bar.</summary>
public sealed class FractionToStarConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double d && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : 0;
        return new GridLength(Invert ? 1 - fraction : fraction, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Colours a size bar by how much of its parent the row takes up.
///
/// The hue sweeps from a calm blue for a small share to a warm orange for a
/// dominant one, so the folder eating the drive is identifiable from across the
/// room without reading a single number. Brushes are cached per bucket because
/// this runs for every visible row on every scroll.
/// </summary>
public sealed class BarBrushConverter : IValueConverter
{
    private const int Buckets = 32;

    private readonly Brush?[] _dark = new Brush?[Buckets + 1];
    private readonly Brush?[] _light = new Brush?[Buckets + 1];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double d && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : 0;
        if (fraction <= 0)
            return Brushes.Transparent;

        bool dark = ThemeManager.IsDark;
        var cache = dark ? _dark : _light;

        int bucket = (int)Math.Round(fraction * Buckets);
        var cached = cache[bucket];
        if (cached is not null)
            return cached;

        // Square-rooting spreads the interesting range: most rows sit well under
        // half of their parent, and a linear ramp would leave them all blue.
        double t = Math.Sqrt(bucket / (double)Buckets);
        double hue = 205 - 180 * t;

        var color = TreemapPalette.FromHsl(hue, dark ? 0.55 : 0.62, dark ? 0.42 : 0.62);
        var brush = new SolidColorBrush(Color.FromArgb(dark ? (byte)115 : (byte)150, color.R, color.G, color.B));
        brush.Freeze();

        cache[bucket] = brush;
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Converts a tree depth into the left margin of the name cell.</summary>
public sealed class LevelToIndentConverter : IValueConverter
{
    public double PerLevel { get; set; } = 16d;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int level = value is int i ? Math.Max(0, i) : 0;
        return new Thickness(level * PerLevel, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>True becomes Collapsed; used where the default converter reads backwards.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>
/// Collapses an element when its bound string is null or blank. With
/// <see cref="Invert"/> set it does the opposite, which is how the watermark
/// inside the filter box knows to disappear as soon as the user types.
/// </summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasText = !string.IsNullOrWhiteSpace(value as string);
        if (Invert)
            hasText = !hasText;
        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Collapses an element when the bound value is null.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is not null;
        if (Invert)
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Maps a chart slice index to its palette colour.</summary>
public sealed class IndexToSliceBrushConverter : IValueConverter
{
    private readonly Dictionary<(int, bool), Brush> _cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int index = value is int i ? i : 0;
        bool dark = ThemeManager.IsDark;

        if (_cache.TryGetValue((index, dark), out var cached))
            return cached;

        var brush = new SolidColorBrush(TreemapPalette.For(index, dark));
        brush.Freeze();
        _cache[(index, dark)] = brush;
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Multiplies a 0..1 fraction by a fixed width, for inline meters.</summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    public double MaxWidth { get; set; } = 120d;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double d && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : 0;
        return fraction * MaxWidth;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
