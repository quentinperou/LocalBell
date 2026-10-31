using System.Windows.Input;

namespace LocalBell.App.ViewModels;

/// <summary>A command bound in XAML; its parameter (the row of a list) is handed to the action.</summary>
public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute()) { }

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
