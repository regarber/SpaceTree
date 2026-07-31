using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpaceTree.App.Infrastructure;

/// <summary>Minimal INotifyPropertyChanged base. Kept dependency-free on purpose.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected void RaiseAll(params string[] propertyNames)
    {
        var handler = PropertyChanged;
        if (handler is null)
            return;
        foreach (var name in propertyNames)
            handler(this, new PropertyChangedEventArgs(name));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(propertyName);
        return true;
    }
}
