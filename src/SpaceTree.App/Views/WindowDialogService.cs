using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SpaceTree.App.Services;
using SpaceTree.Core.Export;
using SpaceTree.Core.Model;
using SpaceTree.Core.Util;

namespace SpaceTree.App.Views;

/// <summary>Window-backed implementation of the dialogs the view model asks for.</summary>
public sealed class WindowDialogService : IDialogService
{
    private readonly Window _owner;

    public WindowDialogService(Window owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public IntPtr OwnerHandle
    {
        get
        {
            var helper = new WindowInteropHelper(_owner);
            return helper.Handle;
        }
    }

    public string? BrowseForFolder(string? initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to scan",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            try
            {
                if (Directory.Exists(initialPath))
                    dialog.InitialDirectory = initialPath;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // A stale saved path is no reason not to show the picker.
            }
        }

        return dialog.ShowDialog(_owner) == true ? dialog.FolderName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName, string defaultExtension)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = Sanitize(defaultFileName),
            DefaultExt = defaultExtension,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public DeleteChoice ConfirmDelete(DeleteRequest request)
    {
        var units = App.Settings.Current.Units;
        return DeleteDialog.Show(_owner, request, units);
    }

    public void ShowMessage(string title, string message, bool isError = false)
    {
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK,
            isError ? MessageBoxImage.Error : MessageBoxImage.Information);
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes;

    public void ShowErrors(IReadOnlyList<ScanError> errors)
    {
        if (errors.Count == 0)
            return;

        new ErrorsWindow(errors) { Owner = _owner }.ShowDialog();
    }

    public void PrintReport(IReadOnlyList<ExportRow> rows, ReportMetadata metadata, SizeUnitSystem units) =>
        ReportPrinter.Print(_owner, rows, metadata, units);

    /// <summary>Strips the characters Windows will not accept in a file name.</summary>
    private static string Sanitize(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[fileName.Length];

        int length = 0;
        foreach (char c in fileName)
            buffer[length++] = Array.IndexOf(invalid, c) >= 0 ? '-' : c;

        string cleaned = new string(buffer[..length]).Trim();
        return cleaned.Length == 0 ? "SpaceTree export" : cleaned;
    }
}
