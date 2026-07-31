using System.Windows;
using Microsoft.Win32;

namespace SpaceTree.App.Services;

/// <summary>Swaps the palette dictionary at index 0 of the application resources.</summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after the palette changes so owner-drawn controls can re-read their brushes.</summary>
    public static event EventHandler? ThemeChanged;

    public static bool IsDark => Resolve(Current) == AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        Current = theme;

        var app = Application.Current;
        if (app is null)
            return;

        var source = new Uri(
            Resolve(theme) == AppTheme.Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml",
            UriKind.Relative);

        var dictionaries = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = source };

        if (dictionaries.Count == 0)
            dictionaries.Add(palette);
        else
            dictionaries[0] = palette;

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Maps <see cref="AppTheme.System"/> onto whatever Windows is currently using.</summary>
    private static AppTheme Resolve(AppTheme theme)
    {
        if (theme != AppTheme.System)
            return theme;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // AppsUseLightTheme: 0 = dark, 1 = light. Missing means a build old
            // enough to predate the setting, which only had light.
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Locked-down policy: fall through to the default.
        }

        return AppTheme.Dark;
    }
}
