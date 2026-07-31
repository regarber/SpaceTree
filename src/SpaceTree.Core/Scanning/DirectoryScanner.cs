using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpaceTree.Core.Model;
using SpaceTree.Core.Native;
using static SpaceTree.Core.Native.NativeMethods;

namespace SpaceTree.Core.Scanning;

/// <summary>
/// Parallel recursive directory scanner.
///
/// Shape of the algorithm:
///   * a LIFO work stack of directories, drained by N dedicated worker threads;
///   * each directory is enumerated in large batches with
///     GetFileInformationByHandleEx(FileFullDirectoryInfo), which returns name,
///     attributes, timestamps, logical size AND allocated size in one syscall
///     per ~600 entries;
///   * when a directory finishes, its own totals are added to itself and every
///     ancestor with interlocked arithmetic.
///
/// The incremental roll-up is what lets the UI show a live, correct-so-far tree
/// instead of a spinner. LIFO ordering keeps the working set small and warm: we
/// finish a branch before starting a sibling.
///
/// Two decisions here came directly from measurement:
///   * Dispatch is lock-free and never blocks. Waking workers through a semaphore
///     cost a kernel transition per directory and made the scanner 2.5x slower
///     than a plain Parallel.ForEach. Workers now spin briefly on an empty stack,
///     which is nearly free because the stack is rarely empty until the very end.
///   * Allocated size comes from the directory enumeration itself. Asking
///     GetCompressedFileSize per compressed file doubled total scan time, and it
///     was less accurate than the AllocationSize the filesystem already reports.
/// </summary>
public sealed class DirectoryScanner
{
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Per-directory read buffer. Big enough to hold ~600 entries per syscall.</summary>
    private const int EnumerationBufferSize = 64 * 1024;

    private readonly ConcurrentStack<WorkItem> _queue = new();
    private readonly List<ScanError> _errors = new();
    private readonly object _errorLock = new();

    private int _outstanding;
    private volatile bool _finished;

    private WorkerState[] _workers = Array.Empty<WorkerState>();
    private volatile string _currentExtendedPath = string.Empty;
    private volatile DirectoryNode? _root;

    private int _clusterSize = VolumeService.FallbackClusterSize;
    private bool _followReparsePoints;
    private bool _retainFiles;
    private int _maxErrors;
    private int _errorCount;

    private readonly record struct WorkItem(DirectoryNode Node, string ExtendedPath);

    private enum EnumerationOutcome
    {
        /// <summary>The directory was read.</summary>
        Completed,
        /// <summary>The directory could not be opened or read.</summary>
        Failed,
        /// <summary>The filesystem does not implement the batched API; retry the compatible path.</summary>
        Unsupported,
    }

    /// <summary>
    /// Per-worker scratch space: tallies plus the enumeration buffer.
    ///
    /// The counters are padded so that sixteen threads bumping them do not fight
    /// over a single cache line, and the buffer is allocated on the pinned object
    /// heap once per worker rather than per directory.
    /// </summary>
    private sealed class WorkerState
    {
#pragma warning disable CS0169, IDE0051 // false-sharing padding, intentionally unread
        private long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6;
#pragma warning restore CS0169, IDE0051
        public long Files;
        public long Directories;
        public long Bytes;
#pragma warning disable CS0169, IDE0051
        private long _pad7, _pad8, _pad9, _pad10, _pad11, _pad12, _pad13;
#pragma warning restore CS0169, IDE0051

        public readonly byte[] Buffer = GC.AllocateArray<byte>(EnumerationBufferSize, pinned: true);
    }

    /// <summary>Running totals for the directory currently being enumerated.</summary>
    private ref struct DirectoryAccumulator
    {
        public List<FileEntry>? Files;
        public List<DirectoryNode>? Directories;
        public long OwnSize;
        public long OwnAllocated;
        public int FileCount;
        public long NewestWrite;
    }

    /// <summary>Raised on the scanning thread at a fixed cadence while a scan runs.</summary>
    public event EventHandler<ScanProgress>? ProgressChanged;

    /// <summary>
    /// The tree being built, available as soon as the scan starts rather than
    /// only when it finishes. Totals under it are rolled up with interlocked
    /// arithmetic, so a reader on another thread always sees a consistent —
    /// if still growing — picture. This is what lets the UI show a live tree.
    /// </summary>
    public DirectoryNode? Root => _root;

    /// <summary>Interval between progress notifications.</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(120);

