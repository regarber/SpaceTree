using System.Linq;
using SpaceTree.Core.Filtering;
using SpaceTree.Core.Model;
using SpaceTree.Core.Scanning;
using Xunit;

namespace SpaceTree.Core.Tests;

public class FilterIndexTests
{
    /// <summary>
    /// A tree with the match buried three levels deep, plus a sibling branch that
    /// contains nothing interesting — the shape that tells us whether ancestors
    /// of a match survive the filter.
    /// </summary>
    private static ScanResult ScanSample(TempTree tree)
    {
        tree.File(@"keep\deep\nested\target.log", 4096);
        tree.File(@"keep\deep\readme.md", 128);
        tree.File(@"unrelated\notes.txt", 256);
        tree.Dir("hollow");

        return new DirectoryScanner().Scan(new ScanOptions { RootPath = tree.Root, ThreadCount = 2 });
    }

    private static DirectoryNode Child(DirectoryNode parent, string name) =>
        parent.Directories.Single(d => d.Name == name);

    [Fact]
    public void NameFilter_KeepsEveryAncestorOfAMatch()
    {
        using var tree = new TempTree();
        var result = ScanSample(tree);

        var index = FilterIndex.Build(result.Root, NodeFilter.Create("*.log"));

        var keep = Child(result.Root, "keep");
        var deep = Child(keep, "deep");
        var nested = Child(deep, "nested");

        // Without this the tree would collapse to nothing and the user would have
        // no path to the file they just searched for.
        Assert.True(index.IsFolderVisible(keep));
        Assert.True(index.IsFolderVisible(deep));
        Assert.True(index.IsFolderVisible(nested));
    }

    [Fact]
    public void NameFilter_HidesBranchesWithNoMatch()
    {
        using var tree = new TempTree();
        var result = ScanSample(tree);

        var index = FilterIndex.Build(result.Root, NodeFilter.Create("*.log"));

        Assert.False(index.IsFolderVisible(Child(result.Root, "unrelated")));
        Assert.False(index.IsFolderVisible(Child(result.Root, "hollow")));
    }

    [Fact]
    public void NameFilter_MatchesFolderNamesThemselves()
    {
        using var tree = new TempTree();
        var result = ScanSample(tree);

        var index = FilterIndex.Build(result.Root, NodeFilter.Create("hollow"));

        Assert.True(index.IsFolderVisible(Child(result.Root, "hollow")));
        Assert.False(index.IsFolderVisible(Child(result.Root, "unrelated")));
    }

    [Fact]
    public void ScanRoot_StaysVisibleEvenWhenNothingMatches()
    {
        using var tree = new TempTree();
        var result = ScanSample(tree);

        var index = FilterIndex.Build(result.Root, NodeFilter.Create("*.nonexistent-extension"));

        // Hiding the root would leave an empty window with no way back.
        Assert.True(index.IsFolderVisible(result.Root));
        Assert.False(index.IsFolderVisible(Child(result.Root, "keep")));
    }

    [Fact]
    public void NoFilter_ShowsEverything()
    {
        using var tree = new TempTree();
        var result = ScanSample(tree);

        var index = FilterIndex.Build(result.Root, NodeFilter.None);

        Assert.False(index.IsActive);
        foreach (var node in result.Root.DescendantsAndSelf())
            Assert.True(index.IsFolderVisible(node));
    }

    [Fact]
    public void MinimumSize_HidesSmallFoldersAndFiles()
    {
        using var tree = new TempTree();
        var result = ScanSample(tree);

        var index = FilterIndex.Build(result.Root, NodeFilter.Create(null, minimumSize: 2048));

        Assert.True(index.IsFolderVisible(Child(result.Root, "keep")));        // holds the 4 KB file
        Assert.False(index.IsFolderVisible(Child(result.Root, "unrelated")));  // only 256 bytes

        var big = new FileEntry("big.bin", 8192, 8192, 0, 0);
        var small = new FileEntry("small.bin", 16, 4096, 0, 0);
        Assert.True(index.IsFileVisible(big));
        Assert.False(index.IsFileVisible(small));
    }

    [Fact]
    public void HideEmptyFolders_DropsFoldersHoldingNoBytes()
    {
        using var tree = new TempTree();
        var result = ScanSample(tree);

        var index = FilterIndex.Build(result.Root, NodeFilter.Create(null, hideEmptyFolders: true));

        Assert.False(index.IsFolderVisible(Child(result.Root, "hollow")));
        Assert.True(index.IsFolderVisible(Child(result.Root, "keep")));
    }

    [Fact]
    public void SizeAndNameRules_BothApply()
    {
        using var tree = new TempTree();
        var result = ScanSample(tree);

        // "keep" contains the match but is filtered out on size anyway.
        var index = FilterIndex.Build(result.Root,
            NodeFilter.Create("*.log", minimumSize: 1024L * 1024L));

        Assert.False(index.IsFolderVisible(Child(result.Root, "keep")));
    }

    [Fact]
    public void FileVisibility_CombinesNameAndSize()
    {
        using var tree = new TempTree();
        var result = ScanSample(tree);

        var index = FilterIndex.Build(result.Root, NodeFilter.Create("*.log"));

        Assert.True(index.IsFileVisible(new FileEntry("trace.log", 100, 4096, 0, 0)));
        Assert.False(index.IsFileVisible(new FileEntry("trace.txt", 100, 4096, 0, 0)));
    }

    [Fact]
    public void DeepTree_DoesNotOverflowTheStack()
    {
        using var tree = new TempTree();

        // The index sweeps the tree iteratively; a recursive implementation would
        // fall over on paths like this, which are exactly what a disk analyser meets.
        string deep = tree.DeepPath(600);
        System.IO.File.WriteAllBytes(@"\\?\" + System.IO.Path.Combine(deep, "buried.log"), new byte[64]);

        var result = new DirectoryScanner().Scan(new ScanOptions { RootPath = tree.Root, ThreadCount = 2 });
        var index = FilterIndex.Build(result.Root, NodeFilter.Create("*.log"));

        Assert.True(index.IsFolderVisible(result.Root));
        Assert.True(result.Root.Directories.Length > 0);
        Assert.True(index.IsFolderVisible(result.Root.Directories[0]));
    }
}
