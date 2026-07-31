using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace SpaceTree.Core.Model;

/// <summary>
/// One directory in the scanned tree. Totals are rolled up incrementally by the
/// scanner (each completed directory adds itself to every ancestor) so the UI can
/// render a meaningful, growing tree while the scan is still running.
/// </summary>
public sealed class DirectoryNode
{
    private static readonly DirectoryNode[] EmptyDirs = Array.Empty<DirectoryNode>();
    private static readonly FileEntry[] EmptyFiles = Array.Empty<FileEntry>();

    private long _totalSize;
    private long _totalAllocated;
    private long _totalFileCount;
    private long _totalDirectoryCount;
    private long _lastWriteFileTime;

    public DirectoryNode(string name, DirectoryNode? parent)
    {
        Name = name;
        Parent = parent;
        Directories = EmptyDirs;
        Files = EmptyFiles;
    }

    /// <summary>Directory name only, except for the scan root which holds its full path.</summary>
    public string Name { get; }

    public DirectoryNode? Parent { get; }

    /// <summary>Immediate subdirectories. Replaced once, by the scanner, when this directory is enumerated.</summary>
    public DirectoryNode[] Directories { get; internal set; }

    /// <summary>Immediate files. Empty when <see cref="ScanOptions.RetainFileEntries"/> is off.</summary>
    public FileEntry[] Files { get; internal set; }

    /// <summary>Logical bytes of the files directly in this directory.</summary>
    public long OwnSize { get; internal set; }

    /// <summary>On-disk bytes of the files directly in this directory.</summary>
    public long OwnAllocated { get; internal set; }

    /// <summary>Number of files directly in this directory (accurate even if entries are not retained).</summary>
    public int OwnFileCount { get; internal set; }

    /// <summary>Logical bytes for the whole subtree, including this directory.</summary>
    public long TotalSize => Volatile.Read(ref _totalSize);

    /// <summary>On-disk bytes for the whole subtree, including this directory.</summary>
    public long TotalAllocated => Volatile.Read(ref _totalAllocated);

    /// <summary>Files in the whole subtree.</summary>
    public long TotalFileCount => Volatile.Read(ref _totalFileCount);

    /// <summary>Subdirectories in the whole subtree (excluding this directory itself).</summary>
    public long TotalDirectoryCount => Volatile.Read(ref _totalDirectoryCount);

    /// <summary>Most recent write time anywhere in the subtree, as a raw FILETIME.</summary>
    public long LastWriteFileTime => Volatile.Read(ref _lastWriteFileTime);

    public DateTime LastWriteUtc => FileTimes.ToDateTimeUtc(LastWriteFileTime);

    /// <summary>True when the directory could not be fully read (usually access denied).</summary>
    public bool HasError { get; internal set; }

    /// <summary>Win32 error code when <see cref="HasError"/> is set.</summary>
    public int ErrorCode { get; internal set; }

    /// <summary>True for junctions / symlinks. Not recursed into unless explicitly enabled.</summary>
    public bool IsReparsePoint { get; internal set; }

    /// <summary>Depth below the scan root (root is 0).</summary>
    public int Depth { get; internal set; }

    public bool IsRoot => Parent is null;

    /// <summary>Reconstructs the full path by walking up to the root.</summary>
    public string FullPath
    {
        get
        {
            if (Parent is null)
                return Name;

            // Depth is small, so a stack-walk plus one StringBuilder beats caching
            // a string on every node (which would cost ~100 bytes per directory).
            var stack = new List<string>(Depth + 1);
            for (DirectoryNode? n = this; n is not null; n = n.Parent)
                stack.Add(n.Name);
            stack.Reverse();

            var sb = new StringBuilder(stack.Count * 12);
            for (int i = 0; i < stack.Count; i++)
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '\\')
                    sb.Append('\\');
                sb.Append(stack[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>Share of the parent's total size, 0..1. The root is always 1.</summary>
    public double PercentOfParent
    {
        get
        {
            var parent = Parent;
            if (parent is null)
                return 1d;
            long p = parent.TotalSize;
            return p <= 0 ? 0d : (double)TotalSize / p;
        }
    }

    /// <summary>Bytes held by files directly here rather than in a subdirectory ("&lt;Files&gt;" pseudo-row).</summary>
    public bool HasOwnFiles => OwnFileCount > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddTotals(long size, long allocated, long files, long directories)
    {
        Interlocked.Add(ref _totalSize, size);
        Interlocked.Add(ref _totalAllocated, allocated);
        Interlocked.Add(ref _totalFileCount, files);
        Interlocked.Add(ref _totalDirectoryCount, directories);
    }

    internal void MergeLastWrite(long fileTime)
    {
        if (fileTime <= 0)
            return;

        long snapshot;
        do
        {
            snapshot = Volatile.Read(ref _lastWriteFileTime);
            if (fileTime <= snapshot)
                return;
        }
        while (Interlocked.CompareExchange(ref _lastWriteFileTime, fileTime, snapshot) != snapshot);
    }

    /// <summary>Depth-first walk over this node and every descendant directory.</summary>
    public IEnumerable<DirectoryNode> DescendantsAndSelf()
    {
        var stack = new Stack<DirectoryNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            var dirs = node.Directories;
            for (int i = dirs.Length - 1; i >= 0; i--)
                stack.Push(dirs[i]);
        }
    }

    public override string ToString() => $"{Name} ({TotalSize:N0} bytes)";
}