    public ScanProgress CurrentProgress(TimeSpan elapsed)
    {
        long files = 0, dirs = 0, bytes = 0;
        var workers = _workers;
        for (int i = 0; i < workers.Length; i++)
        {
            files += Volatile.Read(ref workers[i].Files);
            dirs += Volatile.Read(ref workers[i].Directories);
            bytes += Volatile.Read(ref workers[i].Bytes);
        }

        return new ScanProgress(
            files,
            dirs,
            bytes,
            Math.Max(0, Volatile.Read(ref _outstanding)),
            LongPath.ToDisplay(_currentExtendedPath),
            elapsed,
            Volatile.Read(ref _errorCount));
    }

    public Task<ScanResult> ScanAsync(ScanOptions options, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(options, cancellationToken), CancellationToken.None);

    /// <summary>Runs a scan to completion. Blocks the calling thread; use <see cref="ScanAsync"/> from UI code.</summary>
    public ScanResult Scan(ScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        string rootPath = LongPath.NormalizeRoot(options.RootPath);
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("A scan root is required.", nameof(options));

        _followReparsePoints = options.FollowReparsePoints;
        _retainFiles = options.RetainFileEntries;
        _maxErrors = options.MaxRecordedErrors;
        _clusterSize = options.ClusterSizeOverride > 0
            ? options.ClusterSizeOverride
            : VolumeService.GetClusterSize(rootPath);

        var volume = VolumeService.GetVolumeInfo(rootPath);
        var root = new DirectoryNode(rootPath, null) { Depth = 0 };
        SeedRootTimestamp(root, rootPath);
        _root = root;

        int threadCount = Math.Clamp(options.ThreadCount, 1, 64);
        _workers = new WorkerState[threadCount];
        for (int i = 0; i < threadCount; i++)
            _workers[i] = new WorkerState();

        var stopwatch = Stopwatch.StartNew();
        bool cancelled = false;

        Enqueue(new WorkItem(root, LongPath.ToExtended(rootPath)));

        var threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int index = i;
            threads[i] = new Thread(() => WorkerLoop(index, cancellationToken))
            {
                IsBackground = true,
                Name = $"SpaceTree.Scan[{index}]",
            };
            threads[i].Start();
        }

        // Publish progress from the calling thread while the workers run.
        try
        {
            while (!AllThreadsDone(threads))
            {
                Thread.Sleep(ProgressInterval);
                ProgressChanged?.Invoke(this, CurrentProgress(stopwatch.Elapsed));
                if (cancellationToken.IsCancellationRequested)
                    cancelled = true;
            }
        }
        finally
        {
            foreach (var t in threads)
                t.Join();
            stopwatch.Stop();
        }

        cancelled |= cancellationToken.IsCancellationRequested;
        ProgressChanged?.Invoke(this, CurrentProgress(stopwatch.Elapsed));

