using SpaceTree.Core.Filtering;
using SpaceTree.Core.Model;
using SpaceTree.Core.Sorting;
using SpaceTree.Core.Util;

namespace SpaceTree.App.ViewModels;

/// <summary>
/// View state shared by every row: sort order, filter, units.
///
/// Rows read this by reference instead of holding their own copy, so changing a
/// sort order is one field write rather than a walk over the tree. <see cref="Version"/>
/// is the invalidation signal — a row whose cached children were built under an
/// older version rebuilds them on next use.
/// </summary>
public sealed class TreeContext
{
    private SortColumn _sortColumn = SortColumn.Size;
    private SortDirection _sortDirection = SortDirection.Descending;
    private FilterIndex _filter = FilterIndex.Build(new DirectoryNode("", null), NodeFilter.None);
    private bool _showFiles = true;

    /// <summary>Bumped whenever anything that changes child composition or order changes.</summary>
    public int Version { get; private set; } = 1;

    public SortColumn SortColumn
    {
        get => _sortColumn;
        set { if (_sortColumn != value) { _sortColumn = value; Invalidate(); } }
    }

    public SortDirection SortDirection
    {
        get => _sortDirection;
        set { if (_sortDirection != value) { _sortDirection = value; Invalidate(); } }
    }

    public FilterIndex Filter
    {
        get => _filter;
        set { _filter = value ?? throw new ArgumentNullException(nameof(value)); Invalidate(); }
    }

    public bool ShowFiles
    {
        get => _showFiles;
        set { if (_showFiles != value) { _showFiles = value; Invalidate(); } }
    }

    /// <summary>Display units. Does not affect ordering, so it does not invalidate caches.</summary>
    public SizeUnitSystem Units { get; set; } = SizeUnitSystem.Binary;

    /// <summary>Draw the proportional bars from allocated size rather than logical size.</summary>
    public bool BarsUseAllocated { get; set; }

    public bool ShowSizeBars { get; set; } = true;

    /// <summary>Forces every cached child list to be rebuilt on next access.</summary>
    public void Invalidate() => Version++;
}
