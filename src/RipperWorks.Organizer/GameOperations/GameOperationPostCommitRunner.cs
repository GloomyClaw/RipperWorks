namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Post-MutationStarted execution and the single owner of post-commit
/// cancellation registration, persistence, and final result loading.
/// </summary>
internal sealed class GameOperationPostCommitRunner
{
    private readonly IGameOperationJournal _journal;
    private readonly IGameOperationExecutor _executor;
    private readonly Func<string, CancellationToken, Task>? _barrierHook;
    private readonly bool _failDuringFinalize;
    private readonly Func<CancellationToken, Task>? _beforeTerminalComplete;
    private readonly Func<GameOperationStepSpec, bool>? _testOnlyFailAfterExecute;

    public GameOperationPostCommitRunner(
        IGameOperationJournal journal,
        IGameOperationExecutor executor,
        Func<string, CancellationToken, Task>? barrierHook,
        bool failDuringFinalize,
        Func<CancellationToken, Task>? beforeTerminalComplete = null,
        Func<GameOperationStepSpec, bool>? testOnlyFailAfterExecute = null)
    {
        _journal = journal;
        _executor = executor;
        _barrierHook = barrierHook;
        _failDuringFinalize = failDuringFinalize;
        _beforeTerminalComplete = beforeTerminalComplete;
        _testOnlyFailAfterExecute = testOnlyFailAfterExecute;
    }

    public async Task<GameOperationResult> RunAsync(
        OperationId operationId,
        GameOperationPlan plan,
        int executorCalls,
        CountingGameOperationExecutor? counting,
        int beforeWrites,
        CancellationToken cancellationToken)
    {
        var cancel = new CancelState();
        var registration = cancellationToken.Register(
            static state =>
            {
                var owner = (CancelState)state!;
                Interlocked.Exchange(ref owner.Requested, 1);
            },
            cancel);
        var registrationClosed = false;

        try
        {
            await ObserveCancelAsync(operationId, cancel).ConfigureAwait(false);
            await _journal.TransitionAsync(
                    operationId,
                    GameOperationState.MutationStarted,
                    GameOperationState.Executing,
                    GameOperationPhase.Executing,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            var steps = await ExecuteStepsAsync(
                    operationId,
                    plan,
                    cancel,
                    counting,
                    beforeWrites,
                    executorCalls)
                .ConfigureAwait(false);
            executorCalls = steps.ExecutorCalls;

            if (!steps.RecoveryRequired)
            {
                await ObserveCancelAsync(operationId, cancel).ConfigureAwait(false);
                await BarrierAsync("B08").ConfigureAwait(false);
                await _journal.TransitionAsync(
                        operationId,
                        GameOperationState.Executing,
                        GameOperationState.Finalizing,
                        GameOperationPhase.Finalizing,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);

                await ObserveCancelAsync(operationId, cancel).ConfigureAwait(false);
                try
                {
                    if (_failDuringFinalize)
                        throw new IOException("FinalizeFault");
                    await _executor.FinalizeAsync(plan, CancellationToken.None)
                        .ConfigureAwait(false);
                    executorCalls++;
                }
                catch
                {
                    await ObserveCancelAsync(operationId, cancel)
                        .ConfigureAwait(false);
                    await _journal.TransitionAsync(
                            operationId,
                            GameOperationState.Finalizing,
                            GameOperationState.RecoveryRequired,
                            GameOperationPhase.Terminal,
                            "FinalizeFault",
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }

                var current = await _journal.LoadAsync(
                        operationId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (current?.State == GameOperationState.Finalizing)
                {
                    if (_beforeTerminalComplete is not null)
                    {
                        await _beforeTerminalComplete(CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    await ObserveCancelAsync(operationId, cancel)
                        .ConfigureAwait(false);
                    await _journal.TransitionAsync(
                            operationId,
                            GameOperationState.Finalizing,
                            GameOperationState.Completed,
                            GameOperationPhase.Terminal,
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                    await BarrierAsync("B09").ConfigureAwait(false);
                }
            }

            // Close the callback window before the final durable observation.
            // CancellationTokenRegistration.Dispose waits for an active callback.
            registration.Dispose();
            registrationClosed = true;
            await ObserveCancelAsync(operationId, cancel).ConfigureAwait(false);

            var writes = (counting?.WriteCount ?? 0) - beforeWrites;
            return await GameOperationTerminalHelpers.LoadResultAsync(
                    _journal,
                    operationId,
                    executorCalls,
                    writes)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!registrationClosed)
                registration.Dispose();
        }
    }

    private async Task<(bool RecoveryRequired, int ExecutorCalls)> ExecuteStepsAsync(
        OperationId operationId,
        GameOperationPlan plan,
        CancelState cancel,
        CountingGameOperationExecutor? counting,
        int beforeWrites,
        int executorCalls)
    {
        foreach (var step in plan.Steps.OrderBy(s => s.Sequence))
        {
            await ObserveCancelAsync(operationId, cancel).ConfigureAwait(false);
            await _journal.RecordStepIntentAsync(
                    operationId,
                    step,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await BarrierAsync("B05").ConfigureAwait(false);

            try
            {
                await _executor.ExecuteStepAsync(
                        plan,
                        step,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                executorCalls++;
                if (_testOnlyFailAfterExecute?.Invoke(step) == true)
                    throw new IOException("TestOnlyPostExecuteFault");
                await BarrierAsync("B06").ConfigureAwait(false);
                await ObserveCancelAsync(operationId, cancel).ConfigureAwait(false);
                await _journal.RecordStepAppliedAsync(
                        operationId,
                        step.Sequence,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await BarrierAsync("B07").ConfigureAwait(false);
                await ObserveCancelAsync(operationId, cancel).ConfigureAwait(false);
                await _executor.VerifyStepAsync(
                        plan,
                        step,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await _journal.RecordStepVerifiedAsync(
                        operationId,
                        step.Sequence,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (GameOperationCancellationPersistenceException)
            {
                throw;
            }
            catch
            {
                await ObserveCancelAsync(operationId, cancel).ConfigureAwait(false);
                await _journal.TransitionAsync(
                        operationId,
                        GameOperationState.Executing,
                        GameOperationState.RecoveryRequired,
                        GameOperationPhase.Terminal,
                        "ExecutorFault",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                _ = counting;
                _ = beforeWrites;
                return (true, executorCalls);
            }
        }

        return (false, executorCalls);
    }

    private async Task ObserveCancelAsync(
        OperationId operationId,
        CancelState cancel)
    {
        if (Volatile.Read(ref cancel.Requested) == 0 || cancel.Durable)
            return;

        try
        {
            await _journal.SetCancellationRequestedAsync(
                    operationId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            cancel.Durable = true;
        }
        catch (Exception exception)
        {
            throw new GameOperationCancellationPersistenceException(exception);
        }
    }

    private async Task BarrierAsync(string name)
    {
        if (_barrierHook is not null)
            await _barrierHook(name, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class CancelState
    {
        public int Requested;
        public bool Durable;
    }
}

internal sealed class GameOperationCancellationPersistenceException :
    InvalidOperationException
{
    public GameOperationCancellationPersistenceException(Exception innerException)
        : base("CancellationPersistenceFault", innerException)
    {
    }
}
