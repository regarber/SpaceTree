using System.Linq;
using SpaceTree.Core.Sorting;
using Xunit;

namespace SpaceTree.Core.Tests;

public class NodeSortTests
{
    private static RowKey Folder(string name, long size = 0, long allocated = 0,
        long files = 0, long folders = 0, long written = 0) =>
        new(name, size, allocated, files, folders, written, isFolder: true);

    private static RowKey File(string name, long size = 0, long allocated = 0, long written = 0) =>
        new(name, size, allocated, 0, 0, written, isFolder: false);

    // ── Name comparison ──

    [Theory]
    [InlineData("img2", "img10")]          // digits compare numerically, not textually
    [InlineData("file9", "file10")]
    [InlineData("a", "b")]
    [InlineData("Backup 2", "Backup 19")]
    [InlineData("v1.9", "v1.10")]
    public void CompareNames_OrdersNaturally(string first, string second)
    {
        Assert.True(NodeSort.CompareNames(first, second) < 0);
        Assert.True(NodeSort.CompareNames(second, first) > 0);
    }

    [Theory]
    [InlineData("README", "readme")]
    [InlineData("Windows", "wInDoWs")]
    public void CompareNames_IgnoresCase(string a, string b) =>
        Assert.Equal(0, NodeSort.CompareNames(a, b));

    [Fact]
    public void CompareNames_TreatsLeadingZerosAsEqualValue()
    {
        // "007" and "7" are the same number, so only the surrounding text decides.
        Assert.Equal(0, NodeSort.CompareNames("part007", "part7"));
        Assert.True(NodeSort.CompareNames("part007a", "part7b") < 0);
    }

    [Fact]
    public void CompareNames_HandlesPrefixes() =>
        Assert.True(NodeSort.CompareNames("log", "logs") < 0);

    [Fact]
    public void CompareNames_EqualStringsCompareEqual() =>
        Assert.Equal(0, NodeSort.CompareNames("identical", "identical"));

    // ── Column comparison ──

    [Fact]
    public void Size_DescendingPutsLargestFirst()
    {
        var small = Folder("small", size: 10);
        var large = Folder("large", size: 1000);

        Assert.True(NodeSort.Compare(large, small, SortColumn.Size, SortDirection.Descending) < 0);
        Assert.True(NodeSort.Compare(large, small, SortColumn.Size, SortDirection.Ascending) > 0);
    }

    [Fact]
    public void PercentOfParent_RanksIdenticallyToSize()
    {
        // Siblings share a parent, so the ratio and the raw size order the same way.
        var a = Folder("a", size: 300);
        var b = Folder("b", size: 100);

        Assert.Equal(
            Math.Sign(NodeSort.Compare(a, b, SortColumn.Size, SortDirection.Descending)),
            Math.Sign(NodeSort.Compare(a, b, SortColumn.PercentOfParent, SortDirection.Descending)));
    }

    [Theory]
    [InlineData(SortColumn.Allocated)]
    [InlineData(SortColumn.Files)]
    [InlineData(SortColumn.Folders)]
    [InlineData(SortColumn.LastModified)]
    public void EveryNumericColumn_Sorts(SortColumn column)
    {
        var low = Folder("low", allocated: 1, files: 1, folders: 1, written: 1);
        var high = Folder("high", allocated: 9, files: 9, folders: 9, written: 9);

        Assert.True(NodeSort.Compare(high, low, column, SortDirection.Descending) < 0);
        Assert.True(NodeSort.Compare(high, low, column, SortDirection.Ascending) > 0);
    }

    [Fact]
    public void Name_SortsByNameRegardlessOfSize()
    {
        var big = Folder("zzz", size: long.MaxValue);
        var small = Folder("aaa", size: 0);

        Assert.True(NodeSort.Compare(small, big, SortColumn.Name, SortDirection.Ascending) < 0);
    }

    // ── Tie-breaking ──

    [Fact]
    public void EqualRank_PutsFoldersBeforeFiles()
    {
        var folder = Folder("same", size: 500);
        var file = File("same", size: 500);

        Assert.True(NodeSort.Compare(folder, file, SortColumn.Size, SortDirection.Descending) < 0);
        Assert.True(NodeSort.Compare(folder, file, SortColumn.Size, SortDirection.Ascending) < 0);
    }

    [Fact]
    public void TieBreak_DoesNotFlipWithSortDirection()
    {
        // Reversing the sort must not shuffle items that compare equal, or the
        // list appears to churn for no reason every time a header is clicked.
        var a = Folder("alpha", size: 100);
        var b = Folder("beta", size: 100);

        Assert.True(NodeSort.Compare(a, b, SortColumn.Size, SortDirection.Ascending) < 0);
        Assert.True(NodeSort.Compare(a, b, SortColumn.Size, SortDirection.Descending) < 0);
    }

    [Fact]
    public void Ordering_IsTotalSoResortsAreDeterministic()
    {
        var rows = new[]
        {
            Folder("b", size: 100),
            File("a", size: 100),
            Folder("a", size: 100),
            Folder("c", size: 500),
            File("b", size: 500),
        };

        var forwards = rows.ToArray();
        var backwards = rows.Reverse().ToArray();

        Comparison<RowKey> comparison = (x, y) =>
            NodeSort.Compare(x, y, SortColumn.Size, SortDirection.Descending);

        Array.Sort(forwards, comparison);
        Array.Sort(backwards, comparison);

        // Two different starting orders must converge on the same result.
        Assert.Equal(
            forwards.Select(r => (r.Name, r.IsFolder)),
            backwards.Select(r => (r.Name, r.IsFolder)));
    }

    [Fact]
    public void SortedOrder_IsBiggestFirstThenFoldersThenName()
    {
        var rows = new[]
        {
            File("readme.txt", size: 10),
            Folder("assets", size: 900),
            Folder("src", size: 900),
            File("huge.bin", size: 5000),
        };

        Array.Sort(rows, (x, y) => NodeSort.Compare(x, y, SortColumn.Size, SortDirection.Descending));

        Assert.Equal(new[] { "huge.bin", "assets", "src", "readme.txt" }, rows.Select(r => r.Name));
    }
}
