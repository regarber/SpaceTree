using System.Collections.Generic;
using SpaceTree.Core.Export;
using SpaceTree.Core.Model;
using SpaceTree.Core.Util;

namespace SpaceTree.App.Services;

/// <summary>What the user is about to delete, so the confirmation can be specific.</summary>
public sealed record DeleteRequest(
    IReadOnlyList<string> Paths,
    long TotalSize,
    long FileCount,
    bool AnyFolders);

public sealed record DeleteChoice(bool Confirmed, bool Permanent);

/// <summary>
/// Everything the view model needs from the window layer. Keeping it behind an
/// interface is what stops file pickers and message boxes from leaking into the
/// view model, and it means the scanning and view logic stay testable headless.
/// </summary>
public interface IDialogService
{
    /// <summary>Window handle used to parent shell dialogs.</summary>
    IntPtr OwnerHandle { get; }

    string? BrowseForFolder(string? initialPath);

    string? SaveFile(string title, string filter, string defaultFileName, string defaultExtension);

    /// <summary>Delete confirmation. Returns whether to proceed, and whether to bypass the recycle bin.</summary>
    DeleteChoice ConfirmDelete(DeleteRequest request);

    void ShowMessage(string title, string message, bool isError = false);

    bool Confirm(string title, string message);

    void ShowErrors(IReadOnlyList<ScanError> errors);

    /// <summary>Opens the system print dialog for a formatted report of the given rows.</summary>
    void PrintReport(IReadOnlyList<ExportRow> rows, ReportMetadata metadata, SizeUnitSystem units);
}
