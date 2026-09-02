using System.Runtime.ExceptionServices;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>Bounded exactly-once cleanup routine; coordinator remains lifecycle authority.</summary>
internal static class GameOperationCoordinatorCleanup
{
    public static async Task RunAsync(
        Func<Task> stopAsync,
        SemaphoreSlim journalGate,
        Func<IGameOperationJournal?> getJournal,
        Action clearJournal,
        Func<Task>? cleanupFault,
        Action journalDisposed)
    {
        Exception? primary = null;
        var secondary = new List<Exception>();
        try
        {
            await stopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        var gateHeld = false;
        try
        {
            await journalGate.WaitAsync().ConfigureAwait(false);
            gateHeld = true;
            var journal = getJournal();
            if (journal is IAsyncDisposable asyncJournal)
            {
                await asyncJournal.DisposeAsync().ConfigureAwait(false);
                journalDisposed();
            }
            else if (journal is not null)
            {
                await journal.CloseAsync().ConfigureAwait(false);
                journalDisposed();
            }

            clearJournal();
        }
        catch (Exception exception)
        {
            secondary.Add(exception);
        }
        finally
        {
            if (gateHeld)
                journalGate.Release();
        }

        try
        {
            if (cleanupFault is not null)
                await cleanupFault().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            secondary.Add(exception);
        }

        try
        {
            journalGate.Dispose();
        }
        catch (Exception exception)
        {
            secondary.Add(exception);
        }

        if (primary is not null && secondary.Count > 0)
            throw new AggregateException([primary, .. secondary]);
        if (primary is not null)
            ExceptionDispatchInfo.Capture(primary).Throw();
        if (secondary.Count == 1)
            ExceptionDispatchInfo.Capture(secondary[0]).Throw();
        if (secondary.Count > 1)
            throw new AggregateException(secondary);
    }
}
