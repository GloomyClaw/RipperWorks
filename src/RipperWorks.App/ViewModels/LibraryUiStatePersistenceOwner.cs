namespace RipperWorks.App.ViewModels;

internal sealed class LibraryUiStatePersistenceOwner
{
    private readonly object _gate = new();
    private readonly Action<Exception> _observeFailure;
    private Task _completion = Task.CompletedTask;
    private bool _accepting = true;

    public LibraryUiStatePersistenceOwner(Action<Exception> observeFailure)
    {
        _observeFailure = observeFailure;
    }

    internal Task Completion
    {
        get
        {
            lock (_gate)
                return _completion;
        }
    }

    internal bool TrySchedule(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            if (!_accepting)
                return false;
            _completion = RunAfterAsync(_completion, operation);
            return true;
        }
    }

    internal Task CompleteAsync()
    {
        lock (_gate)
        {
            _accepting = false;
            return _completion;
        }
    }

    internal void StopAccepting()
    {
        lock (_gate)
            _accepting = false;
    }

    private async Task RunAfterAsync(
        Task previous,
        Func<Task> operation)
    {
        await previous;
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            _observeFailure(exception);
        }
    }
}
