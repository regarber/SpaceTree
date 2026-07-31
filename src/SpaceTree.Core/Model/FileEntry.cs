namespace SpaceTree.Core.Model;

/// <summary>
/// A single file. This is a value type stored in a flat array on the owning
/// directory: on a full system drive there can be several million of these, and
/// making each one a heap object would cost ~3x the memory and wreck GC pauses.
/// </summary>
public readonly struct FileEntry
{
    public FileEntry(string name, long size, long allocated, long lastWriteFileTime, uint attributes)
    {
        Name = name;
        Size = size;
        Allocated = allocated;
        LastWriteFileTime = lastWriteFileTime;
        Attributes = attributes;
    }

    /// <summary>File name including extension, without any directory part.</summary>
    public string Name { get; }

    /// <summary>Logical size in bytes (what the file claims to be).</summary>
    public long Size { get; }

    /// <summary>Size actually occupied on disk, cluster-rounded (and compression-aware).</summary>
    public long Allocated { get; }

    /// <summary>Last write time as a raw Windows FILETIME (comparable as an integer).</summary>
    public long LastWriteFileTime { get; }

    public uint Attributes { get; }

    public bool IsCompressed => (Attributes & 0x00000800) != 0;
    public bool IsSparse => (Attributes & 0x00000200) != 0;
    public bool IsHidden => (Attributes & 0x00000002) != 0;
    public bool IsSystem => (Attributes & 0x00000004) != 0;
    public bool IsReparsePoint => (Attributes & 0x00000400) != 0;

    public DateTime LastWriteUtc => FileTimes.ToDateTimeUtc(LastWriteFileTime);
}

public static class FileTimes
{
    /// <summary>Converts a raw FILETIME to UTC, tolerating the garbage values some drivers report.</summary>
    public static DateTime ToDateTimeUtc(long fileTime)
    {
        if (fileTime <= 0)
            return DateTime.MinValue;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    public static DateTime ToDateTimeLocal(long fileTime)
    {
        var utc = ToDateTimeUtc(fileTime);
        return utc == DateTime.MinValue ? DateTime.MinValue : utc.ToLocalTime();
    }
}
