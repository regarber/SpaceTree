using System.Collections.Generic;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SpaceTree.Core.Export;
using SpaceTree.Core.Util;

namespace SpaceTree.App.Services;

/// <summary>
/// Renders export rows into a paginated FlowDocument and hands it to the system
/// print dialog.
///
/// This is also how PDF export works: Windows ships "Microsoft Print to PDF" as
/// a printer, so the same path produces a PDF without bundling a PDF library.
/// The document is deliberately printed in black on white regardless of the app
/// theme — a dark-themed report wastes toner and reads badly on paper.
/// </summary>
public static class ReportPrinter
{
    /// <summary>
    /// Rows beyond this are dropped. A full drive is millions of rows and nobody
    /// wants a hundred thousand pages; the CSV export exists for the complete set.
    /// </summary>
    private const int MaxPrintedRows = 4000;

    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5C));
    private static readonly Brush Rule = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xD2));
    private static readonly Brush BarFill = new SolidColorBrush(Color.FromRgb(0xB8, 0xCC, 0xE8));

    public static void Print(Window? owner, IReadOnlyList<ExportRow> rows, ReportMetadata metadata, SizeUnitSystem units)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(metadata);

        var dialog = new PrintDialog();
        if (owner is not null)
            dialog.UserPageRangeEnabled = true;

        if (dialog.ShowDialog() != true)
            return;

        var document = BuildDocument(rows, metadata, units);

        // Lay the document out to the paper the user actually chose.
        document.PageWidth = dialog.PrintableAreaWidth;
        document.PageHeight = dialog.PrintableAreaHeight;
        document.PagePadding = new Thickness(48);
        document.ColumnGap = 0;
        document.ColumnWidth = dialog.PrintableAreaWidth;

        IDocumentPaginatorSource paginator = document;
        dialog.PrintDocument(paginator.DocumentPaginator, $"SpaceTree — {metadata.RootPath}");
    }

    private static FlowDocument BuildDocument(IReadOnlyList<ExportRow> rows, ReportMetadata metadata, SizeUnitSystem units)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9.5,
            Foreground = Ink,
            Background = Brushes.White,
            PagePadding = new Thickness(48),
        };

        document.Blocks.Add(new Paragraph(new Run("Disk Space Report"))
        {
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });

        document.Blocks.Add(new Paragraph(new Run(metadata.RootPath))
        {
            FontSize = 11,
            Foreground = Muted,
            Margin = new Thickness(0, 0, 0, 12),
        });

        document.Blocks.Add(BuildSummary(metadata, units));
        document.Blocks.Add(BuildTable(rows, units));

        if (rows.Count > MaxPrintedRows)
        {
            document.Blocks.Add(new Paragraph(new Run(
                $"Showing the first {MaxPrintedRows:N0} of {rows.Count:N0} rows. Export to CSV for the complete listing."))
            {
                FontSize = 8.5,
                Foreground = Muted,
                Margin = new Thickness(0, 10, 0, 0),
            });
        }

        return document;
    }

    private static Block BuildSummary(ReportMetadata metadata, SizeUnitSystem units)
    {
        var parts = new List<string>
        {
            $"Total {SizeFormatter.Format(metadata.TotalSize, units)}",
            $"On disk {SizeFormatter.Format(metadata.TotalAllocated, units)}",
            $"{SizeFormatter.FormatCount(metadata.FileCount)} files",
            $"{SizeFormatter.FormatCount(metadata.FolderCount)} folders",
        };

        if (metadata.Volume is { TotalBytes: > 0 } volume)
            parts.Add($"{SizeFormatter.Format(volume.FreeBytes, units)} free of {SizeFormatter.Format(volume.TotalBytes, units)}");

        if (metadata.Errors.Count > 0)
            parts.Add($"{metadata.Errors.Count:N0} folders unreadable");

        parts.Add(metadata.CompletedAt.ToString("f"));

        return new Paragraph(new Run(string.Join("   ·   ", parts)))
        {
            FontSize = 9,
            Foreground = Muted,
            Margin = new Thickness(0, 0, 0, 14),
            BorderBrush = Rule,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 10),
        };
    }

    private static Table BuildTable(IReadOnlyList<ExportRow> rows, SizeUnitSystem units)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0) };

        table.Columns.Add(new TableColumn { Width = new GridLength(3.2, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.85, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.7, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.7, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1.3, GridUnitType.Star) });

        var header = new TableRowGroup();
        var headerRow = new TableRow();
        headerRow.Cells.Add(HeaderCell("Name", TextAlignment.Left));
        headerRow.Cells.Add(HeaderCell("Size", TextAlignment.Right));
        headerRow.Cells.Add(HeaderCell("Allocated", TextAlignment.Right));
        headerRow.Cells.Add(HeaderCell("% Parent", TextAlignment.Right));
        headerRow.Cells.Add(HeaderCell("Files", TextAlignment.Right));
        headerRow.Cells.Add(HeaderCell("Folders", TextAlignment.Right));
        headerRow.Cells.Add(HeaderCell("Last Modified", TextAlignment.Left));
        header.Rows.Add(headerRow);
        table.RowGroups.Add(header);

        var body = new TableRowGroup();
        int printed = 0;

        foreach (var row in rows)
        {
            if (printed++ >= MaxPrintedRows)
                break;

            var tableRow = new TableRow();

            // Indentation carries the hierarchy; a flat printed list of names
            // would be unreadable without it.
            var name = new Paragraph
            {
                Margin = new Thickness(Math.Min(row.Depth, 20) * 9, 1.5, 4, 1.5),
                TextAlignment = TextAlignment.Left,
            };
            name.Inlines.Add(new Run(row.IsFile ? row.Name : row.Name + "\\")
            {
                FontWeight = row.Depth == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = row.IsFile ? Muted : Ink,
            });

            var nameCell = new TableCell(name);
            ApplyRowBorder(nameCell);
            tableRow.Cells.Add(nameCell);

            tableRow.Cells.Add(BodyCell(SizeFormatter.Format(row.Size, units), TextAlignment.Right, row.PercentOfParent));
            tableRow.Cells.Add(BodyCell(SizeFormatter.Format(row.Allocated, units), TextAlignment.Right));
            tableRow.Cells.Add(BodyCell(SizeFormatter.FormatPercent(row.PercentOfParent), TextAlignment.Right));
            tableRow.Cells.Add(BodyCell(row.IsFile ? string.Empty : SizeFormatter.FormatCount(row.FileCount), TextAlignment.Right));
            tableRow.Cells.Add(BodyCell(row.IsFile ? string.Empty : SizeFormatter.FormatCount(row.FolderCount), TextAlignment.Right));
            tableRow.Cells.Add(BodyCell(SizeFormatter.FormatDate(row.LastModified), TextAlignment.Left));

            body.Rows.Add(tableRow);
        }

        table.RowGroups.Add(body);
        return table;
    }

    private static TableCell HeaderCell(string text, TextAlignment alignment) =>
        new(new Paragraph(new Run(text))
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 8.5,
            Foreground = Muted,
            TextAlignment = alignment,
            Margin = new Thickness(4, 2, 4, 4),
        })
        {
            BorderBrush = Rule,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

    private static TableCell BodyCell(string text, TextAlignment alignment, double? barFraction = null)
    {
        var cell = new TableCell(new Paragraph(new Run(text))
        {
            TextAlignment = alignment,
            Margin = new Thickness(4, 1.5, 4, 1.5),
        });

        // A faint tint stands in for the on-screen size bar, so the shape of the
        // data survives the trip to paper.
        if (barFraction is { } fraction && fraction > 0.10)
            cell.Background = BarFill;

        ApplyRowBorder(cell);
        return cell;
    }

    private static void ApplyRowBorder(TableCell cell)
    {
        cell.BorderBrush = Rule;
        cell.BorderThickness = new Thickness(0, 0, 0, 0.4);
    }
}
