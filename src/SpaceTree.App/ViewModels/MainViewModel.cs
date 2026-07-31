using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using SpaceTree.App.Infrastructure;
using SpaceTree.App.Services;
using SpaceTree.Core.Export;
using SpaceTree.Core.Filtering;
using SpaceTree.Core.Model;
using SpaceTree.Core.Native;
using SpaceTree.Core.Scanning;
using SpaceTree.Core.Sorting;
using SpaceTree.Core.Util;

namespace SpaceTree.App.ViewModels;

/// <summary>One wedge of the top-level share chart.</summary>
public sealed record ChartSlice(string Name, long Size, double Fraction, DirectoryNode? Node);

public sealed class MainViewModel : ObservableObject
{
    /// <summary>
    /// How often the live tree is refreshed while a scan runs. Fast enough to
    /// feel like a live readout, slow enough that re-sorting the visible rows
    /// never competes with the scanner for CPU.
    /// </summary>
    private static readonly TimeSpan LiveInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Minimum gap between filter-index rebuilds during a scan. The index is an
    /// O(tree) sweep, so rebuilding it on every live tick would slow the scan it
    /// is meant to be describing.
    /// </summary>
    private static readonly TimeSpan FilterRebuildInterval = TimeSpan.FromMilliseconds(750);

    private readonly SettingsService _settingsService;
    private readonly TreeContext _context = new();
    private readonly RowCollection _rows = new();
    private readonly DispatcherTimer _liveTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private IDialogService? _dialogs;
    private DirectoryScanner? _scanner;
    private CancellationTokenSource? _cancellation;
    private ScanResult? _result;
    private TreeRowViewModel? _rootRow;
    private object? _progressBox;
    private TimeSpan _lastFilterBuild;

    private string _selectedPath = string.Empty;
    private string _filterText = string.Empty;
    private bool _isScanning;
    private TreeRowViewModel? _selectedRow;
    private DirectoryNode? _treemapRoot;
    private ScanProgress _progress;

    public MainViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        var s = settingsService.Current;
        _context.SortColumn = s.SortColumn;
        _context.SortDirection = s.SortDirection;
        _context.ShowFiles = s.ShowFiles;
        _context.Units = s.Units;
        _context.ShowSizeBars = s.ShowSizeBars;
        _context.BarsUseAllocated = s.UseAllocatedForBars;
        _selectedPath = s.LastScanPath ?? string.Empty;

