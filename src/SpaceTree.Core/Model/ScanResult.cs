namespace SpaceTree.Core.Model;

/// <summary>Outcome of a completed (or cancelled) scan.</summary>
public sealed class ScanResult
{
    public required DirectoryNode Root { get; init; }
    public required string RootPath { get; init; }
    public required TimeSpan Duration { get; init; }
    public required bool Cancelled { get; init; }
    public required int ClusterSize { get; init; }
    public required IReadOnlyList<ScanError> Errors { get; init; }
    public VolumeInfo? Volume { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.Now;

    public long TotalSize => Root.TotalSize;
    public long TotalAllocated => Root.TotalAllocated;
    public long FileCount => Root.TotalFileCount;
    public long DirectoryCount => Root.TotalDirectoryCount;
}

/// <summary>Capacity information for the volume a scan root lives on.</summary>
public sealed record VolumeInfo(
    string RootPath,
    string? Label,
    string? FileSystem,
    long TotalBytes,
    long FreeBytes,
    int ClusterSize)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}
