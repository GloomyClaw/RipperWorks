namespace RipperWorks.App.Services;

internal sealed class FomodPreparationBackgroundExecutor(
    Action<int>? executionStarted = null)
{
    public async Task<T> RunAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return await Task.Run(() =>
            {
                executionStarted?.Invoke(Environment.CurrentManagedThreadId);
                return work();
            })
            .ConfigureAwait(false);
    }

    public async Task RunAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await Task.Run(() =>
            {
                executionStarted?.Invoke(Environment.CurrentManagedThreadId);
                work();
            })
            .ConfigureAwait(false);
    }

    public async Task<T> RunTaskAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return await Task.Run(async () =>
            {
                executionStarted?.Invoke(Environment.CurrentManagedThreadId);
                return await work().ConfigureAwait(false);
            })
            .ConfigureAwait(false);
    }

    public async Task RunTaskAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await Task.Run(async () =>
            {
                executionStarted?.Invoke(Environment.CurrentManagedThreadId);
                await work().ConfigureAwait(false);
            })
            .ConfigureAwait(false);
    }
}
