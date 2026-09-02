using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RipperWorks.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RelayCommand(
    Action execute,
    Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void NotifyCanExecuteChanged() =>
        CommandManager.InvalidateRequerySuggested();
}

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    bool allowConcurrentExecution = false) : ICommand
{
    private int _executionCount;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        (allowConcurrentExecution ||
         Volatile.Read(ref _executionCount) == 0) &&
        (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) =>
        await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
            return;
        Interlocked.Increment(ref _executionCount);
        try
        {
            NotifyCanExecuteChanged();
            await execute();
        }
        finally
        {
            Interlocked.Decrement(ref _executionCount);
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ChoiceItem : ObservableObject
{
    private string _display;

    public string Value { get; }
    public string Display
    {
        get => _display;
        set => SetProperty(ref _display, value);
    }

    public ChoiceItem(string value, string display)
    {
        Value = value;
        _display = display;
    }
}
