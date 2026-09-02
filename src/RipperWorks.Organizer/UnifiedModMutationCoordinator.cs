using System.Collections.Concurrent;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class UnifiedModMutationCoordinator :
    IBatchInstallCoordinator,
    IBatchRemovalCoordinator,
    IApplicationMutationRoute
{
    private readonly OrganizerRepository _repository;
    private readonly ModInstallationService _installer;
    private readonly ModRemovalService _remover;
    private readonly PackageRelationService _relations;
    private readonly ModMutationPreflight _preflight;
    private readonly Action<string, string?>? _recordEvent;
    private readonly VersionSwitchMutationCoordinator? _versionSwitch;
    private readonly RecoveryPlanner _recoveryPlanner;
    private readonly RecoveryExecutor _recoveryExecutor;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly ConcurrentDictionary<PackageId, BatchInstallPlan>
        _singleInstallPlans = new();
    private readonly ConcurrentDictionary<PackageId, BatchRemovalPlan>
        _singleRemovalPlans = new();

    public UnifiedModMutationCoordinator(
        ModInstallationService installer,
        ModRemovalService remover,
        PackageRelationService relations,
        OrganizerRepository repository,
        Action<string, string?>? recordEvent = null,
        ModArchiveSwitchService? archiveSwitch = null)
        : this(
            installer,
            remover,
            relations,
            repository,
            recordEvent,
            archiveSwitch,
            attachPrimitiveRoutes: true)
    {
    }

    private UnifiedModMutationCoordinator(
        ModInstallationService installer,
        ModRemovalService remover,
        PackageRelationService relations,
        OrganizerRepository repository,
        Action<string, string?>? recordEvent,
        ModArchiveSwitchService? archiveSwitch,
        bool attachPrimitiveRoutes)
    {
        _installer = installer;
        _remover = remover;
        _relations = relations;
        _repository = repository;
        _recordEvent = recordEvent;
        _preflight = new(repository, installer, remover);
        _recoveryPlanner = new(
            repository,
            installer.RecoveryContentStore,
            installer.RecoveryStagingRoot,
            installer.RecoveryProfileService);
        _recoveryExecutor = new(
            repository,
            installer.RecoveryContentStore,
            installer.RecoveryStagingRoot,
            installer,
            remover,
            installer.RecoveryProfileService);
        if (attachPrimitiveRoutes)
        {
            _installer.ApplicationMutationRoute = this;
            _remover.ApplicationMutationRoute = this;
            _relations.ApplicationMutationRoute = this;
        }
        if (archiveSwitch is not null)
        {
            _versionSwitch = new(
                repository,
                installer,
                remover,
                archiveSwitch);
            archiveSwitch.ApplicationMutationRoute = this;
        }
    }

    internal static UnifiedModMutationCoordinator
        CreateVersionSwitchCompatibilityRoute(
            ModInstallationService installer,
            ModRemovalService remover,
            PackageRelationService relations,
            OrganizerRepository repository,
            ModArchiveSwitchService archiveSwitch) =>
        new(
            installer,
            remover,
            relations,
            repository,
            recordEvent: null,
            archiveSwitch,
            attachPrimitiveRoutes: false);

    public event EventHandler<BatchPackageStateChanged>? PackageStateChanged;

    public Task<BatchInstallPlan> PrepareAsync(
        IReadOnlyList<BatchPackageCandidate> candidates,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default) =>
        _preflight.PrepareInstallAsync(
            candidates,
            profile,
            libraryRoot,
            cancellationToken);

    public async Task<BatchOperationResult> ExecuteAsync(
        BatchInstallPlan plan,
        GameProfileRecord? profile,
        string? libraryRoot,
        IProgress<BatchOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.CanExecute)
            return Blocked(plan, "PreflightBlocked");
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var refreshed = await _preflight.PrepareInstallAsync(
                plan.Items.Select(item => item.Candidate).ToArray(),
                profile,
                libraryRoot,
                cancellationToken);
            if (!refreshed.CanExecute ||
                !string.Equals(
                    refreshed.ValidationToken,
                    plan.ValidationToken,
                    StringComparison.Ordinal))
            {
                return Blocked(plan, InvalidationReason(refreshed.Items));
            }
            return await ExecuteInstallItemsAsync(
                refreshed,
                profile,
                libraryRoot,
                progress,
                cancellationToken);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public async Task<IReadOnlyList<BatchPackageCandidate>> ExpandRelatedAsync(
        IReadOnlyList<BatchPackageCandidate> selected,
        IReadOnlyList<BatchPackageCandidate> library,
        CancellationToken cancellationToken = default)
    {
        var current = await LoadCurrentLibraryAsync(library, cancellationToken);
        var byId = current.ToDictionary(Id);
        var result = selected.Where(item => byId.ContainsKey(Id(item)))
            .ToDictionary(Id, item => byId[Id(item)]);
        var relations = await BatchRelationGraph.LoadAsync(
            _repository,
            byId.Keys,
            cancellationToken);
        var changed = true;
        while (changed)
        {
            changed = false;
            var selectedIds = result.Keys.ToHashSet();
            foreach (var relation in relations.Where(relation =>
                         relation.ParentPackageIds.Any(selectedIds.Contains) &&
                         !relation.ParentPackageIds.Any(parentId =>
                             !selectedIds.Contains(parentId) &&
                             byId.TryGetValue(parentId, out var parent) &&
                             parent.Package.Package.InstallationState ==
                                 PackageInstallationState.Installed)))
            {
                foreach (var childId in relation.ChildPackageIds)
                {
                    if (!byId.TryGetValue(childId, out var child) ||
                        child.Package.Package.InstallationState !=
                            PackageInstallationState.Installed ||
                        !result.TryAdd(childId, child))
                    {
                        continue;
                    }
                    changed = true;
                }
            }
        }
        return result.Values.OrderBy(item => item.VisualOrder).ToArray();
    }

    public Task<BatchRemovalPlan> PrepareAsync(
        IReadOnlyList<BatchPackageCandidate> candidates,
        IReadOnlyList<BatchPackageCandidate> library,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default) =>
        _preflight.PrepareRemovalAsync(
            candidates,
            library,
            profile,
            libraryRoot,
            cancellationToken);

    public Task<BatchRemovalPlan> PrepareAsync(
        IReadOnlyList<BatchPackageCandidate> candidates,
        IReadOnlyList<BatchPackageCandidate> library,
        GameProfileRecord? profile,
        string? libraryRoot,
        ModifiedFilesAuthorization? authorizedModifiedFiles,
        CancellationToken cancellationToken = default) =>
        _preflight.PrepareRemovalAsync(
            candidates,
            library,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles);

    public async Task<BatchOperationResult> ExecuteAsync(
        BatchRemovalPlan plan,
        GameProfileRecord? profile,
        string? libraryRoot,
        IProgress<BatchOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.CanExecute)
            return Blocked(plan, "PreflightBlocked");
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var currentLibrary = await LoadCurrentLibraryAsync(
                plan.Items.Select(item => item.Candidate)
                    .Concat(plan.UnselectedRelated.SelectMany(group =>
                        group.Dependents)).ToArray(),
                cancellationToken);
            var refreshed = await _preflight.PrepareRemovalAsync(
                plan.Items.Select(item => item.Candidate).ToArray(),
                currentLibrary,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles: plan.AuthorizedModifiedFiles);
            if (!refreshed.CanExecute ||
                !string.Equals(
                    refreshed.ValidationToken,
                    plan.ValidationToken,
                    StringComparison.Ordinal))
            {
                return Blocked(plan, InvalidationReason(refreshed.Items));
            }
            return await ExecuteRemovalItemsAsync(
                refreshed,
                profile,
                libraryRoot,
                progress,
                cancellationToken);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task<BatchOperationResult> ExecuteInstallItemsAsync(
        BatchInstallPlan plan,
        GameProfileRecord? profile,
        string? libraryRoot,
        IProgress<BatchOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var states = Initial(plan.Items.Select(item => (
            item.Candidate, item.State, item.Reason)));
        var successful = 0;
        var failed = 0;
        for (var index = 0; index < plan.ExecutionOrder.Count; index++)
        {
            var item = plan.ExecutionOrder[index];
            if (cancellationToken.IsCancellationRequested)
            {
                MarkNotStarted(plan.ExecutionOrder.Skip(index)
                    .Select(value => value.Candidate), states);
                break;
            }
            Report(
                progress,
                BatchOperationKind.Install,
                index,
                plan.ExecutionOrder.Count,
                item.Candidate.DisplayName,
                successful,
                failed);
            _recordEvent?.Invoke(
                "EventBatchInstallationStarted",
                item.Candidate.DisplayName);
            var result = await _installer.InstallPrimitiveAsync(
                item.Candidate.Package,
                profile,
                libraryRoot,
                cancellationToken: CancellationToken.None);
            if (!result.Success)
            {
                failed++;
                states[Id(item.Candidate)] = new(
                    Id(item.Candidate), item.Candidate.DisplayName,
                    BatchItemState.Failed,
                    result.ErrorMessage ?? result.ErrorCode);
                MarkNotStarted(plan.ExecutionOrder.Skip(index + 1)
                    .Select(value => value.Candidate), states);
                Report(
                    progress,
                    BatchOperationKind.Install,
                    index + 1,
                    plan.ExecutionOrder.Count,
                    item.Candidate.DisplayName,
                    successful,
                    failed);
                break;
            }
            successful++;
            Complete(item.Candidate, states, true);
            Report(
                progress,
                BatchOperationKind.Install,
                index + 1,
                plan.ExecutionOrder.Count,
                item.Candidate.DisplayName,
                successful,
                failed);
        }
        return Result(BatchOperationKind.Install, plan.Items.Select(item =>
            states[Id(item.Candidate)]));
    }

    private async Task<BatchOperationResult> ExecuteRemovalItemsAsync(
        BatchRemovalPlan plan,
        GameProfileRecord? profile,
        string? libraryRoot,
        IProgress<BatchOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var states = Initial(plan.Items.Select(item => (
            item.Candidate, item.State, item.Reason)));
        var successful = 0;
        var failed = 0;
        for (var index = 0; index < plan.ExecutionOrder.Count; index++)
        {
            var item = plan.ExecutionOrder[index];
            if (cancellationToken.IsCancellationRequested)
            {
                MarkNotStarted(plan.ExecutionOrder.Skip(index)
                    .Select(value => value.Candidate), states);
                break;
            }
            Report(
                progress,
                BatchOperationKind.Remove,
                index,
                plan.ExecutionOrder.Count,
                item.Candidate.DisplayName,
                successful,
                failed);
            _recordEvent?.Invoke(
                "EventBatchRemovalStarted",
                item.Candidate.DisplayName);
            var result = await _remover.RemovePrimitiveAsync(
                item.Candidate.Package,
                profile,
                libraryRoot,
                CancellationToken.None,
                authorizedModifiedFiles: plan.AuthorizedModifiedFiles);
            if (!result.Success)
            {
                failed++;
                states[Id(item.Candidate)] = new(
                    Id(item.Candidate), item.Candidate.DisplayName,
                    BatchItemState.Failed,
                    result.ErrorMessage ?? result.ErrorCode);
                MarkNotStarted(plan.ExecutionOrder.Skip(index + 1)
                    .Select(value => value.Candidate), states);
                Report(
                    progress,
                    BatchOperationKind.Remove,
                    index + 1,
                    plan.ExecutionOrder.Count,
                    item.Candidate.DisplayName,
                    successful,
                    failed);
                break;
            }
            successful++;
            Complete(item.Candidate, states, false);
            Report(
                progress,
                BatchOperationKind.Remove,
                index + 1,
                plan.ExecutionOrder.Count,
                item.Candidate.DisplayName,
                successful,
                failed);
        }
        return Result(BatchOperationKind.Remove, plan.Items.Select(item =>
            states[Id(item.Candidate)]));
    }

    private void Complete(
        BatchPackageCandidate candidate,
        IDictionary<PackageId, BatchOperationItemResult> states,
        bool install)
    {
        states[Id(candidate)] = new(
            Id(candidate), candidate.DisplayName, BatchItemState.Completed);
        _recordEvent?.Invoke(
            install
                ? "EventBatchInstallationCompleted"
                : "EventBatchRemovalCompleted",
            candidate.DisplayName);
        PackageStateChanged?.Invoke(this, new(
            Id(candidate),
            install
                ? PackageInstallationState.Installed
                : PackageInstallationState.NotInstalled));
    }

    private async Task<PackageRelationRecord?> FindPairAsync(
        PackageId child,
        PackageId parent,
        CancellationToken cancellationToken) =>
        (await _repository.LoadPackageRelationsAsync(child, cancellationToken))
        .Select(item => item.Relation).FirstOrDefault(item =>
            item.FromPackageId == child && item.ToPackageId == parent);

    private static RelationMutationResult Equivalent(
        PackageRelationRecord existing,
        PackageRelationType requestedType,
        bool requestedConfirmed) =>
        existing.RelationType == requestedType &&
        existing.IsConfirmed == requestedConfirmed
            ? new(true, Relation: existing)
            : new(false, "RelationContradiction");

    private static Dictionary<PackageId, BatchOperationItemResult> Initial(
        IEnumerable<(BatchPackageCandidate Candidate, BatchItemState State,
            string? Reason)> items) => items.ToDictionary(
        item => Id(item.Candidate),
        item => new BatchOperationItemResult(
            Id(item.Candidate), item.Candidate.DisplayName,
            item.State, item.Reason));

    private static void MarkNotStarted(
        IEnumerable<BatchPackageCandidate> items,
        IDictionary<PackageId, BatchOperationItemResult> states)
    {
        foreach (var item in items)
            states[Id(item)] = new(
                Id(item), item.DisplayName, BatchItemState.NotStarted);
    }

    private static BatchOperationResult Blocked(
        BatchInstallPlan plan,
        string reason) => Result(
        BatchOperationKind.Install,
        plan.Items.Select(item => new BatchOperationItemResult(
            Id(item.Candidate), item.Candidate.DisplayName,
            item.CanInstall ? BatchItemState.NotStarted : item.State,
            item.Reason ?? reason)));

    private static BatchOperationResult Blocked(
        BatchRemovalPlan plan,
        string reason) => Result(
        BatchOperationKind.Remove,
        plan.Items.Select(item => new BatchOperationItemResult(
            Id(item.Candidate), item.Candidate.DisplayName,
            item.CanRemove ? BatchItemState.NotStarted : item.State,
            item.Reason ?? reason)));

    private static BatchOperationResult Result(
        BatchOperationKind kind,
        IEnumerable<BatchOperationItemResult> items) => new()
        {
            Kind = kind,
            Items = items.ToArray()
        };

    private static void Report(
        IProgress<BatchOperationProgress>? progress,
        BatchOperationKind kind,
        int completed,
        int total,
        string currentMod,
        int successful,
        int failed) => progress?.Report(new(
        kind,
        completed,
        total,
        currentMod,
        successful,
        failed,
        total - completed));

    private static ModInstallationResult InstallFailure(string code) => new()
    {
        Status = InstallOperationStatus.Blocked,
        InstallationState = PackageInstallationState.NotInstalled,
        ErrorCode = code
    };

    private static ModRemovalResult RemoveFailure(string code) => new()
    {
        Status = InstallOperationStatus.Blocked,
        InstallationState = PackageInstallationState.Installed,
        ErrorCode = code
    };

    private async Task<IReadOnlyList<BatchPackageCandidate>>
        LoadCurrentLibraryAsync(
            IReadOnlyList<BatchPackageCandidate> previous,
            CancellationToken cancellationToken)
    {
        var order = previous.GroupBy(Id).ToDictionary(
            group => group.Key,
            group => group.First().VisualOrder);
        var all = await OperationPerformanceDiagnostics.MeasureDatabaseAsync(
            () => _repository.LoadPackagesAsync(false, cancellationToken));
        return all.Select((package, index) => new BatchPackageCandidate(
            package,
            package.EffectiveDisplayName,
            order.TryGetValue(package.Package.PackageId, out var visualOrder)
                ? visualOrder
                : order.Count + index)).ToArray();
    }

    private static string InvalidationReason(
        IEnumerable<BatchInstallPlanItem> items) =>
        items.Select(item => item.Reason)
            .FirstOrDefault(reason => reason == "SelectedPackageMissing") ??
        "PreflightInvalidated";

    private static string InvalidationReason(
        IEnumerable<BatchRemovalPlanItem> items) =>
        items.Select(item => item.Reason)
            .FirstOrDefault(reason => reason == "SelectedPackageMissing") ??
        "PreflightInvalidated";

    private static PackageId Id(BatchPackageCandidate item) =>
        item.Package.Package.PackageId;
}
