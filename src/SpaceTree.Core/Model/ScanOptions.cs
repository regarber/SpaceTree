namespace SpaceTree.Core.Model;

/// <summary>Everything that changes how a scan behaves.</summary>
public sealed class ScanOptions
{
    /// <summary>Directory or drive to scan, e.g. "C:\" or "D:\Projects".</summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Worker count. More threads help enormously on SSD/NVMe and can hurt on
    /// spinning rust; the default is one per logical core, capped at 32.
    /// </summary>
    public int ThreadCount { get; init; } = DefaultThreadCount;

    /// <summary>
    /// Keep per-file records so the tree can list individual files. Turning this
    /// off keeps counts and sizes exact but drops the entries, saving roughly
    /// 40 bytes plus the name per file on huge volumes.
    /// </summary>
    public bool RetainFileEntries { get; init; } = true;

    /// <summary>
    /// Recurse into junctions and symbolic links. Off by default: reparse points
    /// double-count content and can form cycles (C:\Documents and Settings).
    /// </summary>
    public bool FollowReparsePoints { get; init; }

    /// <summary>
    /// Cluster size used by the compatibility enumeration path to approximate
    /// allocated size. The fast path reports the filesystem's exact allocation
    /// and ignores this. Left at 0 the scanner asks the volume.
    /// </summary>
    public int ClusterSizeOverride { get; init; }

    /// <summary>Maximum number of access errors kept for the report. Scanning continues past the cap.</summary>
    public int MaxRecordedErrors { get; init; } = 2000;

    public static int DefaultThreadCount => Math.Clamp(Environment.ProcessorCount, 1, 32);
}
