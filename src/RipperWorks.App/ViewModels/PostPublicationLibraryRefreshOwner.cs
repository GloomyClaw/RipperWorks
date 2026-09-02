using RipperWorks.Core;

namespace RipperWorks.App.ViewModels;

internal sealed class PostPublicationLibraryRefreshOwner : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<DownloaderEntry, Task> _refresh;
    private readonly Action<Exception>? _faultObserver;
    private DownloaderEntry? _latestEntry;
    private Task _worker = Task.CompletedTask;
    private TaskCompletionSource<bool> _idle = CompletedIdle();
    private Task? _disposeTask;
    private int _faultObserverFailureCount;
    private bool _pending;
    private bool _running;
    private bool _accepting = true;

    public PostPublicationLibraryRefreshOwner(
        Func<DownloaderEntry, Task> refresh,
        Action<Exception>? faultObserver)
    {
        _refresh = refresh;
        _faultObserver = faultObserver;
    }

    internal bool IsAccepting
    {
        get
        {
            lock (_gate)
                return _accepting;
        }
    }

    internal int FaultObserverFailureCount =>
        Volatile.Read(ref _faultObserverFailureCount);

    internal bool TryRequest(DownloaderEntry entry)
    {
        lock (_gate)
        {
            if (!_accepting)
                return false;

            _latestEntry = entry;
            _pending = true;
            if (_idle.Task.IsCompleted)
            {
                _idle = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (!_running)
            {
                _running = true;
                _worker = RunAsync();
            }
            return true;
        }
    }

    internal Task RequestAndWaitAsync(DownloaderEntry entry)
    {
        lock (_gate)
        {
            if (!TryRequest(entry))
                return Task.CompletedTask;
            return _idle.Task;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _accepting = false;
            _disposeTask ??= AwaitWorkerAsync(_worker);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        while (true)
        {
            DownloaderEntry entry;
            lock (_gate)
            {
                if (!_pending)
                {
                    _running = false;
                    _idle.TrySetResult(true);
                    return;
                }
                entry = _latestEntry!;
                _pending = false;
            }

            try
            {
                await _refresh(entry);
            }
            catch (Exception exception)
            {
                ObserveFault(exception);
            }
        }
    }

    private void ObserveFault(Exception exception)
    {
        if (_faultObserver is null)
            return;
        try
        {
            _faultObserver(exception);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _faultObserverFailureCount);
        }
    }

    private static async Task AwaitWorkerAsync(Task worker) =>
        await worker;

    private static TaskCompletionSource<bool> CompletedIdle()
    {
        var value = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        value.SetResult(true);
        return value;
    }
}
