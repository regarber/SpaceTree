using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using SpaceTree.Core.Native;

namespace SpaceTree.App.Services;

public readonly record struct ShellResult(bool Success, bool Aborted, string? Error)
{
    public static ShellResult Ok => new(true, false, null);
    public static ShellResult Cancelled => new(false, true, null);
    public static ShellResult Fail(string message) => new(false, false, message);
}

/// <summary>Explorer, clipboard and recycle-bin operations for the context menu.</summary>
public static class ShellService
{
    /// <summary>Opens a folder in Explorer, or launches a file with its default handler.</summary>
    public static ShellResult Open(string path)
    {
        try
        {
            string display = LongPath.ToDisplay(path);
            var info = new ProcessStartInfo(display) { UseShellExecute = true };
            Process.Start(info);
            return ShellResult.Ok;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return ShellResult.Fail(e.Message);
        }
    }

    /// <summary>Opens the containing folder with the item pre-selected.</summary>
    public static ShellResult ShowInExplorer(string path)
    {
        try
        {
            string display = LongPath.ToDisplay(path);

            // /select needs the path quoted and the comma unspaced; anything else
            // makes Explorer open "Documents" instead.
            var info = new ProcessStartInfo("explorer.exe", $"/select,\"{display}\"")
            {
                UseShellExecute = true,
            };
            Process.Start(info);
            return ShellResult.Ok;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ShellResult.Fail(e.Message);
        }
    }

    public static ShellResult CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return ShellResult.Ok;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            // Another process can hold the clipboard open; one retry is usually enough.
            try
            {
                System.Threading.Thread.Sleep(60);
                Clipboard.SetText(text);
                return ShellResult.Ok;
            }
            catch (Exception inner) when (inner is COMException or InvalidOperationException)
            {
                return ShellResult.Fail(e.Message);
            }
        }
    }

    /// <summary>Shows the shell property sheet for a file or folder.</summary>
    public static ShellResult ShowProperties(string path, IntPtr owner)
    {
        string display = LongPath.ToDisplay(path);

        var info = new NativeShell.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<NativeShell.SHELLEXECUTEINFO>(),
            fMask = NativeShell.SEE_MASK_INVOKEIDLIST | NativeShell.SEE_MASK_NOASYNC,
            hwnd = owner,
            lpVerb = "properties",
            lpFile = display,
            nShow = NativeShell.SW_SHOW,
        };

        if (NativeShell.ShellExecuteEx(ref info))
            return ShellResult.Ok;

        int error = Marshal.GetLastWin32Error();
        return error == 0
            ? ShellResult.Ok
            : ShellResult.Fail(new System.ComponentModel.Win32Exception(error).Message);
    }

    /// <summary>
    /// Deletes files and folders through the shell, sending them to the recycle
    /// bin unless <paramref name="permanent"/> is set. The caller is expected to
    /// have confirmed already, so the shell's own "are you sure" is suppressed —
    /// but the warning shown when an item is too large to be recycled is kept,
    /// because that one changes what actually happens to the data.
    /// </summary>
    public static ShellResult Delete(IReadOnlyList<string> paths, IntPtr owner, bool permanent)
    {
        if (paths.Count == 0)
            return ShellResult.Ok;

        NativeShell.IFileOperation? operation = null;
        try
        {
            operation = (NativeShell.IFileOperation)new NativeShell.FileOperationClass();

            uint flags = NativeShell.FOF_NOCONFIRMATION | NativeShell.FOF_WANTNUKEWARNING;
            if (!permanent)
                flags |= NativeShell.FOF_ALLOWUNDO | NativeShell.FOFX_RECYCLEONDELETE | NativeShell.FOFX_ADDUNDORECORD;

            Check(operation.SetOperationFlags(flags));
            Check(operation.SetOwnerWindow(owner));

            foreach (string path in paths)
            {
                var item = CreateShellItem(path);
                if (item is null)
                    return ShellResult.Fail($"Could not resolve '{LongPath.ToDisplay(path)}'.");

                Check(operation.DeleteItem(item, IntPtr.Zero));
                Marshal.ReleaseComObject(item);
            }

            int hr = operation.PerformOperations();

            operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted)
                return ShellResult.Cancelled;

            // COPYENGINE_E_USER_CANCELLED, returned when the user dismisses the
            // shell's own progress or elevation dialog.
            if (hr == unchecked((int)0x80270000) || hr == unchecked((int)0x800704C7))
                return ShellResult.Cancelled;

            Check(hr);
            return ShellResult.Ok;
        }
        catch (COMException e)
        {
            return ShellResult.Fail(e.Message);
        }
        catch (InvalidCastException e)
        {
            return ShellResult.Fail("The shell file operation service is unavailable. " + e.Message);
        }
        finally
        {
            if (operation is not null)
                Marshal.ReleaseComObject(operation);
        }
    }

    private static NativeShell.IShellItem? CreateShellItem(string path)
    {
        var iid = NativeShell.IID_IShellItem;

        // Try the plain path first: the shell resolves it against the namespace
        // and gives the nicest display names in its progress UI.
        try
        {
            NativeShell.SHCreateItemFromParsingName(LongPath.ToDisplay(path), IntPtr.Zero, ref iid, out var item);
            return item;
        }
        catch (COMException)
        {
            // Past MAX_PATH the parser needs the extended form.
        }

        try
        {
            NativeShell.SHCreateItemFromParsingName(LongPath.ToExtended(path), IntPtr.Zero, ref iid, out var item);
            return item;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }
}
