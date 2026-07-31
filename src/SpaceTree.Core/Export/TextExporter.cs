using System.Text;
using SpaceTree.Core.Util;

namespace SpaceTree.Core.Export;

/// <summary>Writes export rows as an indented plain-text tree, aligned for a fixed-width font.</summary>
public static class TextExporter
{
    public static void Write(TextWriter writer, IEnumerable<ExportRow> rows, string? title = null,
        SizeUnitSystem units = SizeUnitSystem.Binary)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);

        if (!string.IsNullOrEmpty(title))
        {
            writer.WriteLine(title);
            writer.WriteLine(new string('=', Math.Min(title.Length, 100)));
            writer.WriteLine();
        }

        writer.WriteLine($"{"Size",12}  {"Allocated",12}  {"% Parent",9}  {"Files",9}  {"Folders",9}  {"Last Modified",16}  Name");
        writer.WriteLine(new string('-', 100));

        foreach (var row in rows)
        {
            var indent = new string(' ', Math.Min(row.Depth, 40) * 2);
            string name = indent + (row.IsFile ? row.Name : row.Name + "\\");

            writer.WriteLine(
                $"{SizeFormatter.Format(row.Size, units),12}  " +
                $"{SizeFormatter.Format(row.Allocated, units),12}  " +
                $"{SizeFormatter.FormatPercent(row.PercentOfParent),9}  " +
                $"{(row.IsFile ? string.Empty : SizeFormatter.FormatCount(row.FileCount)),9}  " +
                $"{(row.IsFile ? string.Empty : SizeFormatter.FormatCount(row.FolderCount)),9}  " +
                $"{SizeFormatter.FormatDate(row.LastModified),16}  " +
                name);
        }
    }

    public static void WriteToFile(string path, IEnumerable<ExportRow> rows, string? title = null,
        SizeUnitSystem units = SizeUnitSystem.Binary)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        Write(writer, rows, title, units);
    }
}
