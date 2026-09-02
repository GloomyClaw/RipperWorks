using Microsoft.Data.Sqlite;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>RF-06 shadow FIFO/journal owner; never auto-replays or reaches UI.</summary>
public sealed class GameOperationCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, GameOperationProfileLane> _lanes =
        new(StringComparer.Ordinal);
    private readonly IGameOperationPreconditionValidator _validator;
    private readonly IGameOperationExecutor _executor;
    private readonly string? _journalPath;
    private readonly ShadowHarnessPermit? _permit;
    private readonly SemaphoreSlim _journalGate = new(1, 1);
    private readonly GameOperationSubmissionOwnership _submissionOwnership;
    private readonly GameOperationProfileSafetyGate _profileSafetyGate;
    private IGameOperationJournal? _journal;
    private bool _stopped;
    private bool _disposed;
    private Task? _disposeTask;

    internal Func<string, CancellationToken, Task>? TestOnlyBarrier { get; set; }

    internal bool TestOnlyFailAfterMutationStarted { get; set; }

    internal bool TestOnlyFailDuringFinalize { get; set; }

    internal Func<CancellationToken, Task>? TestOnlyAfterInsertAcceptedBeforeLane
    {
        get;
        set;
    }

    internal Func<CancellationToken, Task>?
        TestOnlyAfterInsertAcceptedBeforeDurableBind { get; set; }

    internal Func<CancellationToken, Task>? TestOnlyAfterLaneLookupBeforeEnqueue
    {
        get;
        set;
    }

    internal Func<CancellationToken, Task>? TestOnlyBeforeTerminalComplete
    {
        get;
        set;
    }

    internal Func<GameOperationStepSpec, bool>? TestOnlyFailAfterExecuteStep
    {
        get;
        set;
    }

    internal Func<CancellationToken, Task>? TestOnlyBeforeFinalResourceValidation
    {
        get;
        set;
    }

    internal Func<CancellationToken, Task>?
        TestOnlyBeforeFinalExecutorContractValidation { get; set; }

    internal Func<CancellationToken, Task>?
        TestOnlyAfterFirstNonterminalIdempotencyRead { get; set; }

    internal Func<CancellationToken, Task>?
        TestOnlyAfterSecondNonterminalIdempotencyRead { get; set; }

    internal Func<GameOperationPlan, CancellationToken, Task>?
        TestOnlyBeforeIdempotencyResolution { get; set; }

    internal Action<CanonicalProfileKey, IdempotencyKey>?
        TestOnlyAfterOwnedTaskRemoved { get; set; }

    internal Func<Task>? TestOnlyStopFault { get; set; }

    internal Func<Task>? TestOnlyCleanupFault { get; set; }

    internal Func<Task>? TestOnlyBeforeExecutionSession { get; set; }

    internal int TestOnlyJournalDisposeCount { get; private set; }

    internal int TestOnlySubmissionOwnerCount =>
        _submissionOwnership.TestOnlyOwnedCount;

    internal int TestOnlyAcceptanceBoundaryCount =>
        _submissionOwnership.TestOnlyProfileBoundaryCount;

    public GameOperationCoordinator(
        string? journalPath = null,
        IGameOperationPreconditionValidator? validator = null,
        IGameOperationExecutor? executor = null,
        ShadowHarnessPermit? permit = null)
    {
        _journalPath = string.IsNullOrWhiteSpace(journalPath)
            ? null
            : Path.GetFullPath(journalPath);
        _permit = permit;
        _validator = validator ?? new DefaultGameOperationPreconditionValidator();
        _executor = executor ?? new SyntheticGameOperationExecutor(_permit);
        _submissionOwnership = new GameOperationSubmissionOwnership(
            NotifyOwnedTaskRemoved);
        _profileSafetyGate = new GameOperationProfileSafetyGate(
            _submissionOwnership);
    }

    /// <summary>
    /// Journal path if configured. Null means journal not openable until
    /// constructed with an explicit path (production default).
    /// </summary>
    public string? ConfiguredJournalPath => _journalPath;

    /// <summary>
    /// Observed successful commits on the live journal instance (null if closed).
    /// </summary>
    internal long? ObservedJournalSuccessfulCommits =>
        _journal is SqliteGameOperationJournal sqlite
            ? sqlite.ObservedSuccessfulCommits
            : null;

    internal SqliteGameOperationJournalMetrics? ObservedJournalMetrics =>
        (_journal as SqliteGameOperationJournal)?.ObservedMetrics;

    public bool IsStopped
    {
        get
        {
            lock (_sync)
                return _stopped;
        }
    }

    /// <summary>
    /// Construction does not open SQLite or create directories.
    /// </summary>
    public static GameOperationCoordinator CreateLazyShadow(
        string? journalPath = null) =>
        new(journalPath);

    private async Task BarrierAsync(string name, CancellationToken cancellationToken)
    {
        if (TestOnlyBarrier is not null)
            await TestOnlyBarrier(name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GameOperationResult> SubmitAsync(
        GameOperationPlan plan,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);

        var journalAuthority = ValidateConfiguredJournalAuthority();
        if (!journalAuthority.IsAllowed)
        {
            return GameOperationExecutionSession.TerminalPublic(
                OperationId.CreateNew(),
                GameOperationState.BlockedBeforeCommit,
                plan.PlanHash,
                idempotencyKey.Value,
                journalAuthority.ErrorCode ?? "ShadowRootNotAllowed");
        }

        return await _submissionOwnership.SubmitAsync(
                plan.ProfileKey,
                idempotencyKey,
                plan.PlanHash,
                cancellationToken,
                (claim, ownedToken) => SubmitOwnedWithStopConversionAsync(
                    plan,
                    idempotencyKey,
                    claim,
                    ownedToken),
                errorCode => Blocked(plan, idempotencyKey, errorCode))
            .ConfigureAwait(false);
    }

    private async Task<GameOperationResult> SubmitOwnedWithStopConversionAsync(
        GameOperationPlan plan,
        IdempotencyKey idempotencyKey,
        GameOperationSubmissionOwnership.SubmissionClaim claim,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SubmitOwnedAsync(
                    plan,
                    idempotencyKey,
                    claim,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_submissionOwnership.IsStopping)
        {
            return Blocked(plan, idempotencyKey, "CoordinatorStopped");
        }
    }

    private async Task<GameOperationResult> SubmitOwnedAsync(
        GameOperationPlan plan,
        IdempotencyKey idempotencyKey,
        GameOperationSubmissionOwnership.SubmissionClaim claim,
        CancellationToken cancellationToken)
    {
        if (TestOnlyBeforeIdempotencyResolution is not null)
        {
            await TestOnlyBeforeIdempotencyResolution(plan, cancellationToken)
                .ConfigureAwait(false);
        }

        Task<GameOperationResult>? GetOtherDurableOwner(
            CanonicalProfileKey profileKey,
            IdempotencyKey key,
            OperationId operationId,
            string planHash) =>
            _submissionOwnership.TryGetDurableOwnedTask(
                profileKey,
                key,
                operationId,
                planHash,
                claim);

        var existingResult = await GameOperationIdempotencyResolver.TryResolveAsync(
                _journal,
                _journalPath,
                EnsureJournalAsync,
                plan,
                idempotencyKey,
                GetOtherDurableOwner,
                TestOnlyAfterFirstNonterminalIdempotencyRead,
                TestOnlyAfterSecondNonterminalIdempotencyRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingResult is not null)
            return existingResult;

        if (plan.PlanVersion != GameOperationPlanVersions.Current)
        {
            return GameOperationExecutionSession.TerminalPublic(
                OperationId.CreateNew(),
                GameOperationState.BlockedBeforeCommit,
                plan.PlanHash,
                idempotencyKey.Value,
                "UnsupportedPlanVersion");
        }

        if (_permit is null)
        {
            return GameOperationExecutionSession.TerminalPublic(
                OperationId.CreateNew(),
                GameOperationState.BlockedBeforeCommit,
                plan.PlanHash,
                idempotencyKey.Value,
                "ShadowRootNotAllowed");
        }

        var shadowRoots = _permit.ValidateMutationRoots(plan, _journalPath);
        if (!shadowRoots.IsAllowed)
        {
            return GameOperationExecutionSession.TerminalPublic(
                OperationId.CreateNew(),
                GameOperationState.BlockedBeforeCommit,
                plan.PlanHash,
                idempotencyKey.Value,
                shadowRoots.ErrorCode ?? "ShadowRootNotAllowed");
        }

        var executorBinding = GameOperationExecutorContract.ValidateBinding(
            _executor,
            _permit);
        if (!executorBinding.IsAllowed)
        {
            return GameOperationExecutionSession.TerminalPublic(
                OperationId.CreateNew(),
                GameOperationState.BlockedBeforeCommit,
                plan.PlanHash,
                idempotencyKey.Value,
                executorBinding.ErrorCode ?? "ExecutorNotHarnessBound");
        }

        var planContract = await GameOperationExecutorContract
            .ValidateBeforeAcceptanceAsync(
                _executor,
                _permit,
                plan,
                cancellationToken)
            .ConfigureAwait(false);
        if (!planContract.IsAllowed)
        {
            return GameOperationExecutionSession.TerminalPublic(
                OperationId.CreateNew(),
                GameOperationState.BlockedBeforeCommit,
                plan.PlanHash,
                idempotencyKey.Value,
                planContract.ErrorCode ?? "ExecutorPlanContractInvalid");
        }

        await BarrierAsync("B00", cancellationToken).ConfigureAwait(false);

        IGameOperationJournal journal;
        try
        {
            journal = await EnsureJournalAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "CoordinatorStopped")
        {
            return GameOperationExecutionSession.TerminalPublic(
                OperationId.CreateNew(),
                GameOperationState.BlockedBeforeCommit,
                plan.PlanHash,
                idempotencyKey.Value,
                "CoordinatorStopped");
        }

        // Validate BEFORE durable insert so blocked plans leave no journal row.
        cancellationToken.ThrowIfCancellationRequested();
        PreconditionOutcome preSubmit;
        try
        {
            preSubmit = await _validator.ValidateAsync(plan, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return GameOperationExecutionSession.TerminalPublic(
                OperationId.CreateNew(),
                GameOperationState.BlockedBeforeCommit,
                plan.PlanHash,
                idempotencyKey.Value,
                "ValidatorFault");
        }

        if (!preSubmit.IsAllowed)
        {
            return new GameOperationResult
            {
                OperationId = OperationId.CreateNew(),
                State = GameOperationState.BlockedBeforeCommit,
                PlanHash = plan.PlanHash,
                IdempotencyKey = idempotencyKey.Value,
                ErrorCode = preSubmit.ErrorCode ?? "PreconditionBlocked",
                RedactedErrorDetail = preSubmit.RedactedDetail,
                ExecutorInvocationCount = 0
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var acceptance = await _submissionOwnership.EnterAcceptanceAsync(
                plan.ProfileKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (acceptance is null)
            return Blocked(plan, idempotencyKey, "CoordinatorStopped");

        Task<GameOperationResult> laneTask;
        using (acceptance)
        {
            GameOperationProfileLane lane;
            lock (_sync)
                lane = GetOrCreateLaneUnlocked(plan.ProfileKey);
            if (lane.IsPoisoned)
                return Blocked(plan, idempotencyKey, "ProfileRecoveryRequired");

            // This is the only profile-local acceptance section: final stop
            // admission, orphan check, durable insert and FIFO enqueue.
            if (await _profileSafetyGate.IsBlockedAsync(
                        journal,
                        plan.ProfileKey,
                        currentOperationId: null,
                        CancellationToken.None)
                    .ConfigureAwait(false))
            {
                return GameOperationExecutionSession.TerminalPublic(
                    OperationId.CreateNew(),
                    GameOperationState.RecoveryRequired,
                    plan.PlanHash,
                    idempotencyKey.Value,
                    "ProfileRecoveryRequired");
            }

            var operationId = OperationId.CreateNew();
            GameOperationRecord accepted;
            try
            {
                accepted = await journal.InsertAcceptedAsync(
                        operationId,
                        plan.ProfileKey,
                        idempotencyKey,
                        plan,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return await GameOperationIdempotencyResolver
                    .ResolveConstraintWinnerAsync(
                        journal,
                        _journalPath,
                        EnsureJournalAsync,
                        plan,
                        idempotencyKey)
                    .ConfigureAwait(false);
            }

            if (TestOnlyAfterInsertAcceptedBeforeDurableBind is not null)
            {
                await TestOnlyAfterInsertAcceptedBeforeDurableBind(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            _submissionOwnership.BindDurable(
                claim,
                accepted.OperationId,
                accepted.PlanHash);

            await BarrierAsync("B01", CancellationToken.None).ConfigureAwait(false);
            if (TestOnlyAfterInsertAcceptedBeforeLane is not null)
            {
                await TestOnlyAfterInsertAcceptedBeforeLane(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (TestOnlyAfterLaneLookupBeforeEnqueue is not null)
            {
                await TestOnlyAfterLaneLookupBeforeEnqueue(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            laneTask = lane.Enqueue(
                accepted.OperationId,
                plan,
                idempotencyKey,
                cancellationToken);
            _submissionOwnership.MarkEnqueued(claim);
        }

        return await laneTask.ConfigureAwait(false);
    }

    private static GameOperationResult Blocked(
        GameOperationPlan plan,
        IdempotencyKey idempotencyKey,
        string errorCode) =>
        GameOperationExecutionSession.TerminalPublic(
            OperationId.CreateNew(),
            GameOperationState.BlockedBeforeCommit,
            plan.PlanHash,
            idempotencyKey.Value,
            errorCode);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var submissions = _submissionOwnership.BeginStop();
        await submissions.AcceptanceDrained.WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        List<GameOperationProfileLane> lanes;
        lock (_sync)
        {
            _stopped = true;
            lanes = _lanes.Values.ToList();
        }

        foreach (var lane in lanes)
            lane.CancelQueued();

        foreach (var lane in lanes)
            await lane.DrainAsync(cancellationToken).ConfigureAwait(false);

        await GameOperationSubmissionOwnership.ObserveOwnedAsync(
                submissions.OwnedTasks,
                cancellationToken)
            .ConfigureAwait(false);

        if (TestOnlyStopFault is not null)
            await TestOnlyStopFault().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                disposeTask = _disposeTask;
            }
            else
            {
                _disposeTask = DisposeCoreAsync();
                disposeTask = _disposeTask;
            }
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            await GameOperationCoordinatorCleanup.RunAsync(
                    () => StopAsync(CancellationToken.None),
                    _journalGate,
                    () => _journal,
                    () => _journal = null,
                    TestOnlyCleanupFault,
                    () => TestOnlyJournalDisposeCount++)
                .ConfigureAwait(false);
        }
        finally
        {
            _submissionOwnership.Dispose();
        }
    }

    internal Task ForceReopenJournalAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConfiguredJournalAuthority();
        if (_journalPath is null)
            throw new InvalidOperationException("JournalPathNotConfigured");
        lock (_sync)
        {
            if (!_stopped ||
                _lanes.Values.Any(lane => lane.HasActiveWork) ||
                _submissionOwnership.TestOnlyOwnedCount != 0)
                throw new InvalidOperationException("JournalReopenRequiresStoppedDrain");
        }

        return GameOperationJournalAccess.ForceReopenAsync(
            _journalGate,
            _journalPath,
            () => _journal,
            j => _journal = j,
            cancellationToken);
    }

    public Task<IReadOnlyList<GameOperationRecord>> ReopenAndListAsync(
        CanonicalProfileKey profileKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConfiguredJournalAuthority();
        if (_journalPath is null)
            throw new InvalidOperationException("JournalPathNotConfigured");
        return GameOperationJournalAccess.ListIndependentAsync(
            _journalPath,
            profileKey,
            cancellationToken);
    }

    public async Task<GameOperationRecord?> LoadOperationAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        var journal = await EnsureJournalAsync(cancellationToken)
            .ConfigureAwait(false);
        return await journal.LoadAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
    }

    private GameOperationProfileLane GetOrCreateLaneUnlocked(CanonicalProfileKey key)
    {
        if (!_lanes.TryGetValue(key.Value, out var lane))
        {
            lane = new GameOperationProfileLane(this);
            _lanes[key.Value] = lane;
        }

        return lane;
    }

    internal void NotifyOwnedTaskRemoved(
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey) =>
        TestOnlyAfterOwnedTaskRemoved?.Invoke(profileKey, idempotencyKey);

    private Task<IGameOperationJournal> EnsureJournalAsync(
        CancellationToken cancellationToken)
    {
        EnsureConfiguredJournalAuthority();
        return GameOperationJournalAccess.EnsureOpenAsync(
            _journalGate,
            _journalPath,
            () => _journal,
            j => _journal = j,
            () =>
            {
                lock (_sync)
                    return _stopped;
            },
            cancellationToken);
    }

    private PreconditionOutcome ValidateConfiguredJournalAuthority() =>
        _permit?.ValidateJournalAuthority(_journalPath) ??
        PreconditionOutcome.Block("ShadowRootNotAllowed");

    private void EnsureConfiguredJournalAuthority()
    {
        var outcome = ValidateConfiguredJournalAuthority();
        if (!outcome.IsAllowed)
            throw new InvalidOperationException("ShadowRootNotAllowed");
    }

    internal async Task<GameOperationResult> ExecuteOwnedAsync(
        OperationId operationId,
        GameOperationPlan plan,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        var journal = await EnsureJournalAsync(cancellationToken).ConfigureAwait(false);
        using (await _submissionOwnership.EnterProfileSafetyObservationAsync(
                         plan.ProfileKey,
                         CancellationToken.None)
                   .ConfigureAwait(false))
        {
            if (await _profileSafetyGate.IsBlockedAsync(
                        journal, plan.ProfileKey, operationId,
                        CancellationToken.None).ConfigureAwait(false))
            {
                return await GameOperationTerminalHelpers.TerminalFromAsync(
                        journal, operationId, GameOperationState.Accepted,
                        GameOperationState.BlockedBeforeCommit,
                        "ProfileRecoveryRequired", executorCalls: 0)
                    .ConfigureAwait(false);
            }
        }

        if (TestOnlyBeforeExecutionSession is not null)
            await TestOnlyBeforeExecutionSession().ConfigureAwait(false);

        var session = new GameOperationExecutionSession(
            journal,
            _validator,
            _executor,
            TestOnlyBarrier,
            TestOnlyFailAfterMutationStarted,
            TestOnlyFailDuringFinalize,
            () => IsStopped,
            TestOnlyBeforeTerminalComplete,
            _permit,
            TestOnlyFailAfterExecuteStep,
            TestOnlyBeforeFinalResourceValidation,
            TestOnlyBeforeFinalExecutorContractValidation);
        return await session.ExecuteAsync(
            operationId,
            plan,
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<GameOperationResult> BlockPoisonedAcceptedAsync(OperationId id)
    {
        var journal = await EnsureJournalAsync(CancellationToken.None)
            .ConfigureAwait(false);
        return await GameOperationTerminalHelpers.TerminalFromAsync(
                journal, id, GameOperationState.Accepted,
                GameOperationState.BlockedBeforeCommit, "ProfileRecoveryRequired", 0)
            .ConfigureAwait(false);
    }
}
