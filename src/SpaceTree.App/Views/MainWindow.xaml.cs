using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpaceTree.App.Services;
using SpaceTree.App.ViewModels;
using SpaceTree.Core.Sorting;

namespace SpaceTree.App.Views;

public partial class MainWindow : Window
{
    /// <summary>Column identities, in the order they are declared in XAML.</summary>
    private static readonly (string Id, SortColumn Sort)[] ColumnDefinitions =
    {
        ("Name", SortColumn.Name),
        ("Size", SortColumn.Size),
        ("Allocated", SortColumn.Allocated),
        ("% of Parent", SortColumn.PercentOfParent),
        ("Files", SortColumn.Files),
        ("Folders", SortColumn.Folders),
        ("Last Modified", SortColumn.LastModified),
    };

    private readonly MainViewModel _viewModel;
    private readonly List<GridViewColumn> _allColumns = new();

    private TreeRowViewModel? _selectionBeforeReset;
    private bool _layoutRestored;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.AttachDialogs(new WindowDialogService(this));
        _viewModel.ScrollRequested += OnScrollRequested;
        _viewModel.TreeChanged += (_, _) => RefreshVisuals();

        _viewModel.Rows.Resetting += (_, _) => _selectionBeforeReset = _viewModel.SelectedRow;
        ((INotifyCollectionChanged)_viewModel.Rows).CollectionChanged += OnRowsChanged;

