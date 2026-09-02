namespace RipperWorks.Organizer.GameOperations;

/// <summary>One operation: prepare (RO) → revalidate → MutationStarted → steps → finalize.</summary>
internal sealed class GameOperationExecutionSession
{
    private readonly IGameOperationJournal _journal;
    private readonly IGameOperationPreconditionValidator _validator;
    private readonly IGameOperationExecutor _executor;
    private readonly Func<string, CancellationToken, Task>? _barrierHook;
    private readonly bool _failAfterMutationStarted;
    private readonly bool _failDuringFinalize;
    private readonly Func<bool> _isStopped;
    private readonly Func<CancellationToken, Task>? _beforeTerminalComplete;
    private readonly ShadowHarnessPermit? _permit;
    private readonly Func<GameOperationStepSpec, bool>? _testOnlyFailAfterExecute;
    private readonly Func<CancellationToken, Task>? _beforeFinalResourceValidation;
    private readonly Func<CancellationToken, Task>?
        _beforeFinalExecutorContractValidation;
    private readonly IGameOperationResourceStateReader _resourceReader;

    public GameOperationExecutionSession(
        IGameOperationJournal journal,
        IGameOperationPreconditionValidator validator,
        IGameOperationExecutor executor,
        Func<string, CancellationToken, Task>? barrierHook,
        bool failAfterMutationStarted,
        bool failDuringFinalize,
        Func<bool> isStopped,
        Func<CancellationToken, Task>? beforeTerminalComplete = null,
        ShadowHarnessPermit? permit = null,
        Func<GameOperationStepSpec, bool>? testOnlyFailAfterExecute = null,
        Func<CancellationToken, Task>? beforeFinalResourceValidation = null,
        Func<CancellationToken, Task>? beforeFinalExecutorContractValidation = null)
    {
        _journal = journal;
        _validator = validator;
        _executor = executor;
        _barrierHook = barrierHook;
        _failAfterMutationStarted = failAfterMutationStarted;
        _failDuringFinalize = failDuringFinalize;
        _isStopped = isStopped;
        _beforeTerminalComplete = beforeTerminalComplete;
        _permit = permit;
        _testOnlyFailAfterExecute = testOnlyFailAfterExecute;
        _beforeFinalResourceValidation = beforeFinalResourceValidation;
        _beforeFinalExecutorContractValidation =
            beforeFinalExecutorContractValidation;
        _resourceReader = (executor as IShadowHarnessBoundExecutor)?
            .ResourceStateReader ?? FileSystemGameOperationResourceStateReader.Shared;
    }

