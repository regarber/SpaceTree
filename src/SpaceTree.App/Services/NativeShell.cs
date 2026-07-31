using System.Runtime.InteropServices;

namespace SpaceTree.App.Services;

/// <summary>
/// Shell COM declarations used for deleting through the recycle bin.
///
/// The old SHFileOperation API is simpler but silently truncates at MAX_PATH,
/// which would be a data-loss hazard in a tool whose whole point is finding the
/// deeply-nested folders that got out of hand. IFileOperation handles extended
/// paths, shows the standard confirmation and progress UI, and raises the UAC
/// prompt itself when a target needs elevation.
/// </summary>
internal static class NativeShell
{
    // IFileOperation operation flags.
    internal const uint FOF_SILENT = 0x0004;
    internal const uint FOF_NOCONFIRMATION = 0x0010;
    internal const uint FOF_ALLOWUNDO = 0x0040;
    internal const uint FOF_NOERRORUI = 0x0400;
    internal const uint FOFX_ADDUNDORECORD = 0x20000000;
    internal const uint FOFX_RECYCLEONDELETE = 0x00080000;
    internal const uint FOF_WANTNUKEWARNING = 0x4000;

    [ComImport]
    [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    [ClassInterface(ClassInterfaceType.None)]
    internal class FileOperationClass
    {
    }

    /// <summary>
    /// Every method must be declared, in vtable order, even the ones we never
    /// call — C# has no way to leave gaps in an interop vtable. Unused interface
    /// parameters are typed as IntPtr so their own interfaces can stay undeclared.
    /// </summary>
    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr pfops, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        [PreserveSig] int SetProgressDialog(IntPtr popd);
        [PreserveSig] int SetProperties(IntPtr pproparray);
        [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem psiItem);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        [PreserveSig] int RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        [PreserveSig] int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        [PreserveSig] int DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        [PreserveSig] int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    internal static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    // ── Properties dialog ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    internal const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    internal const uint SEE_MASK_NOASYNC = 0x00000100;
    internal const int SW_SHOW = 5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
}
