namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Resolves an already-durable idempotency key without consulting live
/// executor, content-store, managed-path, or destination state.
/// </summary>
internal static class GameOperationIdempotencyResolver
{
    public static async Task<GameOperationResult> ResolveConstraintWinnerAsync(
        IGameOperationJournal journal,
        string? journalPath,
        Func<CancellationToken, Task<IGameOperationJournal>> ensureJournal,
        GameOperationPlan callerPlan,
        IdempotencyKey idempotencyKey) =>
        await TryResolveAsync(
                journal,
                journalPath,
                ensureJournal,
                callerPlan,
                idempotencyKey,
                (_, _, _, _) => null,
                null,
                null,
                CancellationToken.None)
            .ConfigureAwait(false) ??
        throw new GameOperationJournalConsistencyException(
            "IdempotencyConstraintWinnerMissing");

    public static async Task<GameOperationResult?> TryResolveAsync(
        IGameOperationJournal? openJournal,
        string? journalPath,
        Func<CancellationToken, Task<IGameOperationJournal>> ensureJournal,
        GameOperationPlan callerPlan,
        IdempotencyKey idempotencyKey,
        Func<CanonicalProfileKey, IdempotencyKey, OperationId, string,
            Task<GameOperationResult>?> tryGetDurableOwnedTask,
        Func<CancellationToken, Task>? afterFirstNonterminalRead,
        Func<CancellationToken, Task>? afterSecondNonterminalRead,
        CancellationToken cancellationToken)
    {
        // Preserve the existing no-journal-on-rejected-new-operation contract.
        // A durable key cannot exist when neither a live journal nor its file
        // exists, so new-operation validation may proceed without opening one.
        if (openJournal is null &&
            (string.IsNullOrWhiteSpace(journalPath) ||
             FileSystemEntryInspector.Shared.Inspect(journalPath) !=
                FileSystemEntryKind.Ordinary))
        {
            return null;
        }

        var journal = openJournal ?? await ensureJournal(cancellationToken)
            .ConfigureAwait(false);
        var existing = await journal.FindByIdempotencyAsync(
                callerPlan.ProfileKey,
                idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
            return null;

        EnsureExpectedIdentity(existing, callerPlan.ProfileKey, idempotencyKey);

        if (!string.Equals(
                existing.PlanHash,
                callerPlan.PlanHash,
                StringComparison.Ordinal))
        {
            return GameOperationTerminalHelpers.TerminalPublic(
                existing.OperationId,
                GameOperationState.BlockedBeforeCommit,
                callerPlan.PlanHash,
                idempotencyKey.Value,
                "IdempotencyConflict");
        }

        if (GameOperationTerminalHelpers.IsTerminalState(existing.State))
            return PersistedResult(existing);

        if (afterFirstNonterminalRead is not null)
        {
            await afterFirstNonterminalRead(cancellationToken)
                .ConfigureAwait(false);
        }

        var ownedTask = tryGetDurableOwnedTask(
            callerPlan.ProfileKey,
            idempotencyKey,
            existing.OperationId,
            existing.PlanHash);
        if (ownedTask is not null)
            return await ownedTask.ConfigureAwait(false);

        var second = await journal.FindByIdempotencyAsync(
                callerPlan.ProfileKey,
                idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSameDurableRow(
            existing,
            second,
            callerPlan.ProfileKey,
            idempotencyKey);
        if (GameOperationTerminalHelpers.IsTerminalState(second!.State))
            return PersistedResult(second);

        if (afterSecondNonterminalRead is not null)
        {
            await afterSecondNonterminalRead(cancellationToken)
                .ConfigureAwait(false);
        }

        ownedTask = tryGetDurableOwnedTask(
            callerPlan.ProfileKey,
            idempotencyKey,
            second!.OperationId,
            second.PlanHash);
        if (ownedTask is not null)
            return await ownedTask.ConfigureAwait(false);

        // A terminal commit can race the final owner removal. Classify an
        // orphan only after one last identity-checked durable observation.
        var final = await journal.FindByIdempotencyAsync(
                callerPlan.ProfileKey,
                idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSameDurableRow(
            second,
            final,
            callerPlan.ProfileKey,
            idempotencyKey);
        if (GameOperationTerminalHelpers.IsTerminalState(final!.State))
            return PersistedResult(final);

        var orphanCode = final.State is GameOperationState.MutationStarted
            or GameOperationState.Executing
            or GameOperationState.Finalizing
                ? "IncompletePostCommit"
                : "IncompleteOrphaned";
        return new GameOperationResult
        {
            OperationId = final.OperationId,
            State = GameOperationState.RecoveryRequired,
            PlanHash = final.PlanHash,
            IdempotencyKey = final.IdempotencyKey,
            ErrorCode = orphanCode,
            CancellationRequested = final.CancellationRequested,
            JoinedExisting = true,
            ExecutorInvocationCount = 0
        };
    }

    private static void EnsureExpectedIdentity(
        GameOperationRecord record,
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey)
    {
        if (!string.Equals(
                record.CanonicalProfileKey,
                profileKey.Value,
                StringComparison.Ordinal) ||
            !string.Equals(
                record.IdempotencyKey,
                idempotencyKey.Value,
                StringComparison.Ordinal))
        {
            throw new GameOperationJournalConsistencyException(
                "IdempotencyRowIdentityMismatch");
        }
    }

    private static void EnsureSameDurableRow(
        GameOperationRecord first,
        GameOperationRecord? second,
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey)
    {
        if (second is null ||
            second.OperationId != first.OperationId ||
            !string.Equals(
                second.PlanHash,
                first.PlanHash,
                StringComparison.Ordinal))
        {
            throw new GameOperationJournalConsistencyException(
                "IdempotencyRowChangedDuringResolution");
        }

        EnsureExpectedIdentity(second, profileKey, idempotencyKey);
    }

    private static GameOperationResult PersistedResult(
        GameOperationRecord existing) =>
        new()
        {
            OperationId = existing.OperationId,
            State = existing.State,
            PlanHash = existing.PlanHash,
            IdempotencyKey = existing.IdempotencyKey,
            ErrorCode = existing.ErrorCode,
            RedactedErrorDetail = existing.RedactedErrorDetail,
            CancellationRequested = existing.CancellationRequested,
            JoinedExisting = true,
            ExecutorInvocationCount = 0
        };
}

internal sealed class GameOperationJournalConsistencyException :
    InvalidOperationException
{
    public GameOperationJournalConsistencyException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
