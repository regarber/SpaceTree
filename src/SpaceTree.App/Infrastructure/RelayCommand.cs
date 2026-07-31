using System.Windows.Input;

namespace SpaceTree.App.Infrastructure;

/// <summary>ICommand over a delegate pair.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute is null || _canExecute(parameter);

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            _execute(parameter);
    }

    /// <summary>Re-queries <see cref="CanExecute"/> for every bound control.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