        Tree.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick));

        Loaded += OnLoaded;
        Closing += OnClosing;
        DragOver += OnDragOver;
        Drop += OnDrop;
        PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_layoutRestored)
            return;
        _layoutRestored = true;

        RestoreWindowPlacement();

        _allColumns.Clear();
        foreach (var column in Columns.Columns)
            _allColumns.Add(column);

        RestoreColumnLayout();
        BuildColumnsMenu();
        BuildThreadMenu();
        SyncThemeMenu();
        UpdateSortIndicators();

        VisualTabs.SelectedIndex = Math.Clamp(App.Settings.Current.VisualizationTab, 0, VisualTabs.Items.Count - 1);

        Treemap.NodeSelected += node => _viewModel.RevealNode(node);
        Treemap.NodeActivated += node => _viewModel.TreemapRoot = node;
        Chart.SliceSelected += node => _viewModel.RevealNode(node);

        PathBox.Focus();
    }

    // ═════════════════════════ Window placement ═════════════════════════

    private void RestoreWindowPlacement()
    {
        var s = App.Settings.Current;

        Width = s.WindowWidth;
        Height = s.WindowHeight;

        if (s.WindowLeft is { } left && s.WindowTop is { } top &&
            double.IsFinite(left) && double.IsFinite(top) &&
            IsOnAVisibleScreen(left, top, s.WindowWidth, s.WindowHeight))
        {
            Left = left;
            Top = top;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;

        // Stored as a fraction so the split stays proportional when the saved
        // size came from a differently-sized monitor.
        double treeFraction = Math.Clamp(s.TreePaneWidth, 0.15, 0.9);
        TreeColumn.Width = new GridLength(treeFraction, GridUnitType.Star);
        VisualColumn.Width = new GridLength(1 - treeFraction, GridUnitType.Star);
    }

    /// <summary>Guards against restoring onto a monitor that is no longer attached.</summary>
    private static bool IsOnAVisibleScreen(double left, double top, double width, double height)
    {
        var rect = new Rect(left, top, Math.Max(1, width), Math.Max(1, height));
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        rect.Intersect(virtualScreen);

        // Require a decent chunk of the title bar to be reachable.
        return rect.Width > 200 && rect.Height > 80;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var s = App.Settings.Current;

        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowLeft = Left;
            s.WindowTop = Top;
        }
        else
        {
            var bounds = RestoreBounds;
            if (bounds.Width > 0)
            {
                s.WindowWidth = bounds.Width;
                s.WindowHeight = bounds.Height;
                s.WindowLeft = bounds.Left;
                s.WindowTop = bounds.Top;
            }
        }

        s.WindowMaximized = WindowState == WindowState.Maximized;

        double total = TreeColumn.ActualWidth + VisualColumn.ActualWidth;
        if (total > 0)
            s.TreePaneWidth = Math.Clamp(TreeColumn.ActualWidth / total, 0.15, 0.9);

        s.VisualizationTab = VisualTabs.SelectedIndex;

        SaveColumnLayout();
        App.Settings.Save();

        _viewModel.CancelScan();
    }

    // ═════════════════════════ Columns ═════════════════════════

    private void RestoreColumnLayout()
    {
        var saved = App.Settings.Current.Columns;
        if (saved.Count == 0)
            return;

        var byId = saved.ToDictionary(c => c.Id, StringComparer.Ordinal);

        var ordered = ColumnDefinitions
            .Select((definition, index) => (
                Definition: definition,
                Column: _allColumns[index],
                Setting: byId.TryGetValue(definition.Id, out var setting)
                    ? setting
                    : new ColumnSetting { Id = definition.Id, Width = _allColumns[index].Width, Order = index }))
            .OrderBy(x => x.Setting.Order)
            .ToList();

        Columns.Columns.Clear();

        foreach (var entry in ordered)
        {
            // The name column is the tree itself; hiding it would leave nothing
            // to expand, so it is never optional.
            bool visible = entry.Setting.Visible || entry.Definition.Id == "Name";
            if (!visible)
                continue;

            if (entry.Setting.Width > 20 && entry.Setting.Width < 4000)
                entry.Column.Width = entry.Setting.Width;

            Columns.Columns.Add(entry.Column);
        }

        // A layout that somehow saved every column hidden must not produce an
        // empty header row.
        if (Columns.Columns.Count == 0)
            foreach (var column in _allColumns)
                Columns.Columns.Add(column);
    }

    private void SaveColumnLayout()
    {
        var settings = new List<ColumnSetting>(ColumnDefinitions.Length);

        for (int i = 0; i < ColumnDefinitions.Length; i++)
        {
            var column = _allColumns[i];
            int order = Columns.Columns.IndexOf(column);
            bool visible = order >= 0;

            settings.Add(new ColumnSetting
            {
                Id = ColumnDefinitions[i].Id,
                Width = column.ActualWidth > 0 ? column.ActualWidth : column.Width,
                Visible = visible,
                // Hidden columns keep a stable slot so re-enabling one puts it back
                // roughly where the user last had it.
                Order = visible ? order : ColumnDefinitions.Length + i,
            });
        }

        App.Settings.Current.Columns = settings;
    }

    private void BuildColumnsMenu()
    {
        ColumnsMenu.Items.Clear();

        for (int i = 0; i < ColumnDefinitions.Length; i++)
        {
            var definition = ColumnDefinitions[i];
            var column = _allColumns[i];

            var item = new MenuItem
            {
                Header = definition.Id,
                IsCheckable = true,
                IsChecked = Columns.Columns.Contains(column),
                IsEnabled = definition.Id != "Name",
                Tag = column,
            };

            item.Click += OnColumnVisibilityToggled;
            ColumnsMenu.Items.Add(item);
        }
    }

    private void OnColumnVisibilityToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GridViewColumn column } item)
            return;

        if (item.IsChecked)
        {
            if (Columns.Columns.Contains(column))
                return;

            // Re-insert at the position implied by the declaration order of the
            // columns still on screen, rather than always at the end.
            int declared = _allColumns.IndexOf(column);
            int insertAt = Columns.Columns.Count;

            for (int i = 0; i < Columns.Columns.Count; i++)
            {
                if (_allColumns.IndexOf(Columns.Columns[i]) > declared)
                {
                    insertAt = i;
                    break;
                }
            }

            Columns.Columns.Insert(insertAt, column);
        }
        else
        {
            Columns.Columns.Remove(column);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateSortIndicators);
    }

    private void BuildThreadMenu()
    {
        ThreadMenu.Items.Clear();

        foreach (int count in _viewModel.ThreadCountOptions)
        {
            var item = new MenuItem
            {
                Header = count == 1 ? "1 thread" : $"{count} threads",
                IsCheckable = true,
                IsChecked = _viewModel.ThreadCount == count,
                Tag = count,
            };

            item.Click += (s, _) =>
            {
                if (s is MenuItem { Tag: int selected })
                {
                    _viewModel.ThreadCount = selected;
                    foreach (var other in ThreadMenu.Items.OfType<MenuItem>())
                        other.IsChecked = other.Tag is int value && value == selected;
                }
            };

            ThreadMenu.Items.Add(item);
        }
    }

    // ═════════════════════════ Sorting ═════════════════════════

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header)
            return;

        // The trailing filler header has no column behind it.
        if (header.Column is null || header.Role == GridViewColumnHeaderRole.Padding)
            return;

        string id = header.Column.Header as string ?? string.Empty;
        var match = ColumnDefinitions.FirstOrDefault(c => c.Id == id);
        if (match.Id is null)
            return;

        _viewModel.SortBy(match.Sort);
        UpdateSortIndicators();
    }

    /// <summary>Marks the active column's header so its template can draw an arrow.</summary>
    private void UpdateSortIndicators()
    {
        string activeId = ColumnDefinitions.FirstOrDefault(c => c.Sort == _viewModel.SortColumn).Id ?? string.Empty;
        string direction = _viewModel.SortDirection == SortDirection.Ascending ? "Asc" : "Desc";

        foreach (var header in FindDescendants<GridViewColumnHeader>(Tree))
        {
            if (header.Column is null)
                continue;

            string id = header.Column.Header as string ?? string.Empty;
            header.Tag = id == activeId ? direction : null;
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private void RefreshVisuals()
    {
        Treemap.InvalidateLayoutCache();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateSortIndicators);
    }

    // ═════════════════════════ Tree interaction ═════════════════════════

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset)
            return;

        var restore = _selectionBeforeReset;
        _selectionBeforeReset = null;
        if (restore is null)
            return;

        // The list is rebuilt underneath the selection during live scans and
        // re-sorts; putting it back keeps the user's place.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_viewModel.Rows.Contains(restore))
                _viewModel.SelectedRow = restore;
        });
    }

    private void OnScrollRequested(TreeRowViewModel row)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_viewModel.Rows.Contains(row))
                Tree.ScrollIntoView(row);
        });
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem { DataContext: TreeRowViewModel row })
            return;

        if (row.IsFolder)
            _viewModel.ToggleExpansion(row);
        else
            _viewModel.OpenCommand.Execute(null);

        e.Handled = true;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        var row = _viewModel.SelectedRow;

        switch (e.Key)
        {
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                _viewModel.DeleteCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.CopyPathCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Right when row is not null:
                if (row.IsExpandable && !row.IsExpanded)
                    _viewModel.Expand(row);
                else if (row.IsExpanded && row.Children.Count > 0)
                    SelectRow(row.Children[0]);
                e.Handled = true;
                break;

            case Key.Left when row is not null:
                if (row.IsExpanded)
                    _viewModel.Collapse(row);
                else if (row.Parent is not null)
                    SelectRow(row.Parent);
                e.Handled = true;
                break;

            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None && row is not null:
                if (row.IsFolder)
                    _viewModel.ToggleExpansion(row);
                else
                    _viewModel.OpenCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void SelectRow(TreeRowViewModel row)
    {
        _viewModel.SelectedRow = row;
        Tree.ScrollIntoView(row);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
            return;

        // Escape means "undo the thing that is currently in the way", in the
        // order those things are likely to be bothering the user.
        if (_viewModel.IsScanning)
            _viewModel.CancelScan();
        else if (_viewModel.FilterText.Length > 0)
            _viewModel.FilterText = string.Empty;
        else
            return;

        e.Handled = true;
    }

    // ═════════════════════════ Toolbar handlers ═════════════════════════

    private void OnPathBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        _viewModel.StartScan(PathBox.Text);
        e.Handled = true;
    }

    private void OnDriveSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: SpaceTree.Core.Scanning.DriveSummary drive })
            return;

        _viewModel.StartScan(drive.Name);
    }

    /// <summary>Drops down the recently scanned roots, newest first.</summary>
    private void OnRecentPathsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var menu = new ContextMenu();

        if (_viewModel.RecentPaths.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No recent locations", IsEnabled = false });
        }
        else
        {
            foreach (string path in _viewModel.RecentPaths)
            {
                string captured = path;
                var item = new MenuItem { Header = captured };
                item.Click += (_, _) => _viewModel.StartScan(captured);
                menu.Items.Add(item);
            }
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnExportMenuClick(object sender, RoutedEventArgs e) => OpenMenuFor(sender);

    private void OnSettingsMenuClick(object sender, RoutedEventArgs e) => OpenMenuFor(sender);

    private static void OpenMenuFor(object sender)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnVisualTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_layoutRestored || !ReferenceEquals(e.OriginalSource, VisualTabs))
            return;

        App.Settings.Current.VisualizationTab = VisualTabs.SelectedIndex;
    }

    // ═════════════════════════ Theme ═════════════════════════

    private void OnThemeSystem(object sender, RoutedEventArgs e) => SetTheme(AppTheme.System);

    private void OnThemeDark(object sender, RoutedEventArgs e) => SetTheme(AppTheme.Dark);

    private void OnThemeLight(object sender, RoutedEventArgs e) => SetTheme(AppTheme.Light);

    private void SetTheme(AppTheme theme)
    {
        App.Settings.Current.Theme = theme;
        ThemeManager.Apply(theme);
        SyncThemeMenu();

        // The owner-drawn surfaces cache brushes, so they need a nudge.
        Treemap.InvalidateLayoutCache();
        Chart.InvalidateVisual();
    }

    private void SyncThemeMenu()
    {
        var theme = App.Settings.Current.Theme;
        ThemeSystemItem.IsChecked = theme == AppTheme.System;
        ThemeDarkItem.IsChecked = theme == AppTheme.Dark;
        ThemeLightItem.IsChecked = theme == AppTheme.Light;
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "SpaceTree — disk space analyzer\n\n" +
            "Scans local drives and folders and shows where the space went, as a " +
            "sortable tree and an interactive treemap.\n\n" +
            $"Running {(ElevationService.IsElevated ? "with" : "without")} administrator rights.\n" +
            $"Settings: {SettingsService.FilePath}",
            "About SpaceTree", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ═════════════════════════ Drag and drop ═════════════════════════

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFolder(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        string? folder = TryGetDroppedFolder(e);
        if (folder is null)
            return;

        e.Handled = true;
        Activate();
        _viewModel.StartScan(folder);
    }

    /// <summary>Resolves a drop to a folder — dropping a file scans the folder holding it.</summary>
    private static string? TryGetDroppedFolder(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return null;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return null;

        string path = paths[0];

        try
        {
            if (Directory.Exists(path))
                return path;

            return File.Exists(path) ? Path.GetDirectoryName(path) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PathTooLongException)
        {
            return null;
        }
    }
}