    private async Task BarrierAsync(
        string name,
        CancellationToken cancellationToken)
    {
        if (_barrierHook is not null)
        {
            await _barrierHook(name, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<GameOperationResult> ExecuteAsync(
        OperationId operationId,
        GameOperationPlan plan,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        var journal = _journal;
        var executorCalls = 0;
        var writeCount = 0;
        CountingGameOperationExecutor? counting = _executor as CountingGameOperationExecutor;
        var beforeWrites = counting?.WriteCount ?? 0;

        try
        {
            await journal.TransitionAsync(
                    operationId,
                    GameOperationState.Accepted,
                    GameOperationState.Queued,
                    GameOperationPhase.Queued,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await BarrierAsync("B02", cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested ||
                _isStopped())
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.Queued,
                        GameOperationState.CancelledBeforeCommit,
                        GameOperationPhase.Terminal,
                        "InstallationCanceled",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(
                        journal,
                        operationId,
                        executorCalls,
                        0)
                    .ConfigureAwait(false);
            }

            await journal.TransitionAsync(
                    operationId,
                    GameOperationState.Queued,
                    GameOperationState.Validating,
                    GameOperationPhase.Validating,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            PreconditionOutcome outcome;
            try
            {
                outcome = await _validator.ValidateAsync(
                        plan,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.Validating,
                        GameOperationState.CancelledBeforeCommit,
                        GameOperationPhase.Terminal,
                        "InstallationCanceled",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(
                        journal,
                        operationId,
                        0,
                        0)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.Validating,
                        GameOperationState.BlockedBeforeCommit,
                        GameOperationPhase.Terminal,
                        "ValidatorFault",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(
                        journal,
                        operationId,
                        0,
                        0)
                    .ConfigureAwait(false);
            }

            await BarrierAsync("B03", cancellationToken).ConfigureAwait(false);

            if (!outcome.IsAllowed)
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.Validating,
                        GameOperationState.BlockedBeforeCommit,
                        GameOperationPhase.Terminal,
                        outcome.ErrorCode ?? "PreconditionBlocked",
                        outcome.RedactedDetail,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(
                        journal,
                        operationId,
                        0,
                        0)
                    .ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested || _isStopped())
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.Validating,
                        GameOperationState.CancelledBeforeCommit,
                        GameOperationPhase.Terminal,
                        "InstallationCanceled",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(
                        journal,
                        operationId,
                        0,
                        0)
                    .ConfigureAwait(false);
            }

            await journal.TransitionAsync(
                    operationId,
                    GameOperationState.Validating,
                    GameOperationState.ReadyToCommit,
                    GameOperationPhase.ReadyToCommit,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (_permit is null)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.BlockedBeforeCommit,
                        "ExecutorNotHarnessBound", executorCalls)
                    .ConfigureAwait(false);
            }

            // Reject an already-stale destination/reparse path before even the
            // read-only executor Prepare call, so obvious mismatches have zero
            // executor invocations. The same validation runs again immediately
            // before MutationStarted to close the Prepare/revalidation window.
            PreconditionOutcome initialResourceCheck;
            try
            {
                initialResourceCheck = await GameOperationResourcePrevalidator
                    .ValidateExpectedBeforeAsync(
                        plan,
                        _permit,
                        cancellationToken,
                        _resourceReader)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.CancelledBeforeCommit,
                        "InstallationCanceled", executorCalls)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.BlockedBeforeCommit,
                        "ResourceValidationFault", executorCalls)
                    .ConfigureAwait(false);
            }

            if (!initialResourceCheck.IsAllowed)
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.ReadyToCommit,
                        GameOperationState.BlockedBeforeCommit,
                        GameOperationPhase.Terminal,
                        initialResourceCheck.ErrorCode ?? "ExpectedBeforeMismatch",
                        initialResourceCheck.RedactedDetail,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(
                        journal, operationId, executorCalls, 0)
                    .ConfigureAwait(false);
            }

            if (_beforeFinalResourceValidation is not null)
            {
                await _beforeFinalResourceValidation(cancellationToken)
                    .ConfigureAwait(false);
                var seamCheck = await GameOperationResourcePrevalidator
                    .ValidateExpectedBeforeAsync(
                        plan,
                        _permit,
                        cancellationToken,
                        _resourceReader)
                    .ConfigureAwait(false);
                if (!seamCheck.IsAllowed)
                {
                    await journal.TransitionAsync(
                            operationId,
                            GameOperationState.ReadyToCommit,
                            GameOperationState.BlockedBeforeCommit,
                            GameOperationPhase.Terminal,
                            seamCheck.ErrorCode ?? "ExpectedBeforeMismatch",
                            seamCheck.RedactedDetail,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return await GameOperationTerminalHelpers.LoadResultAsync(
                            journal, operationId, executorCalls, 0)
                        .ConfigureAwait(false);
                }
            }

            try
            {
                await _executor.PrepareAsync(plan, cancellationToken)
                    .ConfigureAwait(false);
                executorCalls++;
            }
            catch (OperationCanceledException)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.CancelledBeforeCommit,
                        "InstallationCanceled", executorCalls)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.FailedBeforeCommit,
                        "PrepareFailed", executorCalls)
                    .ConfigureAwait(false);
            }

            PreconditionOutcome finalCheck;
            try
            {
                finalCheck = await _validator.ValidateAsync(plan, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.CancelledBeforeCommit,
                        "InstallationCanceled", executorCalls)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.BlockedBeforeCommit,
                        "ValidatorFault", executorCalls)
                    .ConfigureAwait(false);
            }

            if (!finalCheck.IsAllowed)
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.ReadyToCommit,
                        GameOperationState.BlockedBeforeCommit,
                        GameOperationPhase.Terminal,
                        finalCheck.ErrorCode ?? "PreconditionBlocked",
                        finalCheck.RedactedDetail,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(journal, operationId, executorCalls, 0)
                    .ConfigureAwait(false);
            }

            if (_beforeFinalExecutorContractValidation is not null)
            {
                await _beforeFinalExecutorContractValidation(cancellationToken)
                    .ConfigureAwait(false);
            }

            // Durable plan/executor contract is intentionally after read-only
            // Prepare and the final ordinary validator, immediately before the
            // final ExpectedBefore check and MutationStarted.
            var persistedValidation = await GameOperationExecutorContract
                .ValidatePersistedAsync(
                    journal,
                    operationId,
                    _executor,
                    _permit,
                    plan,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!persistedValidation.Outcome.IsAllowed ||
                persistedValidation.Plan is null)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.BlockedBeforeCommit,
                        persistedValidation.Outcome.ErrorCode ??
                            "ExecutorPlanContractInvalid",
                        executorCalls)
                    .ConfigureAwait(false);
            }

            var durablePlan = persistedValidation.Plan;

            // Final read-only ExpectedBefore resource check before MutationStarted.
            PreconditionOutcome resourceCheck;
            try
            {
                resourceCheck = await GameOperationResourcePrevalidator
                    .ValidateExpectedBeforeAsync(
                        durablePlan,
                        _permit,
                        cancellationToken,
                        _resourceReader)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.CancelledBeforeCommit,
                        "InstallationCanceled", executorCalls)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.BlockedBeforeCommit,
                        "ResourceValidationFault", executorCalls)
                    .ConfigureAwait(false);
            }

            if (!resourceCheck.IsAllowed)
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.ReadyToCommit,
                        GameOperationState.BlockedBeforeCommit,
                        GameOperationPhase.Terminal,
                        resourceCheck.ErrorCode ?? "ExpectedBeforeMismatch",
                        resourceCheck.RedactedDetail,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(
                        journal, operationId, executorCalls, 0)
                    .ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested || _isStopped())
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.ReadyToCommit,
                        GameOperationState.CancelledBeforeCommit,
                        "InstallationCanceled", executorCalls)
                    .ConfigureAwait(false);
            }

            await journal.TransitionAsync(
                    operationId,
                    GameOperationState.ReadyToCommit,
                    GameOperationState.MutationStarted,
                    GameOperationPhase.MutationStarted,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            await BarrierAsync("B04", CancellationToken.None)
                .ConfigureAwait(false);

            if (_failAfterMutationStarted)
            {
                await journal.TransitionAsync(
                        operationId,
                        GameOperationState.MutationStarted,
                        GameOperationState.RecoveryRequired,
                        GameOperationPhase.Terminal,
                        "PostCommitFault",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(
                        journal,
                        operationId,
                        executorCalls,
                        0)
                    .ConfigureAwait(false);
            }

            var runner = new GameOperationPostCommitRunner(
                journal,
                _executor,
                _barrierHook,
                _failDuringFinalize,
                _beforeTerminalComplete,
                _testOnlyFailAfterExecute);
            return await runner.RunAsync(
                    operationId,
                    durablePlan,
                    executorCalls,
                    counting,
                    beforeWrites,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GameOperationCancellationPersistenceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            !_isStopped())
        {
            var current = await journal.LoadAsync(
                    operationId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (current is not null &&
                current.State is GameOperationState.MutationStarted
                    or GameOperationState.Executing
                    or GameOperationState.Finalizing)
            {
                await journal.SetCancellationRequestedAsync(
                        operationId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await GameOperationTerminalHelpers.LoadResultAsync(
                        journal,
                        operationId,
                        executorCalls,
                        writeCount)
                    .ConfigureAwait(false);
            }

            if (current is not null && !GameOperationTerminalHelpers.IsTerminalState(current.State))
            {
                await journal.TransitionAsync(
                        operationId,
                        current.State,
                        GameOperationState.CancelledBeforeCommit,
                        GameOperationPhase.Terminal,
                        "InstallationCanceled",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return await GameOperationTerminalHelpers.LoadResultAsync(
                    journal,
                    operationId,
                    executorCalls,
                    writeCount)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            var current = await journal.LoadAsync(
                    operationId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (current is not null &&
                current.State is GameOperationState.MutationStarted
                    or GameOperationState.Executing
                    or GameOperationState.Finalizing)
            {
                if (!GameOperationTerminalHelpers.IsTerminalState(current.State))
                {
                    await journal.TransitionAsync(
                            operationId,
                            current.State,
                            GameOperationState.RecoveryRequired,
                            GameOperationPhase.Terminal,
                            "UnexpectedFault",
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            else if (current is not null && !GameOperationTerminalHelpers.IsTerminalState(current.State))
            {
                var preCommitTerminal =
                    current.State is GameOperationState.Accepted
                        or GameOperationState.Queued
                        or GameOperationState.Validating
                        or GameOperationState.ReadyToCommit
                        ? GameOperationState.FailedBeforeCommit
                        : GameOperationState.RecoveryRequired;
                try
                {
                    await journal.TransitionAsync(
                            operationId,
                            current.State,
                            preCommitTerminal,
                            GameOperationPhase.Terminal,
                            "UnexpectedFault",
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    try
                    {
                        await journal.TransitionAsync(
                                operationId,
                                current.State,
                                GameOperationState.CancelledBeforeCommit,
                                GameOperationPhase.Terminal,
                                "UnexpectedFault",
                                cancellationToken: CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already moved; return loaded state.
                    }
                }
            }

            return await GameOperationTerminalHelpers.LoadResultAsync(
                    journal,
                    operationId,
                    executorCalls,
                    writeCount)
                .ConfigureAwait(false);
        }
    }

    internal static bool IsTerminalState(GameOperationState state) =>
        GameOperationTerminalHelpers.IsTerminalState(state);

    internal static GameOperationResult TerminalPublic(
        OperationId id,
        GameOperationState state,
        string planHash,
        string key,
        string? code) =>
        GameOperationTerminalHelpers.TerminalPublic(id, state, planHash, key, code);

    internal static Task<GameOperationResult> TerminalizeUnownedAcceptedAsync(
        IGameOperationJournal journal,
        OperationId operationId,
        string planHash,
        string idempotencyKey,
        string errorCode) =>
        GameOperationTerminalHelpers.TerminalizeUnownedAcceptedAsync(
            journal, operationId, planHash, idempotencyKey, errorCode);
}
