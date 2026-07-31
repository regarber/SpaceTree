using System.Runtime.InteropServices;

namespace SpaceTree.Core.Native;

/// <summary>
/// Win32 interop used by the scanning engine. We deliberately bypass
/// <see cref="System.IO.Directory"/> enumeration: FindFirstFileEx with
/// FindExInfoBasic + LARGE_FETCH is measurably faster and hands us size,
/// attributes and timestamps without a second stat call per entry.
///
/// The find structure is fully blittable (every field is a DWORD, timestamps are
/// split into low/high halves to preserve the native 4-byte layout, and the name
/// buffers are fixed arrays). That means no per-entry marshalling at all, which
/// matters when a system drive produces a million of them.
/// </summary>
internal static unsafe partial class NativeMethods
{
    internal const int MAX_PATH = 260;
    internal const int MAX_ALTERNATE = 14;

    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    internal const uint FILE_ATTRIBUTE_COMPRESSED = 0x00000800;
    internal const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;
    internal const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
    internal const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
    internal const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;

    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_NO_MORE_FILES = 18;
    internal const uint INVALID_FILE_SIZE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WIN32_FIND_DATAW
    {
        internal uint dwFileAttributes;
        internal uint ftCreationTimeLow;
        internal uint ftCreationTimeHigh;
        internal uint ftLastAccessTimeLow;
        internal uint ftLastAccessTimeHigh;
        internal uint ftLastWriteTimeLow;
        internal uint ftLastWriteTimeHigh;
        internal uint nFileSizeHigh;
        internal uint nFileSizeLow;
        internal uint dwReserved0;
        internal uint dwReserved1;
        internal fixed char cFileName[MAX_PATH];
        internal fixed char cAlternateFileName[MAX_ALTERNATE];

        internal readonly long Size => ((long)nFileSizeHigh << 32) | nFileSizeLow;
        internal readonly long LastWriteFileTime => ((long)ftLastWriteTimeHigh << 32) | ftLastWriteTimeLow;
        internal readonly bool IsDirectory => (dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        internal readonly bool IsReparsePoint => (dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    /// <summary>True for the "." and ".." pseudo-entries, without allocating a string.</summary>
    internal static bool IsDotEntry(ref WIN32_FIND_DATAW data)
    {
        fixed (char* p = data.cFileName)
        {
            if (p[0] != '.')
                return false;
            if (p[1] == '\0')
                return true;
            return p[1] == '.' && p[2] == '\0';
        }
    }

    /// <summary>Materialises the entry name from the fixed buffer.</summary>
    internal static string GetFileName(ref WIN32_FIND_DATAW data)
    {
        fixed (char* p = data.cFileName)
        {
            int length = 0;
            while (length < MAX_PATH && p[length] != '\0')
                length++;
            return new string(p, 0, length);
        }
    }

    internal enum FINDEX_INFO_LEVELS
    {
        FindExInfoStandard = 0,
        FindExInfoBasic = 1,
    }

    internal enum FINDEX_SEARCH_OPS
    {
        FindExSearchNameMatch = 0,
    }

    internal const int FIND_FIRST_EX_LARGE_FETCH = 2;

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr FindFirstFileEx(
        string lpFileName,
        FINDEX_INFO_LEVELS fInfoLevelId,
        out WIN32_FIND_DATAW lpFindFileData,
        FINDEX_SEARCH_OPS fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindClose(IntPtr hFindFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetCompressedFileSize(string lpFileName, out uint lpFileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WIN32_FILE_ATTRIBUTE_DATA
    {
        internal uint dwFileAttributes;
        internal uint ftCreationTimeLow;
        internal uint ftCreationTimeHigh;
        internal uint ftLastAccessTimeLow;
        internal uint ftLastAccessTimeHigh;
        internal uint ftLastWriteTimeLow;
        internal uint ftLastWriteTimeHigh;
        internal uint nFileSizeHigh;
        internal uint nFileSizeLow;

        internal readonly long LastWriteFileTime => ((long)ftLastWriteTimeHigh << 32) | ftLastWriteTimeLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileAttributesEx(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

    // ---------------------------------------------------------------------
    // Batched directory enumeration.
    //
    // GetFileInformationByHandleEx(FileFullDirectoryInfo) fills a large buffer
    // with many entries per call and, crucially, reports AllocationSize — the
    // true number of bytes the file occupies. FindFirstFileEx does not, which
    // forces a second syscall per compressed or sparse file. Using this instead
    // makes the scan both faster and more accurate.
    // ---------------------------------------------------------------------

    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint FILE_SHARE_DELETE = 0x00000004;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    internal const int FileFullDirectoryInfo = 14;
    internal const int FileFullDirectoryRestartInfo = 15;

    internal const int ERROR_INVALID_PARAMETER = 87;
    internal const int ERROR_NOT_SUPPORTED = 50;
    internal const int ERROR_INVALID_FUNCTION = 1;

    /// <summary>
    /// Header of FILE_FULL_DIR_INFO. The variable-length file name follows the
    /// header at <see cref="FileNameOffset"/>; that offset is fixed by the native
    /// layout and is NOT sizeof(this), which the compiler pads to 72.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_FULL_DIR_INFO
    {
        internal uint NextEntryOffset;
        internal uint FileIndex;
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal long EndOfFile;        // logical size
        internal long AllocationSize;   // bytes actually occupied on disk
        internal uint FileAttributes;
        internal uint FileNameLength;   // in bytes, not characters
        internal uint EaSize;

        internal const int FileNameOffset = 68;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        IntPtr hFile,
        int fileInformationClass,
        void* lpFileInformation,
        uint dwBufferSize);
}
