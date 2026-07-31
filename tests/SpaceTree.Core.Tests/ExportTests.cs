using SpaceTree.Core.Export;
using SpaceTree.Core.Filtering;
using SpaceTree.Core.Model;
using SpaceTree.Core.Scanning;
using Xunit;

namespace SpaceTree.Core.Tests;

public class ExportTests
{
    private static ScanResult ScanTree(TempTree tree) =>
        new DirectoryScanner().Scan(new ScanOptions
        {
            RootPath = tree.Root,
            ThreadCount = 4,
            ClusterSizeOverride = 4096,
        });

    private static TempTree BuildSample()
    {
        var tree = new TempTree();
        tree.File(@"docs\report.pdf", 5000);
        tree.File(@"docs\notes.txt", 100);
        tree.File(@"media\clip.mp4", 20000);
        tree.File("readme.md", 50);
        return tree;
    }

    [Fact]
    public void RowBuilder_EmitsRootFirstAndSortsBySizeDescending()
    {
        using var tree = BuildSample();
        var result = ScanTree(tree);

        var rows = ExportRowBuilder.Build(result.Root, new ExportOptions()).ToList();

        Assert.Equal(0, rows[0].Depth);
        Assert.Equal(result.TotalSize, rows[0].Size);
        Assert.Equal("media", rows[1].Name);   // 20000 bytes
        Assert.Equal("docs", rows[2].Name);    // 5100 bytes
        Assert.DoesNotContain(rows, r => r.IsFile);
    }

    [Fact]
    public void RowBuilder_IncludesFilesWhenAsked()
    {
        using var tree = BuildSample();
        var result = ScanTree(tree);

        var rows = ExportRowBuilder.Build(result.Root, new ExportOptions { IncludeFiles = true }).ToList();

        Assert.Contains(rows, r => r.IsFile && r.Name == "clip.mp4" && r.Size == 20000);
        Assert.Contains(rows, r => r.IsFile && r.Name == "readme.md");
        Assert.All(rows.Where(r => r.IsFile), r => Assert.True(File.Exists(r.FullPath), r.FullPath));
    }

    [Fact]
    public void RowBuilder_RespectsMaxDepth()
    {
        using var tree = BuildSample();
        var result = ScanTree(tree);

        var rows = ExportRowBuilder.Build(result.Root, new ExportOptions { MaxDepth = 1, IncludeFiles = true }).ToList();

        Assert.All(rows, r => Assert.True(r.Depth <= 1));
        Assert.Contains(rows, r => r.Name == "docs");
        Assert.DoesNotContain(rows, r => r.Name == "report.pdf"); // depth 2
    }

    [Fact]
    public void RowBuilder_AppliesMinimumSizeFilter()
    {
        using var tree = BuildSample();
        var result = ScanTree(tree);

        var options = new ExportOptions { Filter = NodeFilter.Create(null, minimumSize: 10000) };
        var rows = ExportRowBuilder.Build(result.Root, options).ToList();

        Assert.Contains(rows, r => r.Name == "media");
        Assert.DoesNotContain(rows, r => r.Name == "docs"); // only 5100 bytes
    }

    [Fact]
    public void RowBuilder_KeepsFoldersThatContainAMatch()
    {
        using var tree = BuildSample();
        var result = ScanTree(tree);

        var options = new ExportOptions { IncludeFiles = true, Filter = NodeFilter.Create("*.mp4") };
        var rows = ExportRowBuilder.Build(result.Root, options).ToList();

        Assert.Contains(rows, r => r.Name == "media");        // kept: a descendant matches
        Assert.Contains(rows, r => r.Name == "clip.mp4");
        Assert.DoesNotContain(rows, r => r.Name == "docs");   // nothing under it matches
    }

    [Fact]
    public void Csv_HasHeaderAndOneLinePerRow()
    {
        using var tree = BuildSample();
        var result = ScanTree(tree);
        var rows = ExportRowBuilder.Build(result.Root, new ExportOptions()).ToList();

        var writer = new StringWriter();
        CsvExporter.Write(writer, rows);
        string[] lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Level,Name,Full Path,Type,Size (bytes)", lines[0]);
        Assert.Equal(rows.Count + 1, lines.Length);
        Assert.Contains(",Folder,", lines[1]);
    }

    [Fact]
    public void Csv_QuotesFieldsContainingDelimitersAndQuotes()
    {
        var rows = new[]
        {
            new ExportRow(0, "a,b", @"C:\x\a,b", false, 1, 4096, 1, 0, 0, DateTime.MinValue),
            new ExportRow(1, "say \"hi\"", @"C:\x\say ""hi""", true, 2, 4096, 0.5, 1, 0, DateTime.MinValue),
        };

        var writer = new StringWriter();
        CsvExporter.Write(writer, rows);
        string csv = writer.ToString();

        Assert.Contains("\"a,b\"", csv);
        Assert.Contains("\"say \"\"hi\"\"\"", csv);
    }

    [Fact]
    public void Csv_WritesEmptyDateForUnknownTimestamps()
    {
        var rows = new[] { new ExportRow(0, "n", "p", false, 0, 0, 0, 0, 0, DateTime.MinValue) };

        var writer = new StringWriter();
        CsvExporter.Write(writer, rows);

        Assert.EndsWith(",\r\n", writer.ToString());
    }

    [Fact]
    public void Text_IndentsByDepth()
    {
        using var tree = BuildSample();
        var result = ScanTree(tree);
        var rows = ExportRowBuilder.Build(result.Root, new ExportOptions()).ToList();

        var writer = new StringWriter();
        TextExporter.Write(writer, rows, "Sample report");
        string text = writer.ToString();

        Assert.Contains("Sample report", text);
        Assert.Contains("  media\\", text);   // depth 1 -> two spaces
        Assert.Contains("Last Modified", text);
    }

    [Fact]
    public void Html_IsSelfContainedAndEscapesUserContent()
    {
        var rows = new[]
        {
            new ExportRow(0, "<script>alert(1)</script>", @"C:\evil", false, 100, 4096, 1, 1, 0, DateTime.Now),
        };
        var metadata = new ReportMetadata { RootPath = @"C:\evil & co", TotalSize = 100 };

        var writer = new StringWriter();
        HtmlReportExporter.Write(writer, rows, metadata);
        string html = writer.ToString();

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.EndsWith("</body></html>", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("C:\\evil &amp; co", html);
        Assert.DoesNotContain("http://", html);   // no external resources
        Assert.Contains("@media print", html);
    }

    [Fact]
    public void Html_FromScanResult_CarriesTotals()
    {
        using var tree = BuildSample();
        var result = ScanTree(tree);
        var metadata = ReportMetadata.FromScan(result);

        var writer = new StringWriter();
        HtmlReportExporter.Write(writer, ExportRowBuilder.Build(result.Root, new ExportOptions()), metadata);
        string html = writer.ToString();

        Assert.Contains("Total size", html);
        Assert.Contains("Disk Space Report", html);
        Assert.Equal(result.TotalSize, metadata.TotalSize);
    }

    [Fact]
    public void CsvFile_RoundTripsToDisk()
    {
        using var tree = BuildSample();
        var result = ScanTree(tree);
        string path = Path.Combine(tree.Root, "export.csv");

        CsvExporter.WriteToFile(path, ExportRowBuilder.Build(result.Root, new ExportOptions()));

        Assert.True(File.Exists(path));
        Assert.Contains("Full Path", File.ReadAllText(path));
    }
}
