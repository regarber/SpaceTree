using System.Globalization;

namespace SpaceTree.Core.Util;

public enum SizeUnitSystem
{
    /// <summary>1 KB = 1024 bytes, labelled the way Windows Explorer labels it.</summary>
    Binary,
    /// <summary>1 kB = 1000 bytes, the way drive manufacturers count.</summary>
    Decimal,
}

/// <summary>Human-readable byte, count and duration formatting shared by the UI and the exporters.</summary>
public static class SizeFormatter
{
    private static readonly string[] BinaryUnits = { "bytes", "KB", "MB", "GB", "TB", "PB", "EB" };
    private static readonly string[] DecimalUnits = { "bytes", "kB", "MB", "GB", "TB", "PB", "EB" };

    /// <summary>Formats a byte count, e.g. "1.42 GB". Negative values are rendered with a sign.</summary>
    public static string Format(long bytes, SizeUnitSystem system = SizeUnitSystem.Binary)
    {
        if (bytes == 0)
            return "0 bytes";

        string sign = bytes < 0 ? "-" : string.Empty;
        double value = Math.Abs((double)bytes);
        double base_ = system == SizeUnitSystem.Binary ? 1024d : 1000d;
        string[] units = system == SizeUnitSystem.Binary ? BinaryUnits : DecimalUnits;

        int unit = 0;
        while (value >= base_ && unit < units.Length - 1)
        {
            value /= base_;
            unit++;
        }

        if (unit == 0)
            return string.Create(CultureInfo.CurrentCulture, $"{sign}{value:N0} {units[0]}");

        // Keep three significant digits: 9.87 MB, 98.7 MB, 987 MB.
        int decimals = value >= 100 ? 0 : value >= 10 ? 1 : 2;
        return sign + value.ToString("N" + decimals, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>Exact byte count with thousands separators, for tooltips and the properties dialog.</summary>
    public static string FormatExact(long bytes) =>
        bytes.ToString("N0", CultureInfo.CurrentCulture) + (Math.Abs(bytes) == 1 ? " byte" : " bytes");

    public static string FormatCount(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Formats a 0..1 fraction as a percentage, e.g. "12.4 %".</summary>
    public static string FormatPercent(double fraction)
    {
        if (double.IsNaN(fraction) || double.IsInfinity(fraction))
            return "-";
        double pct = fraction * 100d;
        int decimals = pct >= 100 ? 0 : pct >= 10 ? 1 : 2;
        return pct.ToString("N" + decimals, CultureInfo.CurrentCulture) + " %";
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 1)
            return $"{span.TotalMilliseconds:N0} ms";
        if (span.TotalMinutes < 1)
            return $"{span.TotalSeconds:N1} s";
        if (span.TotalHours < 1)
            return $"{span.Minutes}m {span.Seconds:00}s";
        return $"{(int)span.TotalHours}h {span.Minutes:00}m {span.Seconds:00}s";
    }

    public static string FormatDate(DateTime value) =>
        value == DateTime.MinValue ? string.Empty : value.ToString("g", CultureInfo.CurrentCulture);
}