        return new ScanResult
        {
            Root = root,
            RootPath = rootPath,
            Duration = stopwatch.Elapsed,
            Cancelled = cancelled,
            ClusterSize = _clusterSize,
            Errors = SnapshotErrors(),
            Volume = volume,
        };
    }

    private static bool AllThreadsDone(Thread[] threads)
    {
        foreach (var t in threads)
            if (t.IsAlive)
                return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Enqueue(WorkItem item)
    {
        // Count the item before publishing it, so a worker can never observe an
        // empty stack with a zero outstanding count while work is still coming.
        Interlocked.Increment(ref _outstanding);
        _queue.Push(item);
    }

    private void WorkerLoop(int index, CancellationToken cancellationToken)
    {
        var state = _workers[index];
        var spin = new SpinWait();

        while (!_finished)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (_queue.TryPop(out var item))
            {
                spin.Reset();
                try
                {
                    ProcessDirectory(item, state, cancellationToken);
                }
                catch (Exception ex)
                {
                    // A single unreadable directory must never take down the scan.
                    RecordError(LongPath.ToDisplay(item.ExtendedPath), Marshal.GetLastWin32Error(), ex.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref _outstanding);
                }
                continue;
            }

            // Nothing to take. If nothing is in flight either, the tree is done.
            if (Volatile.Read(ref _outstanding) <= 0)
            {
                _finished = true;
                return;
            }

            // Other workers are still expanding directories; back off politely.
            // SpinWait escalates from busy-spin to Thread.Yield to Sleep(1), so an
            // idle worker at the tail of a scan does not burn a core.
            spin.SpinOnce();
        }
    }

    private void ProcessDirectory(WorkItem item, WorkerState state, CancellationToken cancellationToken)
    {
        var node = item.Node;
        string extendedPath = item.ExtendedPath;

        // Publish the raw extended path; converting it for display costs an
        // allocation, and only the progress reader ever needs it.
        _currentExtendedPath = extendedPath;

        var accumulator = new DirectoryAccumulator { NewestWrite = node.LastWriteFileTime };

        var outcome = EnumerateBatched(node, extendedPath, state, ref accumulator, cancellationToken, out int error);
        if (outcome == EnumerationOutcome.Unsupported)
            outcome = EnumerateCompatible(node, extendedPath, ref accumulator, cancellationToken, out error);

        if (outcome == EnumerationOutcome.Failed)
        {
            node.HasError = true;
            node.ErrorCode = error;
            RecordError(LongPath.ToDisplay(extendedPath), error, DescribeError(error));
            state.Directories++;
            return;
        }

        node.Files = accumulator.Files is null ? Array.Empty<FileEntry>() : accumulator.Files.ToArray();
        node.Directories = accumulator.Directories is null ? Array.Empty<DirectoryNode>() : accumulator.Directories.ToArray();
        node.OwnSize = accumulator.OwnSize;
        node.OwnAllocated = accumulator.OwnAllocated;
        node.OwnFileCount = accumulator.FileCount;

        int childDirCount = node.Directories.Length;

        // Roll this directory's own contribution into itself and every ancestor.
        for (DirectoryNode? n = node; n is not null; n = n.Parent)
        {
            n.AddTotals(accumulator.OwnSize, accumulator.OwnAllocated, accumulator.FileCount, childDirCount);
            n.MergeLastWrite(accumulator.NewestWrite);
        }

        state.Files += accumulator.FileCount;
        state.Bytes += accumulator.OwnSize;
        state.Directories++;
    }

    /// <summary>
    /// Fast path: open the directory once and pull entries in 64 KB batches.
    /// Yields the exact on-disk size for every entry with no extra syscalls.
    /// </summary>
    private unsafe EnumerationOutcome EnumerateBatched(
        DirectoryNode node, string extendedPath, WorkerState state,
        ref DirectoryAccumulator accumulator, CancellationToken cancellationToken, out int error)
    {
        error = 0;

        IntPtr handle = CreateFile(
            extendedPath,
            FILE_LIST_DIRECTORY,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle == InvalidHandle)
        {
            error = Marshal.GetLastWin32Error();
            // Listing through FindFirstFileEx needs the same right, so a denial
            // here is a real denial rather than a reason to try the other API.
            return EnumerationOutcome.Failed;
        }

        try
        {
            fixed (byte* buffer = state.Buffer)
            {
                int infoClass = FileFullDirectoryRestartInfo;
                bool anyBatch = false;

                while (GetFileInformationByHandleEx(handle, infoClass, buffer, EnumerationBufferSize))
                {
                    anyBatch = true;
                    infoClass = FileFullDirectoryInfo; // continue from where we stopped

                    byte* cursor = buffer;
                    while (true)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return EnumerationOutcome.Completed;

                        var info = (FILE_FULL_DIR_INFO*)cursor;
                        int nameChars = (int)(info->FileNameLength / sizeof(char));
                        char* namePtr = (char*)(cursor + FILE_FULL_DIR_INFO.FileNameOffset);

                        if (!IsDotName(namePtr, nameChars))
                        {
                            Accept(
                                node,
                                extendedPath,
                                new string(namePtr, 0, nameChars),
                                info->FileAttributes,
                                info->EndOfFile,
                                info->AllocationSize,
                                info->LastWriteTime,
                                allocationIsExact: true,
                                ref accumulator);
                        }

                        uint next = info->NextEntryOffset;
                        if (next == 0)
                            break;
                        cursor += next;
                    }
                }

                int last = Marshal.GetLastWin32Error();
                if (last == ERROR_NO_MORE_FILES)
                    return EnumerationOutcome.Completed;

                // Some filesystems (older SMB redirectors, a few FUSE-style
                // drivers) do not implement this information class.
                if (!anyBatch && last is ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION)
                    return EnumerationOutcome.Unsupported;

                if (last != 0)
                {
                    error = last;
                    return EnumerationOutcome.Failed;
                }

                return EnumerationOutcome.Completed;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Compatibility path for filesystems without the batched information class.
    /// FindFirstFileEx does not report allocated size, so it is approximated by
    /// rounding the logical size up to the volume's cluster size.
    /// </summary>
    private EnumerationOutcome EnumerateCompatible(
        DirectoryNode node, string extendedPath,
        ref DirectoryAccumulator accumulator, CancellationToken cancellationToken, out int error)
    {
        error = 0;

        IntPtr handle = FindFirstFileEx(
            LongPath.SearchPattern(extendedPath),
            FINDEX_INFO_LEVELS.FindExInfoBasic,
            out WIN32_FIND_DATAW data,
            FINDEX_SEARCH_OPS.FindExSearchNameMatch,
            IntPtr.Zero,
            FIND_FIRST_EX_LARGE_FETCH);

        if (handle == InvalidHandle)
        {
            int code = Marshal.GetLastWin32Error();
            if (code is ERROR_FILE_NOT_FOUND or ERROR_NO_MORE_FILES)
                return EnumerationOutcome.Completed;
            error = code;
            return EnumerationOutcome.Failed;
        }

        try
        {
            do
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (IsDotEntry(ref data))
                    continue;

                Accept(
                    node,
                    extendedPath,
                    GetFileName(ref data),
                    data.dwFileAttributes,
                    data.Size,
                    RoundUpToCluster(data.Size, _clusterSize),
                    data.LastWriteFileTime,
                    allocationIsExact: false,
                    ref accumulator);
            }
            while (FindNextFile(handle, out data));
        }
        finally
        {
            FindClose(handle);
        }

        return EnumerationOutcome.Completed;
    }

    /// <summary>Folds one directory entry into the running totals, queueing subdirectories.</summary>
    private void Accept(
        DirectoryNode node, string extendedPath, string name,
        uint attributes, long size, long allocated, long lastWrite,
        bool allocationIsExact, ref DirectoryAccumulator accumulator)
    {
        if (lastWrite > accumulator.NewestWrite)
            accumulator.NewestWrite = lastWrite;

        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            var child = new DirectoryNode(name, node) { Depth = node.Depth + 1 };
            child.MergeLastWrite(lastWrite);
            (accumulator.Directories ??= new List<DirectoryNode>(8)).Add(child);

            if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 && !_followReparsePoints)
            {
                // Junctions and symlinks point at content we either already counted
                // or that lives on another volume. Show the node, do not descend,
                // do not double-count the bytes.
                child.IsReparsePoint = true;
            }
            else
            {
                // Queue immediately: a child only ever touches its own fields and
                // its ancestors' atomic totals, so it is safe to start before this
                // directory has finished enumerating.
                Enqueue(new WorkItem(child, LongPath.Combine(extendedPath, name)));
            }
            return;
        }

        if (!allocationIsExact)
        {
            // Cloud placeholders (OneDrive "online-only") occupy nothing locally.
            const uint offlineMask = FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS;
            if ((attributes & offlineMask) != 0)
                allocated = 0;
        }

        accumulator.OwnSize += size;
        accumulator.OwnAllocated += allocated;
        accumulator.FileCount++;

        if (_retainFiles)
        {
            (accumulator.Files ??= new List<FileEntry>(16))
                .Add(new FileEntry(name, size, allocated, lastWrite, attributes));
        }
    }

    /// <summary>Rounds a logical size up to a whole number of clusters.</summary>
    public static long RoundUpToCluster(long size, int clusterSize)
    {
        if (size <= 0)
            return 0;
        if (clusterSize <= 1)
            return size;

        long clusters = (size + clusterSize - 1) / clusterSize;
        return clusters * clusterSize;
    }

    private static unsafe bool IsDotName(char* name, int length) =>
        (length == 1 && name[0] == '.') ||
        (length == 2 && name[0] == '.' && name[1] == '.');

    private static void SeedRootTimestamp(DirectoryNode root, string rootPath)
    {
        if (GetFileAttributesEx(LongPath.ToExtended(rootPath), 0, out var info))
        {
            root.MergeLastWrite(info.LastWriteFileTime);
            root.IsReparsePoint = (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        }
    }

    private void RecordError(string path, int code, string message)
    {
        if (Interlocked.Increment(ref _errorCount) > _maxErrors)
            return;

        lock (_errorLock)
        {
            _errors.Add(new ScanError(path, code, message));
        }
    }

    private IReadOnlyList<ScanError> SnapshotErrors()
    {
        lock (_errorLock)
        {
            return _errors.ToArray();
        }
    }

    private static string DescribeError(int code) => code switch
    {
        ERROR_ACCESS_DENIED => "Access denied.",
        ERROR_PATH_NOT_FOUND => "Path not found.",
        ERROR_FILE_NOT_FOUND => "File not found.",
        _ => new System.ComponentModel.Win32Exception(code).Message,
    };
}
