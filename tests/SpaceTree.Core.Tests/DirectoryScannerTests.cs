using SpaceTree.Core.Model;
using SpaceTree.Core.Scanning;
using Xunit;

namespace SpaceTree.Core.Tests;

public class DirectoryScannerTests
{
    private const int Cluster = 4096;

    private static ScanResult Scan(string root, Action<ScanOptionsBuilder>? configure = null)
    {
        var builder = new ScanOptionsBuilder(root);
        configure?.Invoke(builder);
        return new DirectoryScanner().Scan(builder.Build());
    }

    private sealed class ScanOptionsBuilder(string root)
    {
        public int Threads { get; set; } = 4;
        public bool RetainFiles { get; set; } = true;
        public bool FollowReparsePoints { get; set; }
        public int ClusterSize { get; set; } = Cluster;

        public ScanOptions Build() => new()
        {
            RootPath = root,
            ThreadCount = Threads,
            RetainFileEntries = RetainFiles,
            FollowReparsePoints = FollowReparsePoints,
            ClusterSizeOverride = ClusterSize,
        };
    }

    [Fact]
    public void EmptyDirectory_HasZeroTotals()
    {
        using var tree = new TempTree();

        var result = Scan(tree.Root);

        Assert.Equal(0, result.TotalSize);
        Assert.Equal(0, result.FileCount);
        Assert.Equal(0, result.DirectoryCount);
        Assert.False(result.Cancelled);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FlatDirectory_SumsFileSizes()
    {
        using var tree = new TempTree();
        tree.File("a.bin", 1000);
        tree.File("b.bin", 2500);
        tree.File("c.bin", 0);

        var result = Scan(tree.Root);

        Assert.Equal(3500, result.TotalSize);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(0, result.DirectoryCount);
        Assert.Equal(3, result.Root.OwnFileCount);
        Assert.Equal(3500, result.Root.OwnSize);
    }

    [Fact]
    public void NestedDirectories_RollUpIntoAncestors()
    {
        using var tree = new TempTree();
        tree.File("root.bin", 100);
        tree.File(@"a\a1.bin", 200);
        tree.File(@"a\b\b1.bin", 400);
        tree.File(@"a\b\c\c1.bin", 800);

        var result = Scan(tree.Root);

        Assert.Equal(1500, result.TotalSize);
        Assert.Equal(4, result.FileCount);
        Assert.Equal(3, result.DirectoryCount); // a, a\b, a\b\c

        var a = FindChild(result.Root, "a");
        Assert.Equal(1400, a.TotalSize);
        Assert.Equal(3, a.TotalFileCount);
        Assert.Equal(2, a.TotalDirectoryCount);
        Assert.Equal(200, a.OwnSize);

        var b = FindChild(a, "b");
        Assert.Equal(1200, b.TotalSize);
        Assert.Equal(1, b.TotalDirectoryCount);

        var c = FindChild(b, "c");
        Assert.Equal(800, c.TotalSize);
        Assert.Equal(0, c.TotalDirectoryCount);
        Assert.Equal(1, c.TotalFileCount);
    }

    [Fact]
    public void AllocatedSize_ReflectsRealOnDiskUsage()
    {
        using var tree = new TempTree();
        tree.File("empty.bin", 0);
        tree.File("tiny.bin", 1);
        tree.File("one-cluster.bin", Cluster);
        tree.File("just-over.bin", Cluster + 1);

        var result = Scan(tree.Root);
        int cluster = VolumeService.GetClusterSize(tree.Root);
        var allocated = result.Root.Files.ToDictionary(f => f.Name, f => f.Allocated);

        Assert.Equal(1 + Cluster + Cluster + 1, result.TotalSize);
        Assert.Equal(0, allocated["empty.bin"]);

        // No file ever occupies more than cluster rounding would suggest. It can
        // occupy less: NTFS keeps very small files resident inside the MFT record,
        // and reports their real footprint there (a 1-byte file comes back as 8
        // bytes, not a whole 4 KB cluster). This is what Explorer shows as "size
        // on disk", and it is why the scanner trusts the filesystem instead of
        // multiplying file counts by the cluster size.
        foreach (var file in result.Root.Files)
        {
            Assert.True(file.Allocated <= DirectoryScanner.RoundUpToCluster(file.Size, cluster),
                $"{file.Name}: allocated {file.Allocated} exceeds cluster-rounded {file.Size}");
            Assert.True(file.Allocated >= 0);
        }

        // Past the resident threshold a file occupies exactly its rounded-up size.
        Assert.Equal(DirectoryScanner.RoundUpToCluster(Cluster, cluster), allocated["one-cluster.bin"]);
        Assert.Equal(DirectoryScanner.RoundUpToCluster(Cluster + 1, cluster), allocated["just-over.bin"]);

        Assert.Equal(result.Root.Files.Sum(f => f.Allocated), result.TotalAllocated);
    }

    [Theory]
    [InlineData(0, 4096, 0)]
    [InlineData(1, 4096, 4096)]
    [InlineData(4095, 4096, 4096)]
    [InlineData(4096, 4096, 4096)]
    [InlineData(4097, 4096, 8192)]
    [InlineData(10_000, 4096, 12288)]
    [InlineData(10_000, 512, 10240)]
    [InlineData(-5, 4096, 0)]
    public void RoundUpToCluster_IsExact(long size, int cluster, long expected) =>
        Assert.Equal(expected, DirectoryScanner.RoundUpToCluster(size, cluster));

    [Fact]
    public void PercentOfParent_ReflectsSizeShare()
    {
        using var tree = new TempTree();
        tree.File(@"big\x.bin", 750);
        tree.File(@"small\y.bin", 250);

        var result = Scan(tree.Root);

        Assert.Equal(1d, result.Root.PercentOfParent);
        Assert.Equal(0.75, FindChild(result.Root, "big").PercentOfParent, 6);
        Assert.Equal(0.25, FindChild(result.Root, "small").PercentOfParent, 6);
    }

    [Fact]
    public void LastModified_TakesNewestInSubtree()
    {
        using var tree = new TempTree();
        string oldFile = tree.File(@"a\old.bin", 10);
        string newFile = tree.File(@"a\b\recent.bin", 10);

        var oldTime = new DateTime(2001, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var midTime = new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newTime = new DateTime(2023, 9, 9, 8, 30, 0, DateTimeKind.Utc);

        // A folder's own timestamp counts as a change within its subtree, so the
        // directories are back-dated too, otherwise "now" would dominate.
        tree.SetLastWrite(newFile, newTime);
        tree.SetLastWrite(Path.Combine(tree.Root, @"a\b"), midTime);
        tree.SetLastWrite(oldFile, oldTime);
        tree.SetLastWrite(Path.Combine(tree.Root, "a"), midTime);
        tree.SetLastWrite(tree.Root, midTime);

        var result = Scan(tree.Root);

        var a = FindChild(result.Root, "a");
        var b = FindChild(a, "b");

        Assert.Equal(newTime, b.LastWriteUtc, TimeSpan.FromSeconds(2));
        Assert.Equal(newTime, a.LastWriteUtc, TimeSpan.FromSeconds(2));
        Assert.Equal(newTime, result.Root.LastWriteUtc, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void LastModified_IncludesFolderTimestamps()
    {
        using var tree = new TempTree();
        tree.Dir(@"a\recently-touched");   // no files at all
        tree.SetLastWrite(tree.Root, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = Scan(tree.Root);

        // An empty but freshly created subfolder still counts as recent activity.
        Assert.True(result.Root.LastWriteUtc > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void FileEntries_AreRetainedWithSizesAndAttributes()
    {
        using var tree = new TempTree();
        tree.File("keep.bin", 1234);

        var result = Scan(tree.Root);

        var file = Assert.Single(result.Root.Files);
        Assert.Equal("keep.bin", file.Name);
        Assert.Equal(1234, file.Size);
        Assert.Equal(4096, file.Allocated);
    }

    [Fact]
    public void RetainFileEntriesOff_KeepsCountsButDropsEntries()
    {
        using var tree = new TempTree();
        tree.File("a.bin", 500);
        tree.File("b.bin", 500);

        var result = Scan(tree.Root, o => o.RetainFiles = false);

        Assert.Empty(result.Root.Files);
        Assert.Equal(2, result.Root.OwnFileCount);
        Assert.Equal(1000, result.TotalSize);
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public void ThreadCountVariations_ProduceIdenticalTotals()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 12; i++)
        {
            tree.File($@"d{i}\f1.bin", 100 * (i + 1));
            tree.File($@"d{i}\sub\f2.bin", 50);
        }

        var single = Scan(tree.Root, o => o.Threads = 1);
        var many = Scan(tree.Root, o => o.Threads = 16);

        Assert.Equal(single.TotalSize, many.TotalSize);
        Assert.Equal(single.TotalAllocated, many.TotalAllocated);
        Assert.Equal(single.FileCount, many.FileCount);
        Assert.Equal(single.DirectoryCount, many.DirectoryCount);
    }

    [Fact]
    public void LongPaths_BeyondMaxPath_AreScanned()
    {
        using var tree = new TempTree();
        string deep = tree.DeepPath(400);
        Assert.True(deep.Length > 260, $"expected a >260 char path, got {deep.Length}");

        using (var stream = new FileStream(@"\\?\" + Path.Combine(deep, "deep.bin"),
                   FileMode.Create, FileAccess.Write))
        {
            stream.Write(new byte[777]);
        }

        var result = Scan(tree.Root);

        Assert.Equal(777, result.TotalSize);
        Assert.Equal(1, result.FileCount);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FullPath_RoundTripsThroughTheTree()
    {
        using var tree = new TempTree();
        tree.File(@"alpha\beta\gamma.bin", 10);

        var result = Scan(tree.Root);
        var beta = FindChild(FindChild(result.Root, "alpha"), "beta");

        Assert.Equal(Path.Combine(tree.Root, "alpha", "beta"), beta.FullPath);
        Assert.True(Directory.Exists(beta.FullPath));
    }

    [Fact]
    public void MissingRoot_ReportsErrorInsteadOfThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "SpaceTreeTests", "does-not-exist-" + Guid.NewGuid());

        var result = Scan(missing);

        Assert.Equal(0, result.TotalSize);
        Assert.NotEmpty(result.Errors);
        Assert.True(result.Root.HasError);
    }

    [Fact]
    public void Cancellation_StopsScanAndFlagsResult()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 40; i++)
            tree.File($@"d{i}\sub{i}\f.bin", 10);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancelled before any work is dequeued

        var result = new DirectoryScanner().Scan(
            new ScanOptions { RootPath = tree.Root, ThreadCount = 4, ClusterSizeOverride = Cluster },
            cts.Token);

        Assert.True(result.Cancelled);
    }

    [Fact]
    public void Progress_ReportsMonotonicCounters()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 60; i++)
            tree.File($@"d{i}\f.bin", 1000);

        var scanner = new DirectoryScanner { ProgressInterval = TimeSpan.FromMilliseconds(1) };
        var snapshots = new List<ScanProgress>();
        scanner.ProgressChanged += (_, p) => { lock (snapshots) snapshots.Add(p); };

        var result = scanner.Scan(new ScanOptions
        {
            RootPath = tree.Root,
            ThreadCount = 2,
            ClusterSizeOverride = Cluster,
        });

        Assert.NotEmpty(snapshots);
        var last = snapshots[^1];
        Assert.Equal(60, last.FilesScanned);
        Assert.Equal(61, last.DirectoriesScanned); // 60 subdirectories + the root
        Assert.Equal(result.TotalSize, last.BytesScanned);
        Assert.InRange(last.EstimatedFraction, 0d, 1d);
    }

    [Fact]
    public void DescendantsAndSelf_VisitsEveryDirectoryOnce()
    {
        using var tree = new TempTree();
        tree.Dir(@"a\b\c");
        tree.Dir(@"a\d");
        tree.Dir("e");

        var result = Scan(tree.Root);
        var names = result.Root.DescendantsAndSelf().Select(n => n.Name).ToList();

        Assert.Equal(6, names.Count); // root + a + b + c + d + e
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("c", names);
    }

    private static DirectoryNode FindChild(DirectoryNode parent, string name) =>
        parent.Directories.SingleOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"'{name}' not found under '{parent.Name}'.");
}
