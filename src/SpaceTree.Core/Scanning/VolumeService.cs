using System.Runtime.InteropServices;
using SpaceTree.Core.Model;
using SpaceTree.Core.Native;

namespace SpaceTree.Core.Scanning;

/// <summary>Volume-level queries: cluster size, capacity, and the drive list for the picker.</summary>
public static class VolumeService
{
    public const int FallbackClusterSize = 4096;

    /// <summary>
    /// Bytes per cluster for the volume containing <paramref name="path"/>.
    /// Falls back to 4 KB, which is correct for the overwhelming majority of
    /// NTFS volumes, when the volume refuses to answer.
    /// </summary>
    public static int GetClusterSize(string path)
    {
        string? root = TryGetVolumeRoot(path);
        if (root is null)
            return FallbackClusterSize;

        if (NativeMethods.GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
        {
            long cluster = (long)sectorsPerCluster * bytesPerSector;
            if (cluster > 0 && cluster <= int.MaxValue)
                return (int)cluster;
        }

        return FallbackClusterSize;
    }

    /// <summary>Capacity and free space for the volume hosting <paramref name="path"/>.</summary>
    public static VolumeInfo? GetVolumeInfo(string path)
    {
        string? root = TryGetVolumeRoot(path);
        if (root is null)
            return null;

        long total = 0, free = 0;
        if (NativeMethods.GetDiskFreeSpaceEx(root, out _, out ulong totalBytes, out ulong freeBytes))
        {
            total = (long)Math.Min(totalBytes, long.MaxValue);
            free = (long)Math.Min(freeBytes, long.MaxValue);
        }

        string? label = null, fileSystem = null;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                label = drive.VolumeLabel;
                fileSystem = drive.DriveFormat;
                if (total == 0)
                {
                    total = drive.TotalSize;
                    free = drive.TotalFreeSpace;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unlabelled, unmounted or network volume: capacity numbers above still stand.
        }

        return new VolumeInfo(root, label, fileSystem, total, free, GetClusterSize(root));
    }

    /// <summary>Ready fixed/removable/network drives, for the drive picker.</summary>
    public static IReadOnlyList<DriveSummary> GetDrives()
    {
        var list = new List<DriveSummary>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { return list; }

        foreach (var d in drives)
        {
            try
            {
                if (!d.IsReady)
                {
                    list.Add(new DriveSummary(d.Name, null, d.DriveType, 0, 0, false));
                    continue;
                }
                list.Add(new DriveSummary(d.Name, string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
                    d.DriveType, d.TotalSize, d.TotalFreeSpace, true));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                list.Add(new DriveSummary(d.Name, null, d.DriveType, 0, 0, false));
            }
        }
        return list;
    }

    private static string? TryGetVolumeRoot(string path)
    {
        try
        {
            path = LongPath.ToDisplay(path);
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return null;

            // GetDiskFreeSpace requires a trailing separator on the root.
            return root.EndsWith('\\') ? root : root + "\\";
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }
}

public sealed record DriveSummary(
    string Name,
    string? Label,
    DriveType DriveType,
    long TotalBytes,
    long FreeBytes,
    bool IsReady)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;

    public string DisplayName =>
        Label is { Length: > 0 } ? $"{Name.TrimEnd('\\')}  {Label}" : Name.TrimEnd('\\');
}
