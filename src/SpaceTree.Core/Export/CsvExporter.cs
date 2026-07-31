using System.Globalization;
using System.Text;

namespace SpaceTree.Core.Export;

/// <summary>Writes export rows as RFC 4180 CSV.</summary>
public static class CsvExporter
{
    public static void Write(TextWriter writer, IEnumerable<ExportRow> rows, char delimiter = ',')
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);

        var culture = CultureInfo.InvariantCulture;

        writer.Write("Level");
        writer.Write(delimiter); writer.Write("Name");
        writer.Write(delimiter); writer.Write("Full Path");
        writer.Write(delimiter); writer.Write("Type");
        writer.Write(delimiter); writer.Write("Size (bytes)");
        writer.Write(delimiter); writer.Write("Allocated (bytes)");
        writer.Write(delimiter); writer.Write("% of Parent");
        writer.Write(delimiter); writer.Write("Files");
        writer.Write(delimiter); writer.Write("Folders");
        writer.Write(delimiter); writer.Write("Last Modified");
        writer.Write("\r\n");

        foreach (var row in rows)
        {
            writer.Write(row.Depth.ToString(culture));
            writer.Write(delimiter); WriteField(writer, row.Name, delimiter);
            writer.Write(delimiter); WriteField(writer, row.FullPath, delimiter);
            writer.Write(delimiter); writer.Write(row.IsFile ? "File" : "Folder");
            writer.Write(delimiter); writer.Write(row.Size.ToString(culture));
            writer.Write(delimiter); writer.Write(row.Allocated.ToString(culture));
            writer.Write(delimiter); writer.Write((row.PercentOfParent * 100).ToString("F2", culture));
            writer.Write(delimiter); writer.Write(row.FileCount.ToString(culture));
            writer.Write(delimiter); writer.Write(row.FolderCount.ToString(culture));
            writer.Write(delimiter);
            writer.Write(row.LastModified == DateTime.MinValue
                ? string.Empty
                : row.LastModified.ToString("yyyy-MM-dd HH:mm:ss", culture));
            writer.Write("\r\n");
        }
    }

    public static void WriteToFile(string path, IEnumerable<ExportRow> rows, char delimiter = ',')
    {
        // UTF-8 with BOM so Excel opens non-ASCII file names correctly.
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        Write(writer, rows, delimiter);
    }

    private static void WriteField(TextWriter writer, string value, char delimiter)
    {
        if (string.IsNullOrEmpty(value))
            return;

        bool needsQuotes = value.IndexOf(delimiter) >= 0 ||
                           value.IndexOf('"') >= 0 ||
                           value.IndexOf('\n') >= 0 ||
                           value.IndexOf('\r') >= 0;

        if (!needsQuotes)
        {
            writer.Write(value);
            return;
        }

        writer.Write('"');
        foreach (char c in value)
        {
            if (c == '"')
                writer.Write('"');
            writer.Write(c);
        }
        writer.Write('"');
    }
}
