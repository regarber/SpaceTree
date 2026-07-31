using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SpaceTree.App.Services;
using SpaceTree.Core.Model;
using SpaceTree.Core.Util;
using SpaceTree.Core.Visualization;

namespace SpaceTree.App.Controls;

/// <summary>
/// Interactive squarified treemap.
///
/// Drawn directly into a DrawingContext rather than built from elements: a
/// treemap of a system drive is thousands of rectangles, and giving each one a
/// FrameworkElement would cost more in layout than the scan costs in I/O. One
/// OnRender pass over a precomputed tile list keeps resizing smooth.
///
/// The layout itself is <see cref="TreemapLayout"/> in the core library, so the
/// geometry is unit-tested independently of anything WPF.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    /// <summary>A laid-out rectangle plus what it represents.</summary>
    private readonly struct Tile
    {
        public Tile(Rect bounds, DirectoryNode node, bool isFileBlock, int depth, Color fill)
        {
            Bounds = bounds;
            Node = node;
            IsFileBlock = isFileBlock;
            Depth = depth;
            Fill = fill;
        }

        public Rect Bounds { get; }
        public DirectoryNode Node { get; }

        /// <summary>True for the block standing in for the files held directly by <see cref="Node"/>.</summary>
        public bool IsFileBlock { get; }

        public int Depth { get; }
        public Color Fill { get; }
    }

    private const double MinimumTileSide = 3d;
    private const double LabelMinimumWidth = 54d;
    private const double LabelMinimumHeight = 16d;
    /// <summary>
    /// Ceiling on drawn rectangles. Generous, because the real limit is the
    /// pixel-size guard in <see cref="Subdivide"/> — this only exists so a
    /// pathological tree cannot make a single render take seconds.
    /// </summary>
    private const int MaxTiles = 12000;

    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private readonly List<Tile> _tiles = new();

    private int _hoverIndex = -1;
    private bool _layoutValid;

    public TreemapControl()
    {
        ClipToBounds = true;
        Focusable = true;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    // ═════════════════════════ Properties ═════════════════════════

    public static readonly DependencyProperty RootNodeProperty = DependencyProperty.Register(
        nameof(RootNode), typeof(DirectoryNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutInputChanged));

    public DirectoryNode? RootNode
    {
        get => (DirectoryNode?)GetValue(RootNodeProperty);
        set => SetValue(RootNodeProperty, value);
    }

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(DirectoryNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DirectoryNode? SelectedNode
    {
        get => (DirectoryNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public static readonly DependencyProperty MaxDepthProperty = DependencyProperty.Register(
        nameof(MaxDepth), typeof(int), typeof(TreemapControl),
        new FrameworkPropertyMetadata(9, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutInputChanged));

    /// <summary>
    /// Hard ceiling on nesting levels. Deliberately high: capping at a shallow
    /// depth made the biggest consumer on the drive — always the most deeply
    /// nested one, some node_modules or site-packages — render as a single flat
    /// slab of colour, which is precisely the region the user opened the treemap
    /// to understand. Recursion normally stops on tile size well before this.
    /// </summary>
    public int MaxDepth
    {
        get => (int)GetValue(MaxDepthProperty);
        set => SetValue(MaxDepthProperty, value);
    }

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(TreemapControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public static readonly DependencyProperty UnitsProperty = DependencyProperty.Register(
        nameof(Units), typeof(SizeUnitSystem), typeof(TreemapControl),
        new FrameworkPropertyMetadata(SizeUnitSystem.Binary, FrameworkPropertyMetadataOptions.AffectsRender));

    public SizeUnitSystem Units
    {
        get => (SizeUnitSystem)GetValue(UnitsProperty);
        set => SetValue(UnitsProperty, value);
    }

    /// <summary>Raised on single click.</summary>
    public event Action<DirectoryNode>? NodeSelected;

    /// <summary>Raised on double click — the caller normally zooms into the node.</summary>
    public event Action<DirectoryNode>? NodeActivated;

    private static void OnLayoutInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TreemapControl)d).InvalidateLayoutCache();

    /// <summary>Forces the tile list to be recomputed, e.g. after a live scan tick.</summary>
    public void InvalidateLayoutCache()
    {
        _layoutValid = false;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateLayoutCache();

    // ═════════════════════════ Layout ═════════════════════════

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        _layoutValid = false;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    private void BuildLayout()
    {
        _tiles.Clear();
        _layoutValid = true;

        var root = RootNode;
        if (root is null || root.TotalSize <= 0)
            return;

        double width = ActualWidth, height = ActualHeight;
        if (width < 8 || height < 8)
            return;

        bool dark = ThemeManager.IsDark;
        Subdivide(root, new Rect(0, 0, width, height), 0, null, dark);
    }

    /// <summary>
    /// Lays out one folder's contents into <paramref name="bounds"/> and recurses.
    ///
    /// The children are the subfolders plus one synthetic block standing for the
    /// files held directly here — without it the child areas would not add up to
    /// the parent, and a folder full of loose files would look empty.
    /// </summary>
    private void Subdivide(DirectoryNode node, Rect bounds, int depth, Color? inheritedColor, bool dark)
    {
        if (_tiles.Count >= MaxTiles ||
            bounds.Width < MinimumTileSide || bounds.Height < MinimumTileSide)
        {
            return;
        }

        var dirs = node.Directories;
        long ownFiles = Math.Max(0, node.TotalSize - SumChildren(dirs));

        bool leaf = depth >= MaxDepth ||
                    bounds.Width < MinimumTileSide * 4 ||
                    bounds.Height < MinimumTileSide * 4 ||
                    (dirs.Length == 0 && ownFiles <= 0);

        if (leaf)
        {
            var color = inheritedColor ?? TreemapPalette.For(0, dark);
            _tiles.Add(new Tile(bounds, node, isFileBlock: dirs.Length == 0, depth, TreemapPalette.AtDepth(color, depth, dark)));
            return;
        }

        // Weights in the same order as the children they describe: subfolders
        // first, then the loose-files block.
        //
        // These lists are local rather than reused instance buffers: this method
        // recurses in the middle of iterating its own layout, so a shared buffer
        // would be overwritten by a child and the parent would carry on reading
        // the child's rectangles.
        var weights = new List<double>(dirs.Length + 1);
        for (int i = 0; i < dirs.Length; i++)
            weights.Add(dirs[i].TotalSize);
        weights.Add(ownFiles);

        var layout = new List<TreemapTile>(weights.Count);
        TreemapLayout.Squarify(weights, bounds.X, bounds.Y, bounds.Width, bounds.Height, layout);

        for (int i = 0; i < layout.Count; i++)
        {
            var placed = layout[i];
            var rect = new Rect(placed.X, placed.Y, Math.Max(0, placed.Width), Math.Max(0, placed.Height));

            if (rect.Width < MinimumTileSide || rect.Height < MinimumTileSide)
                continue;

            bool isFileBlock = placed.Index == dirs.Length;
            var child = isFileBlock ? node : dirs[placed.Index];

            // Top-level children set the categorical colour that everything
            // below them inherits, which is what makes a big consumer readable
            // as one contiguous region.
            Color color = inheritedColor ?? TreemapPalette.For(placed.Index, dark);

            if (isFileBlock)
            {
                _tiles.Add(new Tile(rect, node, isFileBlock: true, depth + 1,
                    TreemapPalette.AtDepth(color, depth + 1, dark)));
                continue;
            }

            // A one-pixel inset gives every nested group a visible frame.
            var inner = Rect.Inflate(rect, -1, -1);
            if (inner.Width < MinimumTileSide || inner.Height < MinimumTileSide)
            {
                _tiles.Add(new Tile(rect, child, isFileBlock: false, depth + 1,
                    TreemapPalette.AtDepth(color, depth + 1, dark)));
                continue;
            }

            _tiles.Add(new Tile(rect, child, isFileBlock: false, depth + 1,
                TreemapPalette.AtDepth(color, depth + 1, dark)));

            Subdivide(child, inner, depth + 1, color, dark);
        }
    }

    private static long SumChildren(DirectoryNode[] dirs)
    {
        long total = 0;
        for (int i = 0; i < dirs.Length; i++)
            total += dirs[i].TotalSize;
        return total;
    }

    // ═════════════════════════ Rendering ═════════════════════════

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var plate = TryFindBrush("Brush.PlotBackground") ?? Brushes.Black;
        dc.DrawRectangle(plate, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (!_layoutValid)
            BuildLayout();

        if (_tiles.Count == 0)
        {
            DrawEmptyMessage(dc);
            return;
        }

        bool dark = ThemeManager.IsDark;
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(dark ? (byte)150 : (byte)90, 0, 0, 0)), 1);
        edge.Freeze();

        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];

            var fill = tile.Fill;
            if (i == _hoverIndex)
                fill = TreemapPalette.Highlight(fill, dark);

            var brush = new SolidColorBrush(fill);
            brush.Freeze();

            // Snapping to whole pixels keeps the 1px edges crisp instead of grey.
            var rect = Snap(tile.Bounds);
            dc.DrawRectangle(brush, edge, rect);
        }

        if (ShowLabels)
            DrawLabels(dc);

        DrawSelection(dc);
    }

    /// <summary>
    /// Labels tiles from the outside in, giving each label a claim on the space
    /// it occupies.
    ///
    /// Tiles are stored parents-first, so shallow folders get first refusal on a
    /// corner and deeper ones fill in wherever nothing has been written yet.
    /// That is what keeps the captions readable: labelling every tile buried
    /// names under each other, and labelling only the deepest ones filled the
    /// view with "(files)" while the folder names that answer "what is this?"
    /// went missing.
    /// </summary>
    private void DrawLabels(DrawingContext dc)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var claimed = new List<Rect>(64);

        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var rect = tile.Bounds;

            // The loose-files block borrows its parent's name, which is already
            // the more informative caption for that area.
            if (tile.IsFileBlock)
                continue;

            if (rect.Width < LabelMinimumWidth || rect.Height < LabelMinimumHeight)
                continue;

            // Only label tiles a person could actually be looking for.
            if (rect.Width * rect.Height < 2400)
                continue;

            var textColor = TreemapPalette.TextOn(tile.Fill);

            var text = new FormattedText(
                tile.Node.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, 11.5, new SolidColorBrush(textColor), dpi)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 8),
                MaxTextHeight = Math.Max(1, rect.Height - 4),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };

            var origin = new Point(rect.X + 4, rect.Y + 3);
            var footprint = new Rect(origin.X - 2, origin.Y - 1, text.Width + 4, text.Height + 2);

            if (Overlaps(claimed, footprint))
                continue;

            // A halo keeps the caption legible where it crosses onto a child
            // tile of a different shade.
            var halo = new SolidColorBrush(Color.FromArgb(90, tile.Fill.R, tile.Fill.G, tile.Fill.B));
            halo.Freeze();
            dc.DrawRectangle(halo, null, footprint);

            dc.DrawText(text, origin);
            claimed.Add(footprint);

            if (claimed.Count >= 60)
                return;
        }
    }

    private static bool Overlaps(List<Rect> claimed, Rect candidate)
    {
        for (int i = 0; i < claimed.Count; i++)
            if (claimed[i].IntersectsWith(candidate))
                return true;
        return false;
    }

    private void DrawSelection(DrawingContext dc)
    {
        var selected = SelectedNode;
        if (selected is null)
            return;

        for (int i = 0; i < _tiles.Count; i++)
        {
            if (!ReferenceEquals(_tiles[i].Node, selected) || _tiles[i].IsFileBlock)
                continue;

            var accent = TryFindBrush("Brush.Accent") ?? Brushes.DodgerBlue;
            var pen = new Pen(accent, 2);
            pen.Freeze();
            dc.DrawRectangle(null, pen, Rect.Inflate(Snap(_tiles[i].Bounds), -1, -1));
            return;
        }
    }

    private void DrawEmptyMessage(DrawingContext dc)
    {
        var foreground = TryFindBrush("Brush.TextSecondary") ?? Brushes.Gray;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var text = new FormattedText(
            RootNode is null ? "Run a scan to see the treemap." : "Nothing to show here.",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, 12, foreground, dpi);

        dc.DrawText(text, new Point(
            Math.Max(8, (ActualWidth - text.Width) / 2),
            Math.Max(8, (ActualHeight - text.Height) / 2)));
    }

    private static Rect Snap(Rect rect) => new(
        Math.Round(rect.X), Math.Round(rect.Y),
        Math.Max(0, Math.Round(rect.Width)), Math.Max(0, Math.Round(rect.Height)));

    private Brush? TryFindBrush(string key) => TryFindResource(key) as Brush;

    // ═════════════════════════ Interaction ═════════════════════════

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int index = HitTest(e.GetPosition(this));
        if (index == _hoverIndex)
            return;

        _hoverIndex = index;
        UpdateToolTip();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex < 0)
            return;

        _hoverIndex = -1;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        int index = HitTest(e.GetPosition(this));
        if (index < 0)
            return;

        var node = _tiles[index].Node;

        if (e.ClickCount >= 2)
            NodeActivated?.Invoke(node);
        else
            NodeSelected?.Invoke(node);

        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        // Select under the cursor first, so the context menu acts on what was
        // right-clicked rather than on whatever was selected before.
        int index = HitTest(e.GetPosition(this));
        if (index >= 0)
            NodeSelected?.Invoke(_tiles[index].Node);
    }

    /// <summary>
    /// Returns the innermost tile at a point. Tiles are appended parent-first, so
    /// scanning backwards finds the deepest — and therefore most specific — one.
    /// </summary>
    private int HitTest(Point point)
    {
        if (!_layoutValid)
            return -1;

        for (int i = _tiles.Count - 1; i >= 0; i--)
            if (_tiles[i].Bounds.Contains(point))
                return i;

        return -1;
    }

    private void UpdateToolTip()
    {
        if (_hoverIndex < 0)
        {
            ToolTip = null;
            return;
        }

        var tile = _tiles[_hoverIndex];
        var node = tile.Node;

        long size = tile.IsFileBlock ? Math.Max(0, node.TotalSize - SumChildren(node.Directories)) : node.TotalSize;

        string header = tile.IsFileBlock
            ? $"Files directly in {node.Name}"
            : SpaceTree.Core.Native.LongPath.ToDisplay(node.FullPath);

        ToolTip = $"{header}\n{SizeFormatter.Format(size, Units)}" +
                  (tile.IsFileBlock ? string.Empty : $"\n{SizeFormatter.FormatCount(node.TotalFileCount)} files");
    }
}
