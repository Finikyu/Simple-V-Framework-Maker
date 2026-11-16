using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Svfm.App;

/// <summary>Base for anything a screen binds to.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A button's action; <see cref="CanExecute"/> follows <see cref="Raise"/>.</summary>
public sealed class Command(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public Command(Action run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }
}
