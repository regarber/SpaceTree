using System.Windows;
using SpaceTree.App.Services;
using SpaceTree.Core.Util;

namespace SpaceTree.App.Views;

/// <summary>
/// Delete confirmation.
///
/// Deliberately spells out how much data is at stake and how many files it
/// covers before offering the button — a folder row can hide a hundred thousand
/// files behind one innocuous name, and that is exactly the case where an
/// unconsidered click hurts most. The recycle bin is preselected; permanent
/// deletion has to be chosen on purpose.
/// </summary>
public partial class DeleteDialog : Window
{
    private DeleteDialog(DeleteRequest request, SizeUnitSystem units)
    {
        InitializeComponent();

        bool single = request.Paths.Count == 1;
        string kind = request.AnyFolders ? "folder" : "file";

        Headline.Text = single
            ? $"Delete this {kind}?"
            : $"Delete {request.Paths.Count} items?";

        PathText.Text = single
            ? request.Paths[0]
            : string.Join(Environment.NewLine, request.Paths);

        string size = SizeFormatter.Format(request.TotalSize, units);
        DetailText.Text = request.AnyFolders
            ? $"{size} · {SizeFormatter.FormatCount(request.FileCount)} files"
            : size;
    }

    public DeleteChoice Choice { get; private set; } = new(false, false);

    public static DeleteChoice Show(Window owner, DeleteRequest request, SizeUnitSystem units)
    {
        var dialog = new DeleteDialog(request, units) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Choice = new DeleteChoice(true, PermanentOption.IsChecked == true);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = new DeleteChoice(false, false);
        DialogResult = false;
    }
}
