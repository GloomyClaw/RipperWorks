namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Journal open/reopen helpers for the coordinator (keeps coordinator under size cap).
/// </summary>
internal static class GameOperationJournalAccess
{
    public static async Task ForceReopenAsync(
        SemaphoreSlim gate,
        string journalPath,
        Func<IGameOperationJournal?> getJournal,
        Action<IGameOperationJournal?> setJournal,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = getJournal();
            if (existing is not null)
            {
                if (existing is IAsyncDisposable asyncJournal)
                    await asyncJournal.DisposeAsync().ConfigureAwait(false);
                else
                    await existing.CloseAsync().ConfigureAwait(false);

                setJournal(null);
            }

            var journal = new SqliteGameOperationJournal(journalPath);
            await journal.OpenAsync(cancellationToken).ConfigureAwait(false);
            setJournal(journal);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<IReadOnlyList<GameOperationRecord>> ListIndependentAsync(
        string journalPath,
        CanonicalProfileKey profileKey,
        CancellationToken cancellationToken)
    {
        await using var reader = new SqliteGameOperationJournal(journalPath);
        await reader.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await reader.LoadByProfileAsync(profileKey, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<IGameOperationJournal> EnsureOpenAsync(
        SemaphoreSlim gate,
        string? journalPath,
        Func<IGameOperationJournal?> getJournal,
        Action<IGameOperationJournal> setJournal,
        Func<bool> isStopped,
        CancellationToken cancellationToken)
    {
        var existing = getJournal();
        if (existing is not null)
            return existing;
        if (journalPath is null)
            throw new InvalidOperationException("JournalPathNotConfigured");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = getJournal();
            if (existing is not null)
                return existing;
            if (isStopped())
                throw new InvalidOperationException("CoordinatorStopped");

            var journal = new SqliteGameOperationJournal(journalPath);
            await journal.OpenAsync(cancellationToken).ConfigureAwait(false);
            setJournal(journal);
            return journal;
        }
        finally
        {
            gate.Release();
        }
    }
}
