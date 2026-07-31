using System.Collections.Generic;
using SpaceTree.App.Infrastructure;
using SpaceTree.Core.Model;
using SpaceTree.Core.Native;
using SpaceTree.Core.Sorting;
using SpaceTree.Core.Util;

namespace SpaceTree.App.ViewModels;

/// <summary>
/// One line of the tree: a directory or a file.
///
/// Rows are created lazily — only for the scan root and for the children of
/// folders the user has actually expanded. A full system drive holds millions of
/// entries and materialising a view model for each would cost far more memory
/// than the scan itself. Everything displayed is computed from the underlying
/// node on demand, so a row costs a handful of fields plus its cached children.
/// </summary>
public sealed class TreeRowViewModel : ObservableObject
{
    private readonly TreeContext _context;
    private readonly DirectoryNode? _node;
    private readonly FileEntry _file;

    private List<TreeRowViewModel>? _children;
    private int _childrenVersion;
    private bool _isExpanded;
    private string? _cachedPath;

    /// <summary>Creates a folder row.</summary>
    public TreeRowViewModel(DirectoryNode node, TreeRowViewModel? parent, TreeContext context)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Parent = parent;
        Level = parent is null ? 0 : parent.Level + 1;
    }

    /// <summary>Creates a file row.</summary>
    public TreeRowViewModel(in FileEntry file, TreeRowViewModel parent, TreeContext context)
    {
        _file = file;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Level = parent.Level + 1;
        IsFile = true;
    }

    public TreeRowViewModel? Parent { get; }

    /// <summary>Indentation depth in the flattened list.</summary>
    public int Level { get; }

    public bool IsFile { get; }

    public bool IsFolder => !IsFile;

    /// <summary>The directory behind this row, or null for a file row.</summary>
    public DirectoryNode? Node => _node;

    public FileEntry File => _file;

    public bool IsRoot => Parent is null;

    // ── Values ──

    public string Name => IsFile ? _file.Name : DisplayName(_node!);

    public long Size => IsFile ? _file.Size : _node!.TotalSize;

    public long Allocated => IsFile ? _file.Allocated : _node!.TotalAllocated;

    public long? FileCount => IsFile ? null : _node!.TotalFileCount;

    public long? FolderCount => IsFile ? null : _node!.TotalDirectoryCount;

    public DateTime LastModified =>
        FileTimes.ToDateTimeLocal(IsFile ? _file.LastWriteFileTime : _node!.LastWriteFileTime);

    /// <summary>Share of the parent row's size, 0..1. The root row is always 1.</summary>
    public double PercentOfParent
    {
        get
        {
            var parent = Parent;
            if (parent is null)
                return 1d;

            long total = parent.Size;
            return total <= 0 ? 0d : Math.Clamp((double)Size / total, 0d, 1d);
        }
    }

    /// <summary>Width of the proportional bar behind the size cell, 0..1.</summary>
    public double BarFraction
    {
        get
        {
            if (!_context.ShowSizeBars)
                return 0d;

            var parent = Parent;
            if (parent is null)
                return 1d;

            long mine = _context.BarsUseAllocated ? Allocated : Size;
            long total = _context.BarsUseAllocated ? parent.Allocated : parent.Size;
            return total <= 0 ? 0d : Math.Clamp((double)mine / total, 0d, 1d);
        }
    }

    public bool HasError => !IsFile && _node!.HasError;

    public bool IsReparsePoint => IsFile ? _file.IsReparsePoint : _node!.IsReparsePoint;

    /// <summary>Segoe Fluent Icons / MDL2 glyph shown before the name.</summary>
    public string Glyph
    {
        get
        {
            if (IsFile)
                return "\uE8A5";      // Document
            if (HasError)
                return "\uE72E";      // Lock - folder could not be read
            if (IsReparsePoint)
                return "\uE71B";      // Link - junction or symbolic link
            return "\uE8B7";          // Folder
        }
    }
    /// <summary>Full path, cached because the context menu and exports ask for it repeatedly.</summary>
    public string FullPath =>
        _cachedPath ??= IsFile
            ? LongPath.Combine(Parent!.FullPath, _file.Name)
            : _node!.FullPath;

    // ── Formatted for display ──

    public string SizeText => SizeFormatter.Format(Size, _context.Units);
    public string AllocatedText => SizeFormatter.Format(Allocated, _context.Units);
    public string PercentText => SizeFormatter.FormatPercent(PercentOfParent);
    public string FilesText => FileCount is { } n ? SizeFormatter.FormatCount(n) : string.Empty;
    public string FoldersText => FolderCount is { } n ? SizeFormatter.FormatCount(n) : string.Empty;
    public string ModifiedText => SizeFormatter.FormatDate(LastModified);

    public string ToolTipText
    {
        get
        {
            var lines = new List<string>(6) { FullPath, string.Empty };
            lines.Add($"Size: {SizeFormatter.Format(Size, _context.Units)}  ({SizeFormatter.FormatExact(Size)})");
            lines.Add($"Allocated: {SizeFormatter.Format(Allocated, _context.Units)}");

            if (IsFolder)
                lines.Add($"Contains: {SizeFormatter.FormatCount(FileCount ?? 0)} files in {SizeFormatter.FormatCount(FolderCount ?? 0)} folders");

            if (HasError)
                lines.Add("Could not be read fully (access denied).");
            if (IsReparsePoint)
                lines.Add("Reparse point — contents are not counted here.");

            return string.Join(Environment.NewLine, lines);
        }
    }

    // ── Hierarchy ──

    /// <summary>True when the row can be expanded under the current filter.</summary>
    public bool IsExpandable
    {
        get
        {
            if (IsFile)
                return false;

            var node = _node!;
            if (node.Directories.Length > 0)
                return true;

            return _context.ShowFiles && node.Files.Length > 0;
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded && IsExpandable;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            Raise();
        }
    }

    /// <summary>Sort projection for this row.</summary>
    public RowKey Key => IsFile ? RowKey.FromFile(_file) : RowKey.FromDirectory(_node!);

    /// <summary>
    /// Child rows in display order, rebuilt when the sort/filter version moves on.
    ///
    /// A rebuild reuses the previous row object for a given directory so that the
    /// expansion state of anything below survives re-sorting and the live updates
    /// that arrive while a scan is still running.
    /// </summary>
    public IReadOnlyList<TreeRowViewModel> Children
    {
        get
        {
            if (_children is not null && _childrenVersion == _context.Version)
                return _children;

            _children = BuildChildren(_children);
            _childrenVersion = _context.Version;
            return _children;
        }
    }

    /// <summary>Drops cached children without forcing a rebuild, freeing memory on collapse-all.</summary>
    public void ReleaseChildren()
    {
        _children = null;
        _childrenVersion = 0;
    }

    private List<TreeRowViewModel> BuildChildren(List<TreeRowViewModel>? previous)
    {
        if (IsFile)
            return new List<TreeRowViewModel>(0);

        var node = _node!;
        var filter = _context.Filter;

        // Index the old rows so expansion state can be carried over.
        Dictionary<DirectoryNode, TreeRowViewModel>? reusable = null;
        if (previous is { Count: > 0 })
        {
            reusable = new Dictionary<DirectoryNode, TreeRowViewModel>(previous.Count);
            foreach (var row in previous)
                if (row._node is not null)
                    reusable[row._node] = row;
        }

        var dirs = node.Directories;
        var files = _context.ShowFiles ? node.Files : Array.Empty<FileEntry>();
        var result = new List<TreeRowViewModel>(dirs.Length + files.Length);

        for (int i = 0; i < dirs.Length; i++)
        {
            var child = dirs[i];
            if (!filter.IsFolderVisible(child))
                continue;

            result.Add(reusable is not null && reusable.TryGetValue(child, out var existing)
                ? existing
                : new TreeRowViewModel(child, this, _context));
        }

        for (int i = 0; i < files.Length; i++)
        {
            if (!filter.IsFileVisible(files[i]))
                continue;
            result.Add(new TreeRowViewModel(files[i], this, _context));
        }

        if (result.Count > 1)
        {
            var column = _context.SortColumn;
            var direction = _context.SortDirection;
            result.Sort((a, b) => NodeSort.Compare(a.Key, b.Key, column, direction));
        }

        return result;
    }

    /// <summary>Re-reads every displayed value. Used while a scan is filling the tree in.</summary>
    public void RefreshValues() => RaiseAll(
        nameof(Size), nameof(Allocated), nameof(FileCount), nameof(FolderCount),
        nameof(LastModified), nameof(PercentOfParent), nameof(BarFraction),
        nameof(SizeText), nameof(AllocatedText), nameof(PercentText),
        nameof(FilesText), nameof(FoldersText), nameof(ModifiedText),
        nameof(IsExpandable), nameof(Glyph), nameof(HasError));

    /// <summary>Walks from the root down to this row, root first.</summary>
    public IReadOnlyList<TreeRowViewModel> PathFromRoot()
    {
        var chain = new List<TreeRowViewModel>(Level + 1);
        for (TreeRowViewModel? row = this; row is not null; row = row.Parent)
            chain.Add(row);
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// The scan root shows its whole path, since "Windows" alone would not say
    /// which drive it came from; every other row shows just its own name.
    /// </summary>
    private static string DisplayName(DirectoryNode node) =>
        node.IsRoot ? LongPath.ToDisplay(node.Name) : node.Name;

    public override string ToString() => $"{Name} ({SizeText})";
}
