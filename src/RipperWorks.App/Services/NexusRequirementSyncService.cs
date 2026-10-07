using RipperWorks.Core;
using RipperWorks.Downloader;

namespace RipperWorks.App.Services;

public sealed class NexusRequirementSyncService :
    INexusRequirementRefreshService,
    INexusRequirementRelationsService
{
    private readonly INexusGraphQlRequirementClient _client;
    private readonly INexusModernRequirementClient? _modernClient;
    private readonly INexusModContentMetadataClient? _contentMetadataClient;
    private readonly NexusAdultContentAccessPolicy _adultContentPolicy;
    private readonly NexusRequirementSnapshotStore _store;
    private readonly NexusRequirementAvailabilityResolver _availability;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IDownloadsModuleBoundary?>? _downloads;
    private readonly object _storeReadyGate = new();
    private Task? _storeReady;
    private readonly object _inFlightGate = new();
    private readonly Dictionary<NexusModIdentity, InFlightRefresh> _inFlight = [];
    private readonly HashSet<NexusModIdentity> _ambiguousMods = [];

    public NexusRequirementSyncService(
        INexusGraphQlRequirementClient client,
        NexusRequirementSnapshotStore store,
        NexusRequirementAvailabilityResolver availability,
        Func<DateTimeOffset>? utcNow = null,
        Func<IDownloadsModuleBoundary?>? downloads = null,
        INexusModernRequirementClient? modernClient = null,
        INexusModContentMetadataClient? contentMetadataClient = null,
        NexusAdultContentAccessPolicy? adultContentPolicy = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _availability = availability ??
            throw new ArgumentNullException(nameof(availability));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _downloads = downloads;
        _modernClient = modernClient;
        _contentMetadataClient = contentMetadataClient;
        _adultContentPolicy = adultContentPolicy ??
            new NexusAdultContentAccessPolicy(
                UnknownNexusAdultContentPermissionProvider.Instance);
    }

    public Task<NexusRequirementSyncResult> RefreshAsync(
        NexusModIdentity queriedMod,
        CancellationToken cancellationToken) =>
        RefreshAsync(queriedMod, clearDismissals: false, cancellationToken);

    public async Task<NexusRequirementSyncResult> RefreshAsync(
        NexusModIdentity queriedMod,
        bool clearDismissals = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NexusRequirementSyncResult result;
        while (true)
        {
            InFlightRefresh? operation;
            Task<NexusRequirementSyncResult>? retiring = null;
            lock (_inFlightGate)
            {
                if (_inFlight.TryGetValue(queriedMod, out operation))
                {
                    if (operation.Cancellation.IsCancellationRequested)
                    {
                        retiring = operation.Task;
                    }
                    else
                    {
                        operation.WaiterCount++;
                    }
                }
                else
                {
                    operation = new();
                    operation.WaiterCount = 1;
                    _inFlight.Add(queriedMod, operation);
                    operation.Task = RefreshAndReleaseAsync(queriedMod, operation);
                }
            }

            if (retiring is not null)
            {
                try
                {
                    await retiring.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                }
                continue;
            }

            result = await AwaitOperationAsync(
                operation!,
                cancellationToken).ConfigureAwait(false);
            break;
        }

        if (clearDismissals)
        {
            var keysToClear = new List<NexusRequirementCanonicalKey>();
            if (result.Forward.Outcome == NexusRequirementTraversalOutcome.Complete)
            {
                keysToClear.AddRange(result.Forward.FreshCanonicalKeys);
            }
            if (result.Reverse.Outcome == NexusRequirementTraversalOutcome.Complete)
            {
                keysToClear.AddRange(result.Reverse.FreshCanonicalKeys);
            }
            var distinctKeys = keysToClear.Distinct().ToArray();
            if (distinctKeys.Length > 0)
            {
                await _store.ClearDismissalsForEdgesAsync(
                    distinctKeys,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return result;
    }

    public async Task<NexusRequirementRelations> LoadRelationsAsync(
        NexusModIdentity queriedMod,
        CancellationToken cancellationToken = default)
    {
        await EnsureStoreReadyAsync(queriedMod, cancellationToken)
            .ConfigureAwait(false);
        var forwardOwner = new NexusRequirementSnapshotOwner(
            queriedMod,
            NexusRequirementTraversal.ForwardRequirements);
        var forwardTask = _store.LoadSnapshotAsync(forwardOwner, cancellationToken);
        var incomingForwardTask = _store.LoadIncomingForwardObservationsAsync(queriedMod, cancellationToken);
        var dismissalsTask = _store.LoadDismissalsForModAsync(queriedMod, cancellationToken);
        await Task.WhenAll(forwardTask, incomingForwardTask, dismissalsTask).ConfigureAwait(false);
        var forward = await forwardTask.ConfigureAwait(false);
        var incomingForwardEdges = await incomingForwardTask.ConfigureAwait(false);
        var dismissals = await dismissalsTask.ConfigureAwait(false);

        var forwardPrepared = Prepare(forward?.Snapshot?.Edges, forward, isForward: true, dismissals, queriedMod);
        var reversePrepared = Prepare(incomingForwardEdges, record: null, isForward: false, dismissals, queriedMod);
        var identities = forwardPrepared.Relations
            .Concat(reversePrepared.Relations)
            .Where(item => item.Identity is not null)
            .Select(item => item.Identity!.Value)
            .Distinct()
            .ToArray();
        var states = await _availability.ResolveManyAsync(
            identities,
            cancellationToken).ConfigureAwait(false);

        bool isAmbiguous;
        lock (_ambiguousMods) { isAmbiguous = _ambiguousMods.Contains(queriedMod); }

        var relations = new NexusRequirementRelations(
            Project(forwardPrepared, states, isForward: true, isSourceAmbiguous: isAmbiguous),
            Project(reversePrepared, states, isForward: false));

        return relations;
    }

    public Task DismissRelationAsync(
        NexusRequirementCanonicalKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _store.DismissRelationAsync(key, _utcNow(), cancellationToken);
    }

    public Task<bool> AddToDownloadsAsync(
        NexusModIdentity targetMod,
        string displayName,
        string? safeUrl = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var downloads = _downloads?.Invoke();
        if (downloads is not null)
        {
            return downloads.AddUnresolvedNexusEntryAsync(
                targetMod,
                safeUrl,
                fallbackName: displayName,
                cancellationToken);
        }
        return Task.FromResult(false);
    }

    private async Task<NexusRequirementSyncResult> RefreshAndReleaseAsync(
        NexusModIdentity queriedMod,
        InFlightRefresh operation)
    {
        try
        {
            return await RefreshCoreAsync(
                queriedMod,
                operation.Cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            var dispose = false;
            lock (_inFlightGate)
            {
                operation.Completed = true;
                if (_inFlight.TryGetValue(queriedMod, out var current) &&
                    ReferenceEquals(current, operation))
                {
                    _inFlight.Remove(queriedMod);
                }
                dispose = operation.WaiterCount == 0;
            }
            if (dispose)
                DisposeCancellation(operation);
        }
    }

    private async Task<NexusRequirementSyncResult> RefreshCoreAsync(
        NexusModIdentity queriedMod,
        CancellationToken cancellationToken)
    {
        await EnsureStoreReadyAsync(queriedMod, cancellationToken)
            .ConfigureAwait(false);
        var attemptedAt = _utcNow();
        var forwardOwner = new NexusRequirementSnapshotOwner(
            queriedMod,
            NexusRequirementTraversal.ForwardRequirements);
        var forward = await RefreshTraversalAsync(
            forwardOwner,
            attemptedAt,
            cancellationToken).ConfigureAwait(false);
        var reverseOwner = new NexusRequirementSnapshotOwner(
            queriedMod,
            NexusRequirementTraversal.ReverseRequiredBy);
        var reverse = new NexusRequirementTraversalSyncResult(
            reverseOwner,
            NexusRequirementTraversalOutcome.Complete,
            Failure: null,
            FreshCanonicalKeys: []);
        var outcome = forward.Outcome == NexusRequirementTraversalOutcome.Complete
            ? NexusRequirementSyncOutcome.Success
            : NexusRequirementSyncOutcome.Failure;
        return new(outcome, forward, reverse);
    }

    private async Task EnsureStoreReadyAsync(
        NexusModIdentity queriedMod,
        CancellationToken cancellationToken)
    {
        Task ready;
        lock (_storeReadyGate)
        {
            _storeReady ??= _store.LoadSnapshotAsync(
                new(queriedMod, NexusRequirementTraversal.ForwardRequirements),
                CancellationToken.None);
            ready = _storeReady;
        }
        await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<NexusRequirementTraversalSyncResult> RefreshTraversalAsync(
        NexusRequirementSnapshotOwner owner,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken)
    {
        var query = owner.Traversal == NexusRequirementTraversal.ForwardRequirements
            ? await _client.GetRequirementsAsync(owner.QueriedMod, cancellationToken)
                .ConfigureAwait(false)
            : await _client.GetModsRequiringAsync(owner.QueriedMod, cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (owner.Traversal == NexusRequirementTraversal.ForwardRequirements &&
            query.LegacyModRequirementsEnabled == false &&
            _modernClient != null)
        {
            var modernResult = await _modernClient.GetModernRequirementsAsync(owner.QueriedMod, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (modernResult.Outcome == NexusModernRequirementOutcome.Complete && modernResult.Snapshot != null)
            {
                var enrichedSnapshot = await EnrichAdultContentAsync(
                    modernResult.Snapshot,
                    cancellationToken).ConfigureAwait(false);
                lock (_ambiguousMods) { _ambiguousMods.Remove(owner.QueriedMod); }
                await _store.SaveCompleteSnapshotAsync(enrichedSnapshot, attemptedAt, cancellationToken).ConfigureAwait(false);
                var freshKeys = enrichedSnapshot.Edges
                    .Where(edge => edge.CanonicalKey.HasValue)
                    .Select(edge => edge.CanonicalKey!.Value)
                    .Distinct()
                    .ToArray();
                return new(owner, NexusRequirementTraversalOutcome.Complete, null, freshKeys, IsSourceAmbiguous: false);
            }

            if (modernResult.Outcome == NexusModernRequirementOutcome.AmbiguousSource)
            {
                lock (_ambiguousMods) { _ambiguousMods.Add(owner.QueriedMod); }
                return new(owner, NexusRequirementTraversalOutcome.Complete, null, [], IsSourceAmbiguous: true);
            }

            lock (_ambiguousMods) { _ambiguousMods.Remove(owner.QueriedMod); }
            var modFailure = modernResult.Failure ?? new NexusRequirementFailure(NexusRequirementFailureKind.Transport, "Modern v3 requirement lookup failed.");
            await _store.RecordFailedAttemptAsync(owner, modFailure, attemptedAt, cancellationToken).ConfigureAwait(false);
            return new(owner, NexusRequirementTraversalOutcome.Failed, modFailure, [], IsSourceAmbiguous: false);
        }

        if (query.IsComplete && query.Snapshot?.Owner == owner)
        {
            var enrichedSnapshot = await EnrichAdultContentAsync(
                query.Snapshot,
                cancellationToken).ConfigureAwait(false);
            lock (_ambiguousMods) { _ambiguousMods.Remove(owner.QueriedMod); }
            await _store.SaveCompleteSnapshotAsync(
                enrichedSnapshot,
                attemptedAt,
                cancellationToken).ConfigureAwait(false);
            var freshKeys = enrichedSnapshot.Edges
                .Where(edge => edge.CanonicalKey.HasValue)
                .Select(edge => edge.CanonicalKey!.Value)
                .Distinct()
                .ToArray();
            return new(owner, NexusRequirementTraversalOutcome.Complete, null, freshKeys);
        }

        lock (_ambiguousMods) { _ambiguousMods.Remove(owner.QueriedMod); }
        var failure = query.Failure ?? new NexusRequirementFailure(
            NexusRequirementFailureKind.MalformedIdentity,
            query.IsComplete
                ? "Nexus returned a complete snapshot for the wrong owner."
                : "Nexus returned neither a complete snapshot nor a failure.");
        if (failure.Kind == NexusRequirementFailureKind.Cancelled &&
            cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        await _store.RecordFailedAttemptAsync(
            owner,
            failure,
            attemptedAt,
            cancellationToken).ConfigureAwait(false);
        return new(owner, NexusRequirementTraversalOutcome.Failed, failure, []);
    }

    private async Task<NexusRequirementSyncResult> AwaitOperationAsync(
        InFlightRefresh operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            var cancel = false;
            var dispose = false;
            lock (_inFlightGate)
            {
                operation.WaiterCount--;
                if (operation.WaiterCount == 0)
                {
                    cancel = !operation.Completed;
                    dispose = operation.Completed;
                }
            }
            if (cancel)
                CancelOperation(operation);
            if (dispose)
                DisposeCancellation(operation);
        }
    }

    private static void CancelOperation(InFlightRefresh operation)
    {
        lock (operation.CancellationGate)
        {
            if (!operation.CancellationDisposed)
                operation.Cancellation.Cancel();
        }
    }

    private static void DisposeCancellation(InFlightRefresh operation)
    {
        lock (operation.CancellationGate)
        {
            if (operation.CancellationDisposed)
                return;
            operation.Cancellation.Dispose();
            operation.CancellationDisposed = true;
        }
    }

    private static PreparedProjection Prepare(
        IReadOnlyList<NexusModRequirementEdge>? edges,
        NexusRequirementSnapshotRecord? record,
        bool isForward,
        IReadOnlySet<NexusRequirementCanonicalKey> dismissals,
        NexusModIdentity queriedMod)
    {
        var relations = new List<PreparedRelation>();
        var filtered = 0;
        var seenIdentities = new HashSet<NexusModIdentity>();
        foreach (var edge in edges ?? [])
        {
            if (edge.CanonicalKey is { } key && dismissals.Contains(key))
            {
                continue;
            }

            NexusModIdentity? identity = isForward
                ? NexusGameIdentityBridge.GetEffectiveTargetIdentity(edge.Target)
                : edge.Source;

            if (isForward && identity is { } effTarget &&
                dismissals.Contains(new NexusRequirementCanonicalKey(edge.Source, NexusRequirementTargetKind.NexusMod, effTarget)))
            {
                continue;
            }

            if (identity is { } targetOrSourceId)
            {
                if (isForward && targetOrSourceId == edge.Source)
                {
                    filtered++;
                    continue;
                }

                if (!isForward && targetOrSourceId == queriedMod)
                {
                    filtered++;
                    continue;
                }

                if (!isForward && !seenIdentities.Add(targetOrSourceId))
                {
                    continue;
                }
            }

            relations.Add(new(edge, identity));
        }
        return new(record, relations, filtered);
    }

    private NexusRequirementTraversalProjection Project(
        PreparedProjection prepared,
        IReadOnlyDictionary<NexusModIdentity, NexusRequirementLocalState> states,
        bool isForward,
        bool isSourceAmbiguous = false)
    {
        var relations = new List<NexusRequirementProjectedEdge>();
        foreach (var item in prepared.Relations)
        {
            if (item.Identity is { } identity && states.TryGetValue(identity, out var state))
            {
                if (!isForward &&
                    state.Availability is not (NexusRequirementLocalAvailability.Installed or
                                              NexusRequirementLocalAvailability.InLibrary or
                                              NexusRequirementLocalAvailability.InDownloads))
                {
                    continue;
                }

                relations.Add(new(
                    item.Edge,
                    state,
                    _adultContentPolicy.Evaluate(item.Edge, isForward)));
            }
            else if (isForward)
            {
                relations.Add(new(
                    item.Edge,
                    new(NexusRequirementLocalAvailability.External, null),
                    _adultContentPolicy.Evaluate(item.Edge, isForward)));
            }
        }
        return new(prepared.State, relations, prepared.FilteredSelfRelationCount, isSourceAmbiguous);
    }

    private async Task<NexusRequirementSnapshot> EnrichAdultContentAsync(
        NexusRequirementSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var identities = snapshot.Edges
            .SelectMany(edge =>
            {
                var target = NexusGameIdentityBridge.GetEffectiveTargetIdentity(
                    edge.Target);
                return target is { } targetIdentity
                    ? new[] { edge.Source, targetIdentity }
                    : new[] { edge.Source };
            })
            .Distinct()
            .ToArray();
        IReadOnlyDictionary<NexusModIdentity, NexusAdultContentClassification>
            classifications =
                new Dictionary<NexusModIdentity, NexusAdultContentClassification>();

        if (_contentMetadataClient is not null && identities.Length > 0)
        {
            try
            {
                classifications = await _contentMetadataClient
                    .GetAdultContentClassificationsAsync(
                        identities,
                        cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Edge discovery is authoritative. Metadata failure stays Unknown.
            }
        }

        var enriched = snapshot.Edges.Select(edge =>
        {
            classifications.TryGetValue(
                edge.Source,
                out var sourceClassification);
            var targetClassification =
                NexusAdultContentClassification.Unknown;
            var targetIdentity = NexusGameIdentityBridge
                .GetEffectiveTargetIdentity(edge.Target);
            if (targetIdentity is { } identity)
            {
                classifications.TryGetValue(
                    identity,
                    out targetClassification);
            }
            return edge with
            {
                SourceAdultContent = sourceClassification,
                TargetAdultContent = targetClassification
            };
        });
        return new NexusRequirementSnapshot(snapshot.Owner, enriched);
    }

    private sealed class InFlightRefresh
    {
        public object CancellationGate { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<NexusRequirementSyncResult> Task { get; set; } = null!;
        public int WaiterCount { get; set; }
        public bool Completed { get; set; }
        public bool CancellationDisposed { get; set; }
    }

    private sealed record PreparedRelation(
        NexusModRequirementEdge Edge,
        NexusModIdentity? Identity);

    private sealed record PreparedProjection(
        NexusRequirementSnapshotRecord? State,
        IReadOnlyList<PreparedRelation> Relations,
        int FilteredSelfRelationCount);
}
