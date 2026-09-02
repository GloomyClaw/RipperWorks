namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Shared terminal/result helpers for coordinator + execution session.
/// Keeps session/coordinator under size governance.
/// </summary>
internal static class GameOperationTerminalHelpers
{
    public static bool IsTerminalState(GameOperationState state) =>
        state is GameOperationState.Completed
            or GameOperationState.CancelledBeforeCommit
            or GameOperationState.BlockedBeforeCommit
            or GameOperationState.FailedBeforeCommit
            or GameOperationState.RecoveryRequired;

    public static GameOperationResult TerminalPublic(
        OperationId id,
        GameOperationState state,
        string planHash,
        string key,
        string? code) =>
        new()
        {
            OperationId = id,
            State = state,
            PlanHash = planHash,
            IdempotencyKey = key,
            ErrorCode = code,
            ExecutorInvocationCount = 0
        };

    public static async Task<GameOperationResult> LoadResultAsync(
        IGameOperationJournal journal,
        OperationId operationId,
        int executorCalls,
        int writes)
    {
        var row = await journal.LoadAsync(operationId, CancellationToken.None)
            .ConfigureAwait(false);
        if (row is null)
        {
            return TerminalPublic(
                operationId, GameOperationState.FailedBeforeCommit, "", "",
                "MissingOperation");
        }

        return new GameOperationResult
        {
            OperationId = row.OperationId,
            State = row.State,
            PlanHash = row.PlanHash,
            IdempotencyKey = row.IdempotencyKey,
            ErrorCode = row.ErrorCode,
            RedactedErrorDetail = row.RedactedErrorDetail,
            CancellationRequested = row.CancellationRequested,
            ExecutorInvocationCount = executorCalls,
            SyntheticWriteCount = writes
        };
    }

    public static async Task<GameOperationResult> TerminalFromAsync(
        IGameOperationJournal journal,
        OperationId operationId,
        GameOperationState from,
        GameOperationState to,
        string errorCode,
        int executorCalls)
    {
        await journal.TransitionAsync(
                operationId, from, to, GameOperationPhase.Terminal, errorCode,
                cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
        return await LoadResultAsync(journal, operationId, executorCalls, 0)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// After durable Accepted, if lane ownership is impossible (Stop race),
    /// convert to honest pre-commit terminal — never leave Accepted unowned.
    /// </summary>
    public static async Task<GameOperationResult> TerminalizeUnownedAcceptedAsync(
        IGameOperationJournal journal,
        OperationId operationId,
        string planHash,
        string idempotencyKey,
        string errorCode)
    {
        try
        {
            await journal.TransitionAsync(
                    operationId,
                    GameOperationState.Accepted,
                    GameOperationState.CancelledBeforeCommit,
                    GameOperationPhase.Terminal,
                    errorCode,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Already moved by another path; load actual state.
        }

        var row = await journal.LoadAsync(operationId, CancellationToken.None)
            .ConfigureAwait(false);
        if (row is null)
        {
            return TerminalPublic(
                operationId,
                GameOperationState.CancelledBeforeCommit,
                planHash,
                idempotencyKey,
                errorCode);
        }

        if (row.State == GameOperationState.Accepted)
        {
            try
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.Accepted,
                        GameOperationState.FailedBeforeCommit,
                        GameOperationPhase.Terminal,
                        "FailedBeforeCommit",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                row = await journal.LoadAsync(operationId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                row = await journal.LoadAsync(operationId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return new GameOperationResult
        {
            OperationId = operationId,
            State = row?.State ?? GameOperationState.CancelledBeforeCommit,
            PlanHash = row?.PlanHash ?? planHash,
            IdempotencyKey = row?.IdempotencyKey ?? idempotencyKey,
            ErrorCode = row?.ErrorCode ?? errorCode,
            RedactedErrorDetail = row?.RedactedErrorDetail,
            CancellationRequested = row?.CancellationRequested ?? false,
            ExecutorInvocationCount = 0
        };
    }
}