        _liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LiveInterval };
        _liveTimer.Tick += (_, _) => OnLiveTick();

        Drives = new ObservableCollection<DriveSummary>(VolumeService.GetDrives().Where(d => d.IsReady));
        RecentPaths = new ObservableCollection<string>(s.RecentPaths);
        ChartSlices = new ObservableCollection<ChartSlice>();

        ScanCommand = new RelayCommand(() => StartScan(SelectedPath), () => !IsScanning && SelectedPath.Length > 0);
        CancelCommand = new RelayCommand(CancelScan, () => IsScanning);
        RefreshCommand = new RelayCommand(() => StartScan(_result?.RootPath ?? SelectedPath), () => !IsScanning);
        BrowseCommand = new RelayCommand(Browse, () => !IsScanning);
        ScanDriveCommand = new RelayCommand(p => { if (p is DriveSummary d) StartScan(d.Name); }, _ => !IsScanning);

        ToggleExpandCommand = new RelayCommand(p => ToggleExpansion(p as TreeRowViewModel));
        ClearFilterCommand = new RelayCommand(() => FilterText = string.Empty);
        CollapseAllCommand = new RelayCommand(CollapseAll, () => _rootRow is not null);
        ExpandBranchCommand = new RelayCommand(() => ExpandBranch(SelectedRow ?? _rootRow), () => _rootRow is not null);

        OpenCommand = new RelayCommand(() => RunShell(p => ShellService.Open(p)), HasSelection);
        ShowInExplorerCommand = new RelayCommand(() => RunShell(p => ShellService.ShowInExplorer(p)), HasSelection);
        CopyPathCommand = new RelayCommand(() => RunShell(p => ShellService.CopyText(p)), HasSelection);
        PropertiesCommand = new RelayCommand(
            () => RunShell(p => ShellService.ShowProperties(p, _dialogs?.OwnerHandle ?? IntPtr.Zero)), HasSelection);
        DeleteCommand = new RelayCommand(DeleteSelected, () => HasSelection() && !IsScanning);

        ExportCsvCommand = new RelayCommand(ExportCsv, HasResult);
        ExportTextCommand = new RelayCommand(ExportText, HasResult);
        ExportHtmlCommand = new RelayCommand(ExportHtml, HasResult);
        PrintCommand = new RelayCommand(Print, HasResult);
        ShowErrorsCommand = new RelayCommand(
            () => _dialogs?.ShowErrors(_result?.Errors ?? Array.Empty<ScanError>()),
            () => _result is { Errors.Count: > 0 });

        ZoomInCommand = new RelayCommand(ZoomIn, () => SelectedRow is { IsFolder: true, IsExpandable: true });
        ZoomOutCommand = new RelayCommand(ZoomOut, () => _treemapRoot?.Parent is not null);
        ZoomResetCommand = new RelayCommand(() => TreemapRoot = _result?.Root ?? _scanner?.Root, HasResult);

        RestartElevatedCommand = new RelayCommand(RestartElevated, () => !ElevationService.IsElevated);

        ApplyFilter();
    }

    /// <summary>Connected by the window once it exists.</summary>
    public void AttachDialogs(IDialogService dialogs) => _dialogs = dialogs;

    // ═════════════════════════ Exposed state ═════════════════════════

    public RowCollection Rows => _rows;

    public TreeContext Context => _context;

    public ObservableCollection<DriveSummary> Drives { get; }

    public ObservableCollection<string> RecentPaths { get; }

    public ObservableCollection<ChartSlice> ChartSlices { get; }

    public AppSettings Settings => _settingsService.Current;

    public ScanResult? Result => _result;

    public bool IsElevated => ElevationService.IsElevated;

    public IReadOnlyList<int> ThreadCountOptions { get; } =
        new[] { 1, 2, 4, 6, 8, 12, 16, 24, 32 }.Where(n => n <= 64).ToArray();

    /// <summary>Raised when a row should be brought into view.</summary>
    public event Action<TreeRowViewModel>? ScrollRequested;

    /// <summary>Raised when the tree structure changed enough that the view should re-sync.</summary>
    public event EventHandler? TreeChanged;

    public string SelectedPath
    {
        get => _selectedPath;
        set
        {
            if (Set(ref _selectedPath, value ?? string.Empty))
                ScanCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value))
                return;
            Raise(nameof(IsIdle));
            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            BrowseCommand.RaiseCanExecuteChanged();
            ScanDriveCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsIdle => !_isScanning;

    public TreeRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value))
                return;

            OpenCommand.RaiseCanExecuteChanged();
            ShowInExplorerCommand.RaiseCanExecuteChanged();
            CopyPathCommand.RaiseCanExecuteChanged();
            PropertiesCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            ZoomInCommand.RaiseCanExecuteChanged();
            Raise(nameof(SelectionSummary));
        }
    }

    public string SelectionSummary
    {
        get
        {
            var row = _selectedRow;
            if (row is null)
                return string.Empty;

            string size = SizeFormatter.Format(row.Size, _context.Units);
            return row.IsFile
                ? $"{row.Name} — {size}"
                : $"{row.Name} — {size}, {SizeFormatter.FormatCount(row.FileCount ?? 0)} files";
        }
    }

    /// <summary>The folder the treemap and the chart are currently showing.</summary>
    public DirectoryNode? TreemapRoot
    {
        get => _treemapRoot;
        set
        {
            if (!Set(ref _treemapRoot, value))
                return;
            RaiseAll(nameof(TreemapRootPath), nameof(TreemapRootSizeText));
            ZoomOutCommand.RaiseCanExecuteChanged();
            RebuildChart();
        }
    }

    public string TreemapRootPath => _treemapRoot is null ? string.Empty : LongPath.ToDisplay(_treemapRoot.FullPath);

    /// <summary>Short total for the middle of the donut chart.</summary>
    public string TreemapRootSizeText =>
        _treemapRoot is null ? string.Empty : SizeFormatter.Format(_treemapRoot.TotalSize, _context.Units);

    // ═════════════════════════ View options ═════════════════════════

    public bool ShowFiles
    {
        get => _context.ShowFiles;
        set
        {
            if (_context.ShowFiles == value)
                return;
            _context.ShowFiles = value;
            _settingsService.Current.ShowFiles = value;
            Raise();
            RebuildTree();
        }
    }

    public bool ShowSizeBars
    {
        get => _context.ShowSizeBars;
        set
        {
            if (_context.ShowSizeBars == value)
                return;
            _context.ShowSizeBars = value;
            _settingsService.Current.ShowSizeBars = value;
            Raise();
            RefreshAllValues();
        }
    }

    public bool BarsUseAllocated
    {
        get => _context.BarsUseAllocated;
        set
        {
            if (_context.BarsUseAllocated == value)
                return;
            _context.BarsUseAllocated = value;
            _settingsService.Current.UseAllocatedForBars = value;
            Raise();
            RefreshAllValues();
        }
    }

    public bool UseBinaryUnits
    {
        get => _context.Units == SizeUnitSystem.Binary;
        set
        {
            var units = value ? SizeUnitSystem.Binary : SizeUnitSystem.Decimal;
            if (_context.Units == units)
                return;
            _context.Units = units;
            _settingsService.Current.Units = units;
            Raise();
            RefreshAllValues();
            RaiseStatus();
        }
    }

    public int ThreadCount
    {
        get => _settingsService.Current.ThreadCount;
        set
        {
            int clamped = Math.Clamp(value, 1, 64);
            if (_settingsService.Current.ThreadCount == clamped)
                return;
            _settingsService.Current.ThreadCount = clamped;
            Raise();
        }
    }

    public bool FollowReparsePoints
    {
        get => _settingsService.Current.FollowReparsePoints;
        set
        {
            if (_settingsService.Current.FollowReparsePoints == value)
                return;
            _settingsService.Current.FollowReparsePoints = value;
            Raise();
        }
    }

    // ═════════════════════════ Filtering ═════════════════════════

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!Set(ref _filterText, value ?? string.Empty))
                return;
            Raise(nameof(IsFilterActive));
            ApplyFilter();
            RebuildTree();
        }
    }

    public bool IsFilterActive => _context.Filter.IsActive;

    public bool UseRegexFilter
    {
        get => _settingsService.Current.FilterMode == FilterMode.Regex;
        set
        {
            var mode = value ? FilterMode.Regex : FilterMode.Wildcard;
            if (_settingsService.Current.FilterMode == mode)
                return;
            _settingsService.Current.FilterMode = mode;
            Raise();
            ApplyFilter();
            RebuildTree();
        }
    }

    public bool HideEmptyFolders
    {
        get => _settingsService.Current.HideEmptyFolders;
        set
        {
            if (_settingsService.Current.HideEmptyFolders == value)
                return;
            _settingsService.Current.HideEmptyFolders = value;
            Raise();
            ApplyFilter();
            RebuildTree();
        }
    }

    /// <summary>Minimum size in megabytes, as typed into the filter bar.</summary>
    public double MinimumSizeMb
    {
        get => _settingsService.Current.MinimumSize / (1024d * 1024d);
        set
        {
            long bytes = value <= 0 ? 0 : (long)(value * 1024 * 1024);
            if (_settingsService.Current.MinimumSize == bytes)
                return;
            _settingsService.Current.MinimumSize = bytes;
            Raise();
            ApplyFilter();
            RebuildTree();
        }
    }

    public bool HasInvalidFilterPattern => _context.Filter.Filter.HasInvalidPattern;

    private void ApplyFilter()
    {
        var s = _settingsService.Current;
        var filter = NodeFilter.Create(_filterText, s.FilterMode, s.MinimumSize, s.HideEmptyFolders);

        var root = _result?.Root ?? _scanner?.Root;
        _context.Filter = root is null
            ? FilterIndex.Build(new DirectoryNode(string.Empty, null), filter)
            : FilterIndex.Build(root, filter);

        _lastFilterBuild = _clock.Elapsed;
        RaiseAll(nameof(IsFilterActive), nameof(HasInvalidFilterPattern));
    }

    // ═════════════════════════ Sorting ═════════════════════════

    public SortColumn SortColumn => _context.SortColumn;

    public SortDirection SortDirection => _context.SortDirection;

    /// <summary>
    /// Applies a sort. Clicking the active column flips it; clicking a new one
    /// starts descending for the numeric columns, because "biggest first" is what
    /// someone reaching for a size column almost always wants.
    /// </summary>
    public void SortBy(SortColumn column)
    {
        if (_context.SortColumn == column)
        {
            _context.SortDirection = _context.SortDirection == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending;
        }
        else
        {
            _context.SortColumn = column;
            _context.SortDirection = column == SortColumn.Name
                ? SortDirection.Ascending
                : SortDirection.Descending;
        }

        _settingsService.Current.SortColumn = _context.SortColumn;
        _settingsService.Current.SortDirection = _context.SortDirection;

        RaiseAll(nameof(SortColumn), nameof(SortDirection));
        RebuildTree();
    }

    // ═════════════════════════ Scanning ═════════════════════════

    public void StartScan(string? path)
    {
        if (IsScanning)
            return;

        path = LongPath.NormalizeRoot(path ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path))
        {
            _dialogs?.ShowMessage("Nothing to scan", "Choose a drive or a folder first.");
            return;
        }

        if (!Directory.Exists(path))
        {
            _dialogs?.ShowMessage("Path not found", $"'{path}' is not a folder that can be scanned.", isError: true);
            return;
        }

        SelectedPath = path;

        // Fire-and-forget, but never unobserved: a fault after the await would
        // otherwise vanish silently and leave the UI half-updated.
        _ = RunScanAsync(path).ContinueWith(
            t => ReportBackgroundFailure(t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ReportBackgroundFailure(AggregateException? exception)
    {
        if (exception is null)
            return;

        CrashLog.Write(exception);
        _dialogs?.ShowMessage("Scan error",
            $"The scan finished but the results could not be shown.\n\n{exception.GetBaseException().Message}",
            isError: true);
    }

    private async Task RunScanAsync(string path)
    {
        var scanner = new DirectoryScanner();
        var cancellation = new CancellationTokenSource();

        _scanner = scanner;
        _cancellation = cancellation;
        _result = null;
        _rootRow = null;
        _progressBox = null;
        _progress = default;
        _rows.Clear();
        SelectedRow = null;
        TreemapRoot = null;
        ChartSlices.Clear();

        // The event arrives on a scanner thread; boxing the snapshot into a
        // single reference field makes the hand-off to the UI timer atomic
        // without a lock on the scanner's hot path.
        scanner.ProgressChanged += (_, progress) => _progressBox = progress;

        var options = new ScanOptions
        {
            RootPath = path,
            ThreadCount = _settingsService.Current.ThreadCount,
            FollowReparsePoints = _settingsService.Current.FollowReparsePoints,
            RetainFileEntries = _settingsService.Current.RetainFileEntries,
        };

        IsScanning = true;
        RaiseStatus();
        _liveTimer.Start();

        ScanResult? result = null;
        string? failure = null;

        try
        {
            result = await scanner.ScanAsync(options, cancellation.Token).ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = e.Message;
            CrashLog.Write(e);
        }
        finally
        {
            _liveTimer.Stop();
            IsScanning = false;
            cancellation.Dispose();
            _cancellation = null;
        }

        if (failure is not null)
        {
            _dialogs?.ShowMessage("Scan failed", failure, isError: true);
            return;
        }

        _result = result;
        OnScanCompleted();
    }

    public void CancelScan() => _cancellation?.Cancel();

    private void OnScanCompleted()
    {
        var result = _result;
        if (result is null)
            return;

        EnsureRootRow(result.Root);
        ApplyFilter();
        _context.Invalidate();

        if (_rootRow is not null)
            _rootRow.IsExpanded = true;

        RebuildRows();

        TreemapRoot = result.Root;

        // The live tick already pointed the visualisations at this very node, so
        // the property setter sees no change and skips its refresh. The numbers
        // behind the node grew all through the scan, though, so the chart and the
        // donut total have to be recomputed explicitly or they keep showing a
        // half-finished snapshot.
        RebuildChart();
        RaiseAll(nameof(TreemapRootPath), nameof(TreemapRootSizeText));

        var settings = _settingsService.Current;
        settings.LastScanPath = result.RootPath;
        settings.PushRecentPath(result.RootPath);
        _settingsService.Save();

        RecentPaths.Clear();
        foreach (var recent in settings.RecentPaths)
            RecentPaths.Add(recent);

        RaiseStatus();
        RaiseCommandStates();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drives the live tree and the progress readout while a scan runs.</summary>
    private void OnLiveTick()
    {
        if (_progressBox is ScanProgress snapshot)
            _progress = snapshot;

        var liveRoot = _scanner?.Root;
        if (liveRoot is not null)
        {
            if (_rootRow is null)
            {
                EnsureRootRow(liveRoot);
                if (_rootRow is not null)
                    _rootRow.IsExpanded = true;
                TreemapRoot = liveRoot;
            }

            // The name filter classifies whole subtrees, so its index goes stale
            // as new folders appear. Rebuilding it costs a sweep of the tree,
            // hence the throttle rather than a rebuild per tick.
            if (_context.Filter.Filter.HasNameFilter &&
                _clock.Elapsed - _lastFilterBuild > FilterRebuildInterval)
            {
                ApplyFilter();
            }

            _context.Invalidate();
            RebuildRows();
            RefreshAllValues();

            // Keep the treemap and chart alive during the scan too, not just the
            // tree — watching the big consumers emerge is half the point.
            RebuildChart();
            RaiseAll(nameof(TreemapRootPath), nameof(TreemapRootSizeText));
            TreeChanged?.Invoke(this, EventArgs.Empty);
        }

        RaiseProgress();
        RaiseStatus();
    }

    private void EnsureRootRow(DirectoryNode root)
    {
        if (_rootRow is not null && ReferenceEquals(_rootRow.Node, root))
            return;
        _rootRow = new TreeRowViewModel(root, null, _context);
    }

    // ═════════════════════════ Tree maintenance ═════════════════════════

    /// <summary>Rebuilds cached children and the flat row list from scratch.</summary>
    public void RebuildTree()
    {
        _context.Invalidate();
        RebuildRows();
        RebuildChart();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildRows()
    {
        if (_rootRow is null)
        {
            if (_rows.Count > 0)
                _rows.Clear();
            return;
        }

        var flat = new List<TreeRowViewModel>(Math.Max(16, _rows.Count));
        Flatten(_rootRow, flat);
        _rows.Patch(flat);
    }

    private static void Flatten(TreeRowViewModel row, List<TreeRowViewModel> into)
    {
        into.Add(row);
        if (!row.IsExpanded)
            return;

        var children = row.Children;
        for (int i = 0; i < children.Count; i++)
            Flatten(children[i], into);
    }

    public void Expand(TreeRowViewModel? row)
    {
        if (row is null || row.IsExpanded || !row.IsExpandable)
            return;

        row.IsExpanded = true;

        int index = _rows.IndexOf(row);
        if (index < 0)
            return;

        var added = new List<TreeRowViewModel>();
        var children = row.Children;
        for (int i = 0; i < children.Count; i++)
            Flatten(children[i], added);

        _rows.InsertRange(index + 1, added);
    }

    public void Collapse(TreeRowViewModel? row)
    {
        if (row is null || !row.IsExpanded)
            return;

        int index = _rows.IndexOf(row);
        int visible = CountVisibleDescendants(row);

        row.IsExpanded = false;

        if (index >= 0 && visible > 0)
            _rows.RemoveRange(index + 1, visible);
    }

    public void ToggleExpansion(TreeRowViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsExpanded)
            Collapse(row);
        else
            Expand(row);
    }

    private static int CountVisibleDescendants(TreeRowViewModel row)
    {
        if (!row.IsExpanded)
            return 0;

        int count = 0;
        var children = row.Children;
        for (int i = 0; i < children.Count; i++)
            count += 1 + CountVisibleDescendants(children[i]);
        return count;
    }

    /// <summary>
    /// Expands a branch far enough to be useful without unfolding a whole drive:
    /// levels are added while the number of revealed rows stays modest.
    /// </summary>
    public void ExpandBranch(TreeRowViewModel? row)
    {
        row ??= _rootRow;
        if (row is null)
            return;

        const int budget = 2000;
        int revealed = 0;

        var queue = new Queue<TreeRowViewModel>();
        queue.Enqueue(row);

        while (queue.Count > 0 && revealed < budget)
        {
            var current = queue.Dequeue();
            if (!current.IsExpandable)
                continue;

            current.IsExpanded = true;

            var children = current.Children;
            revealed += children.Count;

            for (int i = 0; i < children.Count && revealed < budget; i++)
                if (children[i].IsFolder)
                    queue.Enqueue(children[i]);
        }

        RebuildRows();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CollapseAll()
    {
        if (_rootRow is null)
            return;

        CollapseRecursive(_rootRow);
        _rootRow.IsExpanded = true;
        RebuildRows();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void CollapseRecursive(TreeRowViewModel row)
    {
        if (!row.IsExpanded)
            return;

        foreach (var child in row.Children)
            CollapseRecursive(child);

        row.IsExpanded = false;
    }

    /// <summary>Expands down to <paramref name="node"/> and selects its row.</summary>
    public void RevealNode(DirectoryNode node)
    {
        if (_rootRow is null || node is null)
            return;

        var chain = new List<DirectoryNode>();
        for (DirectoryNode? n = node; n is not null; n = n.Parent)
            chain.Add(n);
        chain.Reverse();

        if (chain.Count == 0 || !ReferenceEquals(chain[0], _rootRow.Node))
            return;

        var row = _rootRow;
        for (int i = 1; i < chain.Count; i++)
        {
            Expand(row);

            TreeRowViewModel? next = null;
            foreach (var child in row.Children)
            {
                if (ReferenceEquals(child.Node, chain[i]))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
                break;
            row = next;
        }

        SelectedRow = row;
        ScrollRequested?.Invoke(row);
    }

    private void RefreshAllValues()
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].RefreshValues();
    }

    // ═════════════════════════ Treemap and chart ═════════════════════════

    private void ZoomIn()
    {
        if (SelectedRow?.Node is { } node)
            TreemapRoot = node;
    }

    private void ZoomOut()
    {
        if (_treemapRoot?.Parent is { } parent)
            TreemapRoot = parent;
    }

    private void RebuildChart()
    {
        ChartSlices.Clear();

        var root = _treemapRoot;
        if (root is null)
            return;

        const int maxSlices = 9;

        var entries = new List<ChartSlice>(maxSlices + 1);
        long total = root.TotalSize;
        if (total <= 0)
            return;

        var children = root.Directories
            .Where(d => d.TotalSize > 0)
            .OrderByDescending(d => d.TotalSize)
            .ToList();

        long accounted = 0;
        for (int i = 0; i < children.Count && i < maxSlices; i++)
        {
            var child = children[i];
            accounted += child.TotalSize;
            entries.Add(new ChartSlice(child.Name, child.TotalSize, (double)child.TotalSize / total, child));
        }

        // Files sitting directly in this folder plus any tail of small folders
        // are rolled into one wedge, so the wedges always sum to the whole.
        long remainder = total - accounted;
        if (remainder > 0)
        {
            string label = children.Count > maxSlices ? "Other folders and files" : "Files in this folder";
            entries.Add(new ChartSlice(label, remainder, (double)remainder / total, null));
        }

        foreach (var slice in entries)
            ChartSlices.Add(slice);
    }

    // ═════════════════════════ Shell actions ═════════════════════════

    private bool HasSelection() => _selectedRow is not null;

    private bool HasResult() => _result is not null;

    private void RunShell(Func<string, ShellResult> action)
    {
        var row = _selectedRow;
        if (row is null)
            return;

        var result = action(row.FullPath);
        if (!result.Success && result.Error is { Length: > 0 })
            _dialogs?.ShowMessage("Action failed", result.Error, isError: true);
    }

    private void DeleteSelected()
    {
        var row = _selectedRow;
        if (row is null || _dialogs is null)
            return;

        if (row.IsRoot)
        {
            _dialogs.ShowMessage("Cannot delete", "The scan root itself cannot be deleted from here.");
            return;
        }

        var request = new DeleteRequest(
            new[] { row.FullPath },
            row.Size,
            row.IsFile ? 1 : row.FileCount ?? 0,
            row.IsFolder);

        var choice = _dialogs.ConfirmDelete(request);
        if (!choice.Confirmed)
            return;

        var result = ShellService.Delete(request.Paths, _dialogs.OwnerHandle, choice.Permanent);

        if (result.Aborted)
            return;

        if (!result.Success)
        {
            _dialogs.ShowMessage("Delete failed", result.Error ?? "The item could not be deleted.", isError: true);
            return;
        }

        // The in-memory tree no longer matches the disk. Rescanning the whole
        // root would be slow and lose the user's place, so the row is dropped
        // from the view and the totals are left for the next refresh — with a
        // clear hint that what is on screen is now one delete out of date.
        RemoveRowFromView(row);
        IsStale = true;
    }

    private bool _isStale;

    /// <summary>True once the view is known to differ from disk, after a delete.</summary>
    public bool IsStale
    {
        get => _isStale;
        private set => Set(ref _isStale, value);
    }

    private void RemoveRowFromView(TreeRowViewModel row)
    {
        int index = _rows.IndexOf(row);
        if (index < 0)
            return;

        int descendants = CountVisibleDescendants(row);
        _rows.RemoveRange(index, descendants + 1);

        SelectedRow = index < _rows.Count ? _rows[index] : (_rows.Count > 0 ? _rows[^1] : null);
    }

    private void Browse()
    {
        string? picked = _dialogs?.BrowseForFolder(SelectedPath);
        if (!string.IsNullOrWhiteSpace(picked))
            StartScan(picked);
    }

    private void RestartElevated()
    {
        if (_dialogs is null)
            return;

        if (!_dialogs.Confirm("Restart as administrator",
                "SpaceTree will close and reopen with administrator rights so it can read protected folders.\n\nContinue?"))
            return;

        _settingsService.Save();

        if (ElevationService.RestartElevated(_result?.RootPath ?? SelectedPath))
            System.Windows.Application.Current?.Shutdown();
    }

    // ═════════════════════════ Export ═════════════════════════

    /// <summary>Builds the rows for an export, matching what is on screen.</summary>
    private IReadOnlyList<ExportRow> BuildExportRows(DirectoryNode root)
    {
        var options = new ExportOptions
        {
            IncludeFiles = _context.ShowFiles,
            Filter = _context.Filter.Filter,
            SortBySizeDescending = _context.SortColumn == SortColumn.Size &&
                                   _context.SortDirection == SortDirection.Descending,
        };

        return ExportRowBuilder.Build(root, options).ToList();
    }

    /// <summary>
    /// Builds the rows for a report, as opposed to a data export.
    ///
    /// Reports are read in a browser or on paper, so they are summarised rather
    /// than exhaustive: listing every entry of a home directory produced a 61 MB
    /// page with millions of DOM nodes, which is enough to hang a browser tab.
    /// CSV and text remain complete.
    /// </summary>
    private IReadOnlyList<ExportRow> BuildReportRows(DirectoryNode root) =>
        ReportRowBuilder.Build(root, new ReportOptions
        {
            IncludeFiles = _context.ShowFiles,
            Filter = _context.Filter,
        });

    private static string ReportScopeNote(int rowCount) =>
        $"Summarised report: the largest items are listed at each level and everything else is " +
        $"grouped into \"and N more items\" rows, so the figures still add up to the totals above. " +
        $"{rowCount:N0} rows shown. Export to CSV for the complete listing.";

    /// <summary>Exports the selected folder when there is one, otherwise the whole scan.</summary>
    private DirectoryNode? ExportRootNode() => _selectedRow?.Node ?? _result?.Root;

    private void ExportCsv()
    {
        var root = ExportRootNode();
        if (root is null || _dialogs is null)
            return;

        string? path = _dialogs.SaveFile("Export to CSV",
            "CSV files (*.csv)|*.csv|All files (*.*)|*.*", SuggestFileName(root, "csv"), ".csv");
        if (path is null)
            return;

        Guard(() => CsvExporter.WriteToFile(path, BuildExportRows(root)), path);
    }

    private void ExportText()
    {
        var root = ExportRootNode();
        if (root is null || _dialogs is null)
            return;

        string? path = _dialogs.SaveFile("Export to text",
            "Text files (*.txt)|*.txt|All files (*.*)|*.*", SuggestFileName(root, "txt"), ".txt");
        if (path is null)
            return;

        Guard(() => TextExporter.WriteToFile(path, BuildExportRows(root),
            $"SpaceTree — {LongPath.ToDisplay(root.FullPath)}", _context.Units), path);
    }

    private void ExportHtml()
    {
        var root = ExportRootNode();
        if (root is null || _dialogs is null)
            return;

        string? path = _dialogs.SaveFile("Export report",
            "HTML report (*.html)|*.html|All files (*.*)|*.*", SuggestFileName(root, "html"), ".html");
        if (path is null)
            return;

        var rows = BuildReportRows(root);
        Guard(() => HtmlReportExporter.WriteToFile(path, rows, BuildMetadata(root, ReportScopeNote(rows.Count)), _context.Units), path);
    }

    private void Print()
    {
        var root = ExportRootNode();
        if (root is null || _dialogs is null)
            return;

        try
        {
            var rows = BuildReportRows(root);
            _dialogs.PrintReport(rows, BuildMetadata(root, ReportScopeNote(rows.Count)), _context.Units);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            _dialogs.ShowMessage("Print failed", e.Message, isError: true);
        }
    }

    private ReportMetadata BuildMetadata(DirectoryNode root, string? scopeNote = null) => new()
    {
        RootPath = LongPath.ToDisplay(root.FullPath),
        ScopeNote = scopeNote,
        TotalSize = root.TotalSize,
        TotalAllocated = root.TotalAllocated,
        FileCount = root.TotalFileCount,
        FolderCount = root.TotalDirectoryCount,
        Duration = _result?.Duration ?? TimeSpan.Zero,
        Volume = _result?.Volume,
        Errors = _result?.Errors ?? Array.Empty<ScanError>(),
    };

    private void Guard(Action action, string path)
    {
        try
        {
            action();
            _dialogs?.ShowMessage("Export complete", $"Written to:\n{path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _dialogs?.ShowMessage("Export failed", e.Message, isError: true);
        }
    }

    private static string SuggestFileName(DirectoryNode root, string extension)
    {
        string name = LongPath.ToDisplay(root.FullPath)
            .Replace(":", string.Empty)
            .Replace('\\', '-')
            .Replace('/', '-')
            .Trim('-', ' ');

        if (name.Length == 0)
            name = "spacetree";

        return $"SpaceTree {name} {DateTime.Now:yyyy-MM-dd}.{extension}";
    }

    // ═════════════════════════ Progress and status text ═════════════════════════

    public string ProgressPrimary => IsScanning
        ? $"{SizeFormatter.FormatCount(_progress.FilesScanned)} files · " +
          $"{SizeFormatter.FormatCount(_progress.DirectoriesScanned)} folders · " +
          $"{SizeFormatter.Format(_progress.BytesScanned, _context.Units)}"
        : string.Empty;

    public string ProgressCurrentPath => IsScanning ? _progress.CurrentPath : string.Empty;

    public double ProgressPercent => IsScanning ? _progress.EstimatedFraction * 100d : 0d;

    public string ProgressRate => IsScanning && _progress.FilesPerSecond > 0
        ? $"{_progress.FilesPerSecond:N0} files/s"
        : string.Empty;

    public string ProgressEta
    {
        get
        {
            if (!IsScanning)
                return string.Empty;
            var remaining = _progress.EstimatedRemaining;
            return remaining is null
                ? $"{SizeFormatter.FormatDuration(_progress.Elapsed)} elapsed"
                : $"{SizeFormatter.FormatDuration(_progress.Elapsed)} elapsed · about {SizeFormatter.FormatDuration(remaining.Value)} left";
        }
    }

    public string StatusTotal
    {
        get
        {
            var root = _result?.Root ?? _scanner?.Root;
            if (root is null)
                return "No scan yet";

            return $"{SizeFormatter.Format(root.TotalSize, _context.Units)} " +
                   $"({SizeFormatter.Format(root.TotalAllocated, _context.Units)} on disk)";
        }
    }

    public string StatusItems
    {
        get
        {
            var root = _result?.Root ?? _scanner?.Root;
            return root is null
                ? string.Empty
                : $"{SizeFormatter.FormatCount(root.TotalFileCount)} files · {SizeFormatter.FormatCount(root.TotalDirectoryCount)} folders";
        }
    }

    public string StatusVolume
    {
        get
        {
            var volume = _result?.Volume;
            if (volume is null || volume.TotalBytes <= 0)
                return string.Empty;

            double usedFraction = (double)volume.UsedBytes / volume.TotalBytes;
            return $"{volume.RootPath} {SizeFormatter.Format(volume.FreeBytes, _context.Units)} free of " +
                   $"{SizeFormatter.Format(volume.TotalBytes, _context.Units)} ({SizeFormatter.FormatPercent(usedFraction)} used)";
        }
    }

    public double VolumeUsedFraction
    {
        get
        {
            var volume = _result?.Volume;
            return volume is null || volume.TotalBytes <= 0 ? 0 : (double)volume.UsedBytes / volume.TotalBytes;
        }
    }

    public string StatusScanTime
    {
        get
        {
            if (_result is null)
                return string.Empty;
            string prefix = _result.Cancelled ? "Cancelled after" : "Scanned in";
            return $"{prefix} {SizeFormatter.FormatDuration(_result.Duration)}";
        }
    }

    public string StatusErrors
    {
        get
        {
            int count = _result?.Errors.Count ?? 0;
            return count == 0 ? string.Empty : $"{SizeFormatter.FormatCount(count)} unreadable";
        }
    }

    public bool HasErrors => (_result?.Errors.Count ?? 0) > 0;

    private void RaiseProgress() => RaiseAll(
        nameof(ProgressPrimary), nameof(ProgressCurrentPath), nameof(ProgressPercent),
        nameof(ProgressRate), nameof(ProgressEta));

    private void RaiseStatus() => RaiseAll(
        nameof(StatusTotal), nameof(StatusItems), nameof(StatusVolume),
        nameof(StatusScanTime), nameof(StatusErrors), nameof(HasErrors),
        nameof(VolumeUsedFraction), nameof(SelectionSummary));

    private void RaiseCommandStates()
    {
        ExportCsvCommand.RaiseCanExecuteChanged();
        ExportTextCommand.RaiseCanExecuteChanged();
        ExportHtmlCommand.RaiseCanExecuteChanged();
        PrintCommand.RaiseCanExecuteChanged();
        ShowErrorsCommand.RaiseCanExecuteChanged();
        ZoomResetCommand.RaiseCanExecuteChanged();
        CollapseAllCommand.RaiseCanExecuteChanged();
        ExpandBranchCommand.RaiseCanExecuteChanged();
    }

    // ═════════════════════════ Commands ═════════════════════════

    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand ScanDriveCommand { get; }

    public RelayCommand ToggleExpandCommand { get; }
    public RelayCommand ClearFilterCommand { get; }
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand ExpandBranchCommand { get; }

    public RelayCommand OpenCommand { get; }
    public RelayCommand ShowInExplorerCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand PropertiesCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportTextCommand { get; }
    public RelayCommand ExportHtmlCommand { get; }
    public RelayCommand PrintCommand { get; }
    public RelayCommand ShowErrorsCommand { get; }

    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomResetCommand { get; }

    public RelayCommand RestartElevatedCommand { get; }
}
