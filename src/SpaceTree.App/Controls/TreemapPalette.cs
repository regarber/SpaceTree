using System.Windows.Media;

namespace SpaceTree.App.Controls;

/// <summary>
/// Categorical colours for treemap tiles and chart wedges.
///
/// The hues are spaced far enough apart to stay distinguishable side by side and
/// deliberately avoid pure red, which is reserved for warnings elsewhere in the
/// UI. Lightness is adjusted per theme so tiles keep the same identity against a
/// dark or a light plate.
/// </summary>
public static class TreemapPalette
{
    private static readonly double[] Hues =
    {
        210, 28, 150, 280, 45, 190, 330, 100, 255, 15, 170, 310,
    };

    /// <summary>Base colour for the item at <paramref name="index"/> in a sibling group.</summary>
    public static Color For(int index, bool dark)
    {
        double hue = Hues[((index % Hues.Length) + Hues.Length) % Hues.Length];
        return dark
            ? FromHsl(hue, 0.52, 0.46)
            : FromHsl(hue, 0.62, 0.62);
    }

    /// <summary>
    /// Shades a base colour for nesting depth. Deeper tiles sit on top of their
    /// parent, so they are shifted slightly to read as "inside" rather than as a
    /// different category.
    ///
    /// The shift is kept small and saturation is left alone: an earlier version
    /// lightened by 0.055 per level and desaturated as it went, which meant that
    /// by the fourth level every branch had converged on the same pale beige and
    /// the categorical colouring — the whole point of the view — was gone.
    /// </summary>
    public static Color AtDepth(Color baseColor, int depth, bool dark)
    {
        if (depth <= 0)
            return baseColor;

        double step = Math.Min(depth, 5) * (dark ? 0.028 : -0.026);
        ToHsl(baseColor, out double h, out double s, out double l);
        return FromHsl(h, s, Math.Clamp(l + step, 0.12, 0.86));
    }

    public static Color Highlight(Color baseColor, bool dark)
    {
        ToHsl(baseColor, out double h, out double s, out double l);
        return FromHsl(h, Math.Min(1, s + 0.15), Math.Clamp(l + (dark ? 0.16 : 0.12), 0, 1));
    }

    public static Color FromHsl(double hueDegrees, double saturation, double lightness)
    {
        double h = ((hueDegrees % 360) + 360) % 360 / 360d;
        double s = Math.Clamp(saturation, 0, 1);
        double l = Math.Clamp(lightness, 0, 1);

        if (s <= 0)
        {
            byte g = (byte)Math.Round(l * 255);
            return Color.FromRgb(g, g, g);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        return Color.FromRgb(
            (byte)Math.Round(HueToRgb(p, q, h + 1d / 3d) * 255),
            (byte)Math.Round(HueToRgb(p, q, h) * 255),
            (byte)Math.Round(HueToRgb(p, q, h - 1d / 3d) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1d / 6d) return p + (q - p) * 6 * t;
        if (t < 1d / 2d) return q;
        if (t < 2d / 3d) return p + (q - p) * (2d / 3d - t) * 6;
        return p;
    }

    private static void ToHsl(Color color, out double h, out double s, out double l)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));

        l = (max + min) / 2;

        if (Math.Abs(max - min) < 1e-9)
        {
            h = 0;
            s = 0;
            return;
        }

        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        if (Math.Abs(max - r) < 1e-9)
            h = ((g - b) / d + (g < b ? 6 : 0)) * 60;
        else if (Math.Abs(max - g) < 1e-9)
            h = ((b - r) / d + 2) * 60;
        else
            h = ((r - g) / d + 4) * 60;
    }

    /// <summary>Picks black or white text for legibility on a given fill.</summary>
    public static Color TextOn(Color background)
    {
        // Rec. 709 luma: matches how bright the eye actually perceives the fill.
        double luma = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255d;
        return luma > 0.55 ? Color.FromRgb(20, 20, 24) : Color.FromRgb(250, 250, 252);
    }
}
