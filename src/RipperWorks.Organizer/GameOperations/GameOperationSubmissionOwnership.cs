namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Process-local ownership for one logical submit and its short profile-local
/// durable-acceptance boundary. Durable idempotency remains journal authority.
/// </summary>
internal sealed class GameOperationSubmissionOwnership : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<SubmissionKey, OwnedSubmission> _owned = [];
    private readonly Dictionary<string, ProfileAcceptanceGate> _profileGates =
        new(StringComparer.Ordinal);
    private readonly Action<CanonicalProfileKey, IdempotencyKey> _afterRemoved;
    private bool _accepting = true;
    private bool _disposed;
    private int _activeAcceptanceSections;
    private TaskCompletionSource? _acceptanceDrained;

    public GameOperationSubmissionOwnership(
        Action<CanonicalProfileKey, IdempotencyKey> afterRemoved) =>
        _afterRemoved = afterRemoved;

    public async Task<GameOperationResult> SubmitAsync(
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey,
        string planHash,
        CancellationToken callerCancellationToken,
        Func<SubmissionClaim, CancellationToken, Task<GameOperationResult>> execute,
        Func<string, GameOperationResult> blocked)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(blocked);
        while (true)
        {
            var claim = Claim(
                profileKey,
                idempotencyKey,
                planHash,
                callerCancellationToken);
            if (claim.Kind is ClaimKind.Stopped)
                return blocked("CoordinatorStopped");
            if (claim.Kind is ClaimKind.Conflict)
                return blocked("IdempotencyConflict");
            if (claim.Kind is ClaimKind.AwaitingResolution)
            {
                await claim.Resolution!.WaitAsync(callerCancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (claim.Kind is ClaimKind.Joined)
            {
                var joined = await claim.Completion!.ConfigureAwait(false);
                return joined with
                {
                    JoinedExisting = true,
                    ExecutorInvocationCount = 0,
                    SyntheticWriteCount = 0
                };
            }

            claim.Owned!.Start(token => execute(claim, token));
            return await claim.Completion!.ConfigureAwait(false);
        }
    }

    private SubmissionClaim Claim(
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey,
        string planHash,
        CancellationToken ownerCancellationToken)
    {
        lock (_sync)
        {
            if (!_accepting || _disposed)
                return SubmissionClaim.Stopped();

            var key = new SubmissionKey(profileKey.Value, idempotencyKey.Value);
            if (_owned.TryGetValue(key, out var existing))
            {
                if (!existing.IsDurableBound)
                    return SubmissionClaim.AwaitingResolution(existing);

                return string.Equals(
                        existing.PlanHash,
                        planHash,
                        StringComparison.Ordinal)
                    ? SubmissionClaim.Joined(existing)
                    : SubmissionClaim.Conflict();
            }

            var owned = new OwnedSubmission(
                this,
                profileKey,
                idempotencyKey,
                planHash,
                ownerCancellationToken);
            _owned.Add(key, owned);
            return SubmissionClaim.Owner(owned);
        }
    }

    public Task<GameOperationResult>? TryGetDurableOwnedTask(
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey,
        OperationId operationId,
        string planHash,
        SubmissionClaim? excluding = null)
    {
        lock (_sync)
        {
            var key = new SubmissionKey(profileKey.Value, idempotencyKey.Value);
            if (!_owned.TryGetValue(key, out var owned) ||
                ReferenceEquals(owned, excluding?.Owned) ||
                !owned.IsDurableBound ||
                owned.OperationId != operationId ||
                !string.Equals(owned.PlanHash, planHash, StringComparison.Ordinal))
            {
                return null;
            }

            return owned.Completion;
        }
    }

    public bool HasDurableOwner(
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey,
        OperationId operationId,
        string planHash) =>
        TryGetDurableOwnedTask(
            profileKey,
            idempotencyKey,
            operationId,
            planHash) is not null;

    public async Task<AcceptanceLease?> EnterAcceptanceAsync(
        CanonicalProfileKey profileKey,
        CancellationToken cancellationToken)
    {
        ProfileAcceptanceGate gate;
        lock (_sync)
        {
            if (!_accepting || _disposed)
                return null;
            if (!_profileGates.TryGetValue(profileKey.Value, out gate!))
            {
                gate = new ProfileAcceptanceGate();
                _profileGates.Add(profileKey.Value, gate);
            }
        }

        await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (!_accepting || _disposed)
            {
                gate.Semaphore.Release();
                return null;
            }

            _activeAcceptanceSections++;
            return new AcceptanceLease(this, gate);
        }
    }

    public async Task<ProfileSafetyObservationLease>
        EnterProfileSafetyObservationAsync(
            CanonicalProfileKey profileKey,
            CancellationToken cancellationToken)
    {
        ProfileAcceptanceGate gate;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_profileGates.TryGetValue(profileKey.Value, out gate!))
                throw new InvalidOperationException("ProfileGateNotFound");
        }

        await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ProfileSafetyObservationLease(gate);
    }

    public void MarkEnqueued(SubmissionClaim claim) =>
        claim.Owned!.MarkEnqueued();

    public void BindDurable(
        SubmissionClaim claim,
        OperationId operationId,
        string planHash)
    {
        lock (_sync)
        {
            var owned = claim.Owned ??
                throw new InvalidOperationException("SubmissionClaimHasNoOwner");
            var key = new SubmissionKey(
                owned.ProfileKey.Value,
                owned.IdempotencyKey.Value);
            if (!_owned.TryGetValue(key, out var current) ||
                !ReferenceEquals(current, owned))
            {
                throw new InvalidOperationException("SubmissionClaimNotCurrent");
            }

            if (!string.Equals(owned.PlanHash, planHash, StringComparison.Ordinal))
                throw new InvalidOperationException("DurablePlanHashMismatch");
            owned.BindDurable(operationId);
        }
    }

    public StopSnapshot BeginStop()
    {
        OwnedSubmission[] owned;
        Task acceptanceDrained;
        lock (_sync)
        {
            _accepting = false;
            owned = _owned.Values.ToArray();
            if (_activeAcceptanceSections == 0)
            {
                acceptanceDrained = Task.CompletedTask;
            }
            else
            {
                _acceptanceDrained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                acceptanceDrained = _acceptanceDrained.Task;
            }
        }

        foreach (var submission in owned)
            submission.CancelForStopBeforeEnqueue();
        return new StopSnapshot(
            acceptanceDrained,
            owned.Select(item => item.Observation).ToArray());
    }

    public static async Task ObserveOwnedAsync(
        IReadOnlyList<Task> tasks,
        CancellationToken cancellationToken)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Submission cancellation is a completed, observed outcome.
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // The submitting caller receives the fault; shutdown observes it
                // without preventing remaining owned submissions from draining.
            }
        }
    }

    internal int TestOnlyOwnedCount
    {
        get
        {
            lock (_sync)
                return _owned.Count;
        }
    }

    internal bool IsStopping
    {
        get
        {
            lock (_sync)
                return !_accepting;
        }
    }

    internal int TestOnlyProfileBoundaryCount
    {
        get
        {
            lock (_sync)
                return _profileGates.Count;
        }
    }

    public void Dispose()
    {
        ProfileAcceptanceGate[] gates;
        lock (_sync)
        {
            if (_disposed)
                return;
            if (_owned.Count != 0 || _activeAcceptanceSections != 0)
                throw new InvalidOperationException("SubmissionOwnershipNotDrained");
            _disposed = true;
            _accepting = false;
            gates = _profileGates.Values.ToArray();
            _profileGates.Clear();
        }

        foreach (var gate in gates)
            gate.Semaphore.Dispose();
    }

    private void ExitAcceptance(ProfileAcceptanceGate gate)
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            _activeAcceptanceSections--;
            if (_activeAcceptanceSections == 0 && !_accepting)
            {
                drained = _acceptanceDrained;
                _acceptanceDrained = null;
            }
        }

        gate.Semaphore.Release();
        drained?.TrySetResult();
    }

    private void Remove(OwnedSubmission submission)
    {
        lock (_sync)
        {
            var key = new SubmissionKey(
                submission.ProfileKey.Value,
                submission.IdempotencyKey.Value);
            if (_owned.TryGetValue(key, out var current) &&
                ReferenceEquals(current, submission))
            {
                _owned.Remove(key);
            }
        }

        submission.ReleaseResolutionWaiters();
        _afterRemoved(submission.ProfileKey, submission.IdempotencyKey);
    }

    internal sealed class AcceptanceLease : IDisposable
    {
        private GameOperationSubmissionOwnership? _owner;
        private readonly ProfileAcceptanceGate _gate;

        internal AcceptanceLease(
            GameOperationSubmissionOwnership owner,
            ProfileAcceptanceGate gate) =>
            (_owner, _gate) = (owner, gate);

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ExitAcceptance(_gate);
    }

    internal sealed class ProfileSafetyObservationLease : IDisposable
    {
        private ProfileAcceptanceGate? _gate;

        internal ProfileSafetyObservationLease(ProfileAcceptanceGate gate) =>
            _gate = gate;

        public void Dispose() =>
            Interlocked.Exchange(ref _gate, null)?.Semaphore.Release();
    }

    internal sealed class SubmissionClaim
    {
        private SubmissionClaim(ClaimKind kind, OwnedSubmission? owned) =>
            (Kind, Owned) = (kind, owned);

        public ClaimKind Kind { get; }
        internal OwnedSubmission? Owned { get; }
        public Task<GameOperationResult>? Completion => Owned?.Completion;
        public Task? Resolution => Owned?.Resolution;

        internal static SubmissionClaim Owner(OwnedSubmission owned) =>
            new(ClaimKind.Owner, owned);
        internal static SubmissionClaim Joined(OwnedSubmission owned) =>
            new(ClaimKind.Joined, owned);
        internal static SubmissionClaim AwaitingResolution(OwnedSubmission owned) =>
            new(ClaimKind.AwaitingResolution, owned);
        public static SubmissionClaim Conflict() => new(ClaimKind.Conflict, null);
        public static SubmissionClaim Stopped() => new(ClaimKind.Stopped, null);
    }

    internal enum ClaimKind
    {
        Owner,
        Joined,
        AwaitingResolution,
        Conflict,
        Stopped
    }

    internal sealed record StopSnapshot(
        Task AcceptanceDrained,
        IReadOnlyList<Task> OwnedTasks);

    internal sealed class OwnedSubmission
    {
        private readonly GameOperationSubmissionOwnership _owner;
        private readonly CancellationTokenSource _ownerCancellation;
        private readonly TaskCompletionSource<GameOperationResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fullyRemoved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resolution =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _observation;
        private Task? _executionTask;
        private OperationId? _operationId;
        private int _enqueued;

        public OwnedSubmission(
            GameOperationSubmissionOwnership owner,
            CanonicalProfileKey profileKey,
            IdempotencyKey idempotencyKey,
            string planHash,
            CancellationToken ownerCancellationToken)
        {
            _owner = owner;
            ProfileKey = profileKey;
            IdempotencyKey = idempotencyKey;
            PlanHash = planHash;
            _ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                ownerCancellationToken);
            _observation = ObserveCompletionAndRemovalAsync();
        }

        public CanonicalProfileKey ProfileKey { get; }
        public IdempotencyKey IdempotencyKey { get; }
        public string PlanHash { get; }
        public Task<GameOperationResult> Completion => _completion.Task;
        public Task Observation => _observation;
        public Task Resolution => _resolution.Task;
        public bool IsDurableBound => _operationId is not null;
        public OperationId? OperationId => _operationId;

        public void Start(Func<CancellationToken, Task<GameOperationResult>> execute)
        {
            if (_executionTask is not null)
                throw new InvalidOperationException("SubmissionAlreadyStarted");
            _executionTask = CompleteAsync(execute);
        }

        public void MarkEnqueued() => Volatile.Write(ref _enqueued, 1);

        public void BindDurable(OperationId operationId)
        {
            if (_operationId is not null)
                throw new InvalidOperationException("SubmissionAlreadyDurableBound");
            _operationId = operationId;
            _resolution.TrySetResult();
        }

        public void ReleaseResolutionWaiters() => _resolution.TrySetResult();

        public void CancelForStopBeforeEnqueue()
        {
            if (Volatile.Read(ref _enqueued) == 0)
            {
                try
                {
                    _ownerCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Completion won the race and already removed this owner.
                }
            }
        }

        private async Task CompleteAsync(
            Func<CancellationToken, Task<GameOperationResult>> execute)
        {
            try
            {
                var result = await execute(_ownerCancellation.Token)
                    .ConfigureAwait(false);
                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException exception)
            {
                _completion.TrySetException(exception);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                try
                {
                    _owner.Remove(this);
                }
                finally
                {
                    _ownerCancellation.Dispose();
                    _fullyRemoved.TrySetResult();
                }
            }
        }

        private async Task ObserveCompletionAndRemovalAsync()
        {
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch
            {
                // Observe the exact shared fault; callers still receive it from
                // Completion, while lifecycle observation itself can drain.
            }
            finally
            {
                await _fullyRemoved.Task.ConfigureAwait(false);
            }
        }
    }

    internal sealed class ProfileAcceptanceGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
    }

    private readonly record struct SubmissionKey(
        string CanonicalProfileKey,
        string IdempotencyKey);
}
