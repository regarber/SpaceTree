using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SpaceTree.App.Services;
using SpaceTree.App.ViewModels;
using SpaceTree.Core.Model;
using SpaceTree.Core.Util;

namespace SpaceTree.App.Controls;

/// <summary>
/// Donut chart of the largest items in one folder.
///
/// A treemap answers "where did the space go across the whole tree"; this
/// answers the narrower question "what is this one folder made of", which is
/// easier to read at a glance when there are only a handful of children. The
/// hole in the middle carries the folder's total, so the chart is self-describing.
/// </summary>
public sealed class ShareChartControl : FrameworkElement
{
    private const double MinimumSweepDegrees = 0.35;

    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Typeface TotalTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly List<(ChartSlice Slice, double Start, double Sweep, Color Fill)> _wedges = new();

    private int _hoverIndex = -1;
    private bool _layoutValid;

    public ShareChartControl()
    {
        ClipToBounds = true;
        ThemeManager.ThemeChanged += (_, _) => Invalidate();
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(ShareChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty TotalTextProperty = DependencyProperty.Register(
        nameof(TotalText), typeof(string), typeof(ShareChartControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public string TotalText
    {
        get => (string)GetValue(TotalTextProperty);
        set => SetValue(TotalTextProperty, value);
    }

    public static readonly DependencyProperty UnitsProperty = DependencyProperty.Register(
        nameof(Units), typeof(SizeUnitSystem), typeof(ShareChartControl),
        new FrameworkPropertyMetadata(SizeUnitSystem.Binary, FrameworkPropertyMetadataOptions.AffectsRender));

    public SizeUnitSystem Units
    {
        get => (SizeUnitSystem)GetValue(UnitsProperty);
        set => SetValue(UnitsProperty, value);
    }

    public event Action<DirectoryNode>? SliceSelected;

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (ShareChartControl)d;

        if (e.OldValue is INotifyCollectionChanged oldObservable)
            oldObservable.CollectionChanged -= chart.OnItemsChanged;
        if (e.NewValue is INotifyCollectionChanged newObservable)
            newObservable.CollectionChanged += chart.OnItemsChanged;

        chart.Invalidate();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Invalidate();

    private void Invalidate()
    {
        _layoutValid = false;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        _layoutValid = false;
    }

    private void BuildWedges()
    {
        _wedges.Clear();
        _layoutValid = true;

        if (ItemsSource is null)
            return;

        bool dark = ThemeManager.IsDark;

        double cursor = -90; // start at twelve o'clock
        int index = 0;

        foreach (var item in ItemsSource)
        {
            if (item is not ChartSlice slice)
                continue;

            double sweep = Math.Clamp(slice.Fraction, 0, 1) * 360;
            if (sweep < MinimumSweepDegrees)
            {
                index++;
                continue;
            }

            _wedges.Add((slice, cursor, sweep, TreemapPalette.For(index, dark)));
            cursor += sweep;
            index++;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (!_layoutValid)
            BuildWedges();

        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 40)
            return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2 - 6;
        double inner = outer * 0.58;

        if (_wedges.Count == 0)
        {
            DrawPlaceholder(dc, centre, outer);
            return;
        }

        var plate = TryFindResource("Brush.Surface") as Brush ?? Brushes.Transparent;
        var separator = new Pen(plate, 1.5);
        separator.Freeze();

        for (int i = 0; i < _wedges.Count; i++)
        {
            var (_, start, sweep, fill) = _wedges[i];

            var color = i == _hoverIndex ? TreemapPalette.Highlight(fill, ThemeManager.IsDark) : fill;
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            double push = i == _hoverIndex ? 3 : 0;
            dc.DrawGeometry(brush, separator, BuildRing(centre, outer + push, inner + push, start, sweep));
        }

        DrawTotal(dc, centre);
    }

    /// <summary>Builds one donut segment as an outer arc, a line in, and an inner arc back.</summary>
    private static Geometry BuildRing(Point centre, double outer, double inner, double startDegrees, double sweepDegrees)
    {
        // A full circle cannot be expressed as a single arc, so it degenerates
        // into two half sweeps.
        if (sweepDegrees >= 359.99)
        {
            var full = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new EllipseGeometry(centre, outer, outer),
                new EllipseGeometry(centre, inner, inner));
            full.Freeze();
            return full;
        }

        double startRad = startDegrees * Math.PI / 180;
        double endRad = (startDegrees + sweepDegrees) * Math.PI / 180;

        Point OuterAt(double a) => new(centre.X + outer * Math.Cos(a), centre.Y + outer * Math.Sin(a));
        Point InnerAt(double a) => new(centre.X + inner * Math.Cos(a), centre.Y + inner * Math.Sin(a));

        bool large = sweepDegrees > 180;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OuterAt(startRad), isFilled: true, isClosed: true);
            ctx.ArcTo(OuterAt(endRad), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(InnerAt(endRad), true, false);
            ctx.ArcTo(InnerAt(startRad), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawTotal(DrawingContext dc, Point centre)
    {
        if (string.IsNullOrEmpty(TotalText))
            return;

        var foreground = TryFindResource("Brush.Text") as Brush ?? Brushes.White;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var text = new FormattedText(TotalText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            TotalTypeface, 13, foreground, dpi);

        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

    private void DrawPlaceholder(DrawingContext dc, Point centre, double outer)
    {
        var stroke = TryFindResource("Brush.Border") as Brush ?? Brushes.Gray;
        var pen = new Pen(stroke, 1);
        pen.Freeze();
        dc.DrawEllipse(null, pen, centre, outer, outer);

        var foreground = TryFindResource("Brush.TextSecondary") as Brush ?? Brushes.Gray;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText("No data", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, 12, foreground, dpi);
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

    // ═════════════════════════ Interaction ═════════════════════════

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int index = HitTest(e.GetPosition(this));
        if (index == _hoverIndex)
            return;

        _hoverIndex = index;

        ToolTip = index < 0
            ? null
            : $"{_wedges[index].Slice.Name}\n{SizeFormatter.Format(_wedges[index].Slice.Size, Units)} " +
              $"({SizeFormatter.FormatPercent(_wedges[index].Slice.Fraction)})";

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

        int index = HitTest(e.GetPosition(this));
        if (index >= 0 && _wedges[index].Slice.Node is { } node)
            SliceSelected?.Invoke(node);
    }

    private int HitTest(Point point)
    {
        if (!_layoutValid || _wedges.Count == 0)
            return -1;

        double size = Math.Min(ActualWidth, ActualHeight);
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2 - 6;
        double inner = outer * 0.58;

        double dx = point.X - centre.X, dy = point.Y - centre.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < inner || distance > outer + 4)
            return -1;

        // Normalise to the same origin the wedges were laid out from.
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (angle < -90)
            angle += 360;

        for (int i = 0; i < _wedges.Count; i++)
        {
            var (_, start, sweep, _) = _wedges[i];
            if (angle >= start && angle < start + sweep)
                return i;
        }

        return -1;
    }
}
