using System.Linq;
using SpaceTree.Core.Export;
using SpaceTree.Core.Model;
using SpaceTree.Core.Scanning;
using Xunit;

namespace SpaceTree.Core.Tests;

public class ReportRowBuilderTests
{
    private static ScanResult Scan(TempTree tree) =>
        new DirectoryScanner().Scan(new ScanOptions
        {
            RootPath = tree.Root,
            ThreadCount = 4,
            ClusterSizeOverride = 4096,
        });

    /// <summary>A folder holding more children than a report will list individually.</summary>
    private static TempTree BuildWideTree(int childCount = 30)
    {
        var tree = new TempTree();
        for (int i = 0; i < childCount; i++)
        {
            // Descending sizes so the ranking is unambiguous.
            tree.File($@"child{i:D2}\data.bin", (childCount - i) * 1000);
        }
        return tree;
    }

    [Fact]
    public void ListsOnlyTheLargestChildrenPerFolder()
    {
        using var tree = BuildWideTree();
        var result = Scan(tree);

        var rows = ReportRowBuilder.Build(result.Root, new ReportOptions { MaxChildrenPerFolder = 5 });

        var named = rows.Where(r => r.Depth == 1 && !r.IsSummary).Select(r => r.Name).ToList();

        Assert.Equal(5, named.Count);
        Assert.Equal(new[] { "child00", "child01", "child02", "child03", "child04" }, named);
    }

    [Fact]
    public void RemainderIsCollapsedIntoOneSummaryRow()
    {
        using var tree = BuildWideTree();
        var result = Scan(tree);

        var rows = ReportRowBuilder.Build(result.Root, new ReportOptions { MaxChildrenPerFolder = 5 });

        var summaries = rows.Where(r => r.Depth == 1 && r.IsSummary).ToList();

        Assert.Single(summaries);
        Assert.Contains("25", summaries[0].Name);        // 30 children - 5 listed
        Assert.True(summaries[0].Size > 0);
    }

    [Fact]
    public void ListedChildrenPlusSummaryReconcileToTheParentTotal()
    {
        using var tree = BuildWideTree();
        var result = Scan(tree);

        var rows = ReportRowBuilder.Build(result.Root, new ReportOptions { MaxChildrenPerFolder = 5 });

        long root = rows[0].Size;
        long children = rows.Where(r => r.Depth == 1).Sum(r => r.Size);

        // This is the property that makes a summarised report trustworthy: the
        // rows still add up, so no space is quietly unaccounted for.
        Assert.Equal(root, children);
    }

    [Fact]
    public void ReconcilesWhenFilesAreExcluded()
    {
        using var tree = new TempTree();
        tree.File(@"big\payload.bin", 40000);
        tree.File("loose.bin", 9000);          // sits directly in the root
        var result = Scan(tree);

        var rows = ReportRowBuilder.Build(result.Root, new ReportOptions { IncludeFiles = false });

        long children = rows.Where(r => r.Depth == 1).Sum(r => r.Size);

        // The loose file is not listed, so it must be absorbed by the summary row.
        Assert.Equal(rows[0].Size, children);
        Assert.Contains(rows, r => r.IsSummary && r.Size == 9000);
    }

    [Fact]
    public void RespectsMaxDepth()
    {
        using var tree = new TempTree();
        tree.File(@"a\b\c\d\e\deep.bin", 4000);
        var result = Scan(tree);

        var rows = ReportRowBuilder.Build(result.Root, new ReportOptions { MaxDepth = 2 });

        Assert.All(rows, r => Assert.True(r.Depth <= 3, $"depth {r.Depth} exceeded the limit"));
    }

    [Fact]
    public void DoesNotExpandInsignificantBranches()
    {
        using var tree = new TempTree();
        tree.File(@"huge\payload.bin", 10_000_000);
        tree.File(@"tiny\nested\speck.bin", 10);
        var result = Scan(tree);

        // "tiny" is far below 1% of the root, so it is listed but not opened up.
        var rows = ReportRowBuilder.Build(result.Root, new ReportOptions { MinExpandFraction = 0.01 });

        Assert.Contains(rows, r => r.Name == "tiny");
        Assert.DoesNotContain(rows, r => r.Name == "nested");
        Assert.Contains(rows, r => r.Name == "huge");
        Assert.Contains(rows, r => r.Name == "payload.bin");
    }

    [Fact]
    public void SummaryRowsAreFlaggedAndCarryNoItemCounts()
    {
        using var tree = BuildWideTree();
        var result = Scan(tree);

        var rows = ReportRowBuilder.Build(result.Root, new ReportOptions { MaxChildrenPerFolder = 3 });
        var summary = rows.First(r => r.IsSummary);

        Assert.True(summary.IsSummary);
        Assert.False(summary.IsFile);
        Assert.Equal(0, summary.FileCount);
        Assert.Equal(0, summary.FolderCount);
    }

    [Fact]
    public void StaysWithinTheRowBudget()
    {
        using var tree = BuildWideTree(80);
        var result = Scan(tree);

        var rows = ReportRowBuilder.Build(result.Root, new ReportOptions { MaxRows = 10, MaxChildrenPerFolder = 50 });

        Assert.True(rows.Count <= 10, $"produced {rows.Count} rows");
    }

    [Fact]
    public void ProducesFarFewerRowsThanAFullExport()
    {
        using var tree = BuildWideTree(60);
        var result = Scan(tree);

        var full = ExportRowBuilder.Build(result.Root, new ExportOptions { IncludeFiles = true }).ToList();
        var report = ReportRowBuilder.Build(result.Root, new ReportOptions { MaxChildrenPerFolder = 6 });

        Assert.True(report.Count < full.Count / 2,
            $"report {report.Count} rows vs full export {full.Count}");
    }

    [Fact]
    public void RootRowCarriesTheWholeSubtreeTotal()
    {
        using var tree = BuildWideTree(12);
        var result = Scan(tree);

        var rows = ReportRowBuilder.Build(result.Root, new ReportOptions());

        Assert.Equal(0, rows[0].Depth);
        Assert.Equal(result.TotalSize, rows[0].Size);
        Assert.False(rows[0].IsSummary);
    }
}
