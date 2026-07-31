using System.Collections.Generic;
using SpaceTree.Core.Filtering;
using SpaceTree.Core.Model;
using SpaceTree.Core.Sorting;
using SpaceTree.Core.Util;

namespace SpaceTree.App.Services;

public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>
/// Everything the app remembers between runs. Plain properties with defaults so
/// that a missing or partially-corrupt settings file still yields a usable app.
/// </summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    // ── Window placement ──
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;

    // Nullable rather than NaN: System.Text.Json cannot write NaN or infinity,
    // and a throwing Save would abort whatever flow triggered it.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
    public double TreePaneWidth { get; set; } = 0.62;

    // ── Scanning ──
    public string? LastScanPath { get; set; }
    public bool RescanLastPathOnStart { get; set; } = true;
    public int ThreadCount { get; set; } = ScanOptions.DefaultThreadCount;
    public bool FollowReparsePoints { get; set; }
    public bool RetainFileEntries { get; set; } = true;

    // ── View ──
    public bool ShowFiles { get; set; } = true;
    public SizeUnitSystem Units { get; set; } = SizeUnitSystem.Binary;
    public SortColumn SortColumn { get; set; } = SortColumn.Size;
    public SortDirection SortDirection { get; set; } = SortDirection.Descending;
    public bool ShowSizeBars { get; set; } = true;
    public bool UseAllocatedForBars { get; set; }
    public int VisualizationTab { get; set; }

    // ── Filtering ──
    public FilterMode FilterMode { get; set; } = FilterMode.Wildcard;
    public long MinimumSize { get; set; }
    public bool HideEmptyFolders { get; set; }

    /// <summary>Column layout, keyed by the column id used in the view model.</summary>
    public List<ColumnSetting> Columns { get; set; } = new();

    /// <summary>Recently scanned roots, most recent first.</summary>
    public List<string> RecentPaths { get; set; } = new();

    public const int MaxRecentPaths = 12;

    public void PushRecentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        RecentPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentPaths.Insert(0, path);
        if (RecentPaths.Count > MaxRecentPaths)
            RecentPaths.RemoveRange(MaxRecentPaths, RecentPaths.Count - MaxRecentPaths);
    }
}

/// <summary>Persisted width, order and visibility for one tree column.</summary>
public sealed class ColumnSetting
{
    public string Id { get; set; } = string.Empty;
    public double Width { get; set; }
    public bool Visible { get; set; } = true;
    public int Order { get; set; }
}
