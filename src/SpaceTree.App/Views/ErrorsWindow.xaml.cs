using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using SpaceTree.App.Services;
using SpaceTree.Core.Model;

namespace SpaceTree.App.Views;

/// <summary>
/// Lists the folders a scan could not read.
///
/// These matter more than they look: every denied folder is space the totals do
/// not account for, so an admin needs to see which ones they were before
/// trusting the numbers or deciding to rerun elevated.
/// </summary>
public partial class ErrorsWindow : Window
{
    private readonly IReadOnlyList<ScanError> _errors;

    public ErrorsWindow(IReadOnlyList<ScanError> errors)
    {
        _errors = errors ?? Array.Empty<ScanError>();

        InitializeComponent();

        ErrorList.ItemsSource = _errors;

        int denied = _errors.Count(e => e.ErrorCode == 5);
        Summary.Text = denied > 0
            ? $"{_errors.Count} folders could not be read; {denied} of them because access was denied. " +
              "Restarting SpaceTree as administrator usually resolves those."
            : $"{_errors.Count} folders could not be read. Their contents are not included in the totals.";
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder(_errors.Count * 80);
        foreach (var error in _errors)
            sb.Append(error.Path).Append('\t').AppendLine(error.Message);

        var result = ShellService.CopyText(sb.ToString());
        if (!result.Success)
            MessageBox.Show(this, result.Error ?? "The clipboard is unavailable.", "Copy failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
