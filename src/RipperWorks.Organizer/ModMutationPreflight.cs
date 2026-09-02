using System.Security.Cryptography;
using System.Text;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed class ModMutationPreflight(
    OrganizerRepository repository,
    ModInstallationService installer,
    ModRemovalService remover)
{
    public async Task<BatchInstallPlan> PrepareInstallAsync(
        IReadOnlyList<BatchPackageCandidate> requested,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        var all = await OperationPerformanceDiagnostics.MeasureDatabaseAsync(
            () => repository.LoadPackagesAsync(false, cancellationToken));
        var candidates = Refresh(requested, all);
        var missing = Missing(requested, all);
        IReadOnlyList<BatchDependencyRelation> relations;
        using (OperationPerformanceDiagnostics.MeasureMetric(
                   "RELATION_LOOKUP",
                   "relation_lookup"))
        {
            relations = await BatchRelationGraph.LoadAsync(
                repository,
                all.Select(item => item.Package.PackageId),
                cancellationToken);
        }
        var cycle = BatchRelationGraph.FindCyclePackages(candidates, relations);
        var items = new List<BatchInstallPlanItem>(candidates.Count);
        var decisions = new Dictionary<(PackageId, PackageId),
            RelationDecisionRequirement>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = candidate.Package;
            if (package.Package.InstallationState ==
                PackageInstallationState.Installed)
            {
                items.Add(Item(candidate, BatchItemState.AlreadyInstalled,
                    reason: "PackageAlreadyInstalled"));
                continue;
            }
            var plan = await installer.BuildPrimitivePreflightPlanAsync(
                package,
                profile,
                libraryRoot,
                cancellationToken);
            foreach (var conflict in plan.Entries.Where(entry =>
                         entry.Action == InstallPlanAction.Conflict &&
                         entry.OwnerPackageId is not null)
                     .GroupBy(entry => entry.OwnerPackageId!.Value))
            {
                var owner = all.FirstOrDefault(value =>
                    value.Package.PackageId == conflict.Key);
                decisions.TryAdd(
                    (package.Package.PackageId, conflict.Key),
                    new(
                        package.Package.PackageId,
                        candidate.DisplayName,
                        conflict.Key,
                        owner?.EffectiveDisplayName ??
                            conflict.First().OwnerDisplayName ?? "—",
                        conflict.Count()));
            }
            var ready = IsReady(plan);
            items.Add(Item(
                candidate,
                ready ? BatchItemState.Ready : Classify(plan),
                plan,
                ready ? null : Reason(plan)));
        }
        items.AddRange(missing.Select(candidate => Item(
            candidate,
            BatchItemState.InstallationBlocked,
            reason: "SelectedPackageMissing")));

        AddProjectedOverlapDecisions(candidates, relations, decisions);
        ApplyDecisionBlocks(items, decisions.Values);
        ApplyMissingParentBlocks(items, all, relations);
        var readyItems = items.Where(item => item.CanInstall).ToArray();
        var order = cycle.Count == 0
            ? BatchRelationGraph.Order(
                readyItems,
                item => item.Candidate,
                relations,
                reverse: false)
            : [];
        return new()
        {
            Items = items,
            ExecutionOrder = order,
            CyclePackages = cycle,
            Decisions = decisions.Values
                .OrderBy(item => FindOrder(candidates, item.ChildPackageId))
                .ThenBy(item => item.ParentDisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ValidationToken = Token(
                BatchOperationKind.Install,
                candidates,
                profile,
                libraryRoot,
                relations)
        };
    }

    public async Task<BatchRemovalPlan> PrepareRemovalAsync(
        IReadOnlyList<BatchPackageCandidate> requested,
        IReadOnlyList<BatchPackageCandidate> requestedLibrary,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        var all = await OperationPerformanceDiagnostics.MeasureDatabaseAsync(
            () => repository.LoadPackagesAsync(false, cancellationToken));
        var candidates = Refresh(requested, all);
        var missing = Missing(requested, all);
        var requestedOrder = requestedLibrary
            .GroupBy(Id)
            .ToDictionary(group => group.Key, group => group.First().VisualOrder);
        var library = all.Select((item, index) => new BatchPackageCandidate(
            item,
            item.EffectiveDisplayName,
            requestedOrder.TryGetValue(item.Package.PackageId, out var order)
                ? order
                : requestedOrder.Count + index)).ToArray();
        var libraryById = library.ToDictionary(Id);
        var selectedIds = candidates.Select(Id).ToHashSet();
        IReadOnlyList<BatchDependencyRelation> relations;
        using (OperationPerformanceDiagnostics.MeasureMetric(
                   "RELATION_LOOKUP",
                   "relation_lookup"))
        {
            relations = await BatchRelationGraph.LoadAsync(
                repository,
                all.Select(item => item.Package.PackageId),
                cancellationToken);
        }
        var cycle = BatchRelationGraph.FindCyclePackages(candidates, relations);
        var related = candidates.Select(owner =>
        {
            var dependents = relations
                .Where(relation => relation.MatchesParent(Id(owner)) &&
                    ParentBecomesUnavailable(
                        relation,
                        selectedIds,
                        libraryById))
                .SelectMany(relation => relation.ChildPackageIds)
                .Where(id => !selectedIds.Contains(id))
                .Distinct()
                .Where(id => libraryById.TryGetValue(id, out var dependent) &&
                    dependent.Package.Package.InstallationState ==
                        PackageInstallationState.Installed)
                .Select(id => libraryById[id])
                .DistinctBy(Id)
                .OrderBy(item => item.VisualOrder)
                .ToArray();
            return new BatchRelatedPackageGroup(owner, dependents);
        }).Where(group => group.Dependents.Count > 0).ToArray();

        var items = new List<BatchRemovalPlanItem>();
        foreach (var candidate in candidates)
        {
            if (candidate.Package.Package.InstallationState !=
                PackageInstallationState.Installed)
            {
                items.Add(new()
                {
                    Candidate = candidate,
                    State = BatchItemState.NotInstalled,
                    Reason = "PackageNotInstalled"
                });
                continue;
            }
            var plan = await remover.BuildPrimitivePreflightPlanAsync(
                candidate.Package,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles: authorizedModifiedFiles);
            items.Add(new()
            {
                Candidate = candidate,
                State = plan.CanRemove
                    ? BatchItemState.Ready
                    : BatchItemState.RemovalBlocked,
                Plan = plan,
                Reason = plan.CanRemove
                    ? null
                    : plan.ErrorDetail ?? plan.ErrorCode,
                RelatedPackages = relations.Where(relation =>
                        relation.MatchesChild(Id(candidate)) ||
                        relation.MatchesParent(Id(candidate)))
                    .SelectMany(relation => relation.MatchesChild(Id(candidate))
                        ? relation.ParentPackageIds
                        : relation.ChildPackageIds)
                    .Where(libraryById.ContainsKey)
                    .Select(id => libraryById[id].DisplayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            });
        }
        items.AddRange(missing.Select(candidate => new BatchRemovalPlanItem
        {
            Candidate = candidate,
            State = BatchItemState.RemovalBlocked,
            Reason = "SelectedPackageMissing"
        }));
        var ready = items.Where(item => item.CanRemove).ToArray();
        return new()
        {
            Items = items,
            ExecutionOrder = cycle.Count == 0
                ? BatchRelationGraph.Order(
                    ready,
                    item => item.Candidate,
                    relations,
                    reverse: true)
                : [],
            UnselectedRelated = related,
            CyclePackages = cycle,
            AuthorizedModifiedFiles = authorizedModifiedFiles,
            ValidationToken = Token(
                BatchOperationKind.Remove,
                candidates,
                profile,
                libraryRoot,
                relations)
        };
    }

    private static IReadOnlyList<BatchPackageCandidate> Refresh(
        IReadOnlyList<BatchPackageCandidate> requested,
        IReadOnlyList<OrganizerPackageRecord> all)
    {
        var byId = all.ToDictionary(item => item.Package.PackageId);
        return requested.GroupBy(Id).Select(group => group.First())
            .OrderBy(item => item.VisualOrder)
            .Where(item => byId.ContainsKey(Id(item)))
            .Select(item => item with { Package = byId[Id(item)] })
            .ToArray();
    }

    private static IReadOnlyList<BatchPackageCandidate> Missing(
        IReadOnlyList<BatchPackageCandidate> requested,
        IReadOnlyList<OrganizerPackageRecord> all)
    {
        var existing = all.Select(item => item.Package.PackageId).ToHashSet();
        return requested.GroupBy(Id).Select(group => group.First())
            .Where(item => !existing.Contains(Id(item)))
            .OrderBy(item => item.VisualOrder)
            .ToArray();
    }

    private static void AddProjectedOverlapDecisions(
        IReadOnlyList<BatchPackageCandidate> candidates,
        IReadOnlyList<BatchDependencyRelation> relations,
        IDictionary<(PackageId, PackageId), RelationDecisionRequirement>
            decisions)
    {
        var byPath = candidates.SelectMany(candidate =>
                InstallPaths(candidate).Select(path => (candidate, path)))
            .GroupBy(value => value.path, StringComparer.OrdinalIgnoreCase);
        foreach (var overlap in byPath.Where(group => group.Count() > 1))
        {
            var values = overlap.Select(value => value.candidate)
                .DistinctBy(Id).OrderBy(value => value.VisualOrder).ToArray();
            for (var index = 0; index < values.Length; index++)
            for (var other = index + 1; other < values.Length; other++)
            {
                var first = values[index];
                var second = values[other];
                if (HasDependency(relations, Id(first), Id(second)) ||
                    HasDependency(relations, Id(second), Id(first)))
                {
                    continue;
                }
                var key = (Id(second), Id(first));
                if (decisions.TryGetValue(key, out var existing))
                    decisions[key] = existing with
                    {
                        OverlappingFileCount =
                            existing.OverlappingFileCount + 1
                    };
                else
                    decisions[key] = new(
                        Id(second), second.DisplayName,
                        Id(first), first.DisplayName, 1);
            }
        }
    }

    private static void ApplyDecisionBlocks(
        IList<BatchInstallPlanItem> items,
        IEnumerable<RelationDecisionRequirement> decisions)
    {
        var blocked = decisions.Select(item => item.ChildPackageId).ToHashSet();
        for (var index = 0; index < items.Count; index++)
        {
            if (!blocked.Contains(Id(items[index].Candidate)))
                continue;
            items[index] = items[index] with
            {
                State = BatchItemState.InstallationBlocked,
                Reason = "RelationDecisionRequired"
            };
        }
    }

    private static void ApplyMissingParentBlocks(
        IList<BatchInstallPlanItem> items,
        IReadOnlyList<OrganizerPackageRecord> all,
        IReadOnlyList<BatchDependencyRelation> relations)
    {
        var selected = items.Select(item => Id(item.Candidate)).ToHashSet();
        var installed = all.Where(item => item.Package.InstallationState ==
                PackageInstallationState.Installed)
            .Select(item => item.Package.PackageId).ToHashSet();
        var missingChildren = relations
            .SelectMany(relation => relation.ChildPackageIds
                .Where(selected.Contains)
                .Where(_ => !relation.ParentPackageIds.Any(parent =>
                    selected.Contains(parent) || installed.Contains(parent))))
            .ToHashSet();
        for (var index = 0; index < items.Count; index++)
        {
            if (!missingChildren.Contains(Id(items[index].Candidate)))
                continue;
            items[index] = items[index] with
            {
                State = BatchItemState.InstallationBlocked,
                Reason = "RequiredParentNotInstalled"
            };
        }
    }

    private static IEnumerable<string> InstallPaths(
        BatchPackageCandidate candidate) =>
        candidate.Package.Analysis?.Entries.Where(entry =>
                entry.IsInstallable &&
                !entry.IsDirectory &&
                !string.IsNullOrWhiteSpace(entry.RelativeInstallPath))
            .Select(entry => entry.RelativeInstallPath!) ?? [];

    private static bool HasDependency(
        IEnumerable<BatchDependencyRelation> relations,
        PackageId child,
        PackageId parent) => relations.Any(relation =>
            relation.MatchesChild(child) && relation.MatchesParent(parent));

    private static bool ParentBecomesUnavailable(
        BatchDependencyRelation relation,
        IReadOnlySet<PackageId> selectedIds,
        IReadOnlyDictionary<PackageId, BatchPackageCandidate> library) =>
        !relation.ParentPackageIds.Any(parentId =>
            !selectedIds.Contains(parentId) &&
            library.TryGetValue(parentId, out var parent) &&
            parent.Package.Package.InstallationState ==
                PackageInstallationState.Installed);

    private static bool IsReady(InstallPlan plan) =>
        string.IsNullOrWhiteSpace(plan.ErrorCode) &&
        plan.Entries.Count > 0 && plan.ConflictCount == 0 &&
        plan.BlockedCount == 0;

    private static BatchItemState Classify(InstallPlan plan) =>
        plan.ErrorCode switch
        {
            "ArchiveMissing" => BatchItemState.ArchiveMissing,
            "AnalysisMissing" or "AnalysisNotReady" =>
                BatchItemState.RequiresAnalysis,
            _ => BatchItemState.InstallationBlocked
        };

    private static string Reason(InstallPlan plan) =>
        plan.ErrorDetail ?? plan.ErrorCode ??
        (plan.ConflictCount > 0 ? "InstallConflict" : "InstallPlanBlocked");

    private static BatchInstallPlanItem Item(
        BatchPackageCandidate candidate,
        BatchItemState state,
        InstallPlan? plan = null,
        string? reason = null) => new()
        {
            Candidate = candidate,
            State = state,
            Plan = plan,
            Reason = reason
        };

    private static int FindOrder(
        IReadOnlyList<BatchPackageCandidate> candidates,
        PackageId id) => candidates.First(item => Id(item) == id).VisualOrder;

    private static string Token(
        BatchOperationKind kind,
        IReadOnlyList<BatchPackageCandidate> candidates,
        GameProfileRecord? profile,
        string? libraryRoot,
        IReadOnlyList<BatchDependencyRelation> relations)
    {
        var value = new StringBuilder().Append(kind).Append('|')
            .Append(profile?.GameRoot).Append('|')
            .Append(profile?.ValidationState).Append('|')
            .Append(libraryRoot).AppendLine();
        foreach (var item in candidates.OrderBy(value => Id(value).Value,
                     StringComparer.Ordinal))
        {
            var package = item.Package;
            value.Append(package.Package.PackageId.Value).Append('|')
                .Append(package.Package.InstallationState).Append('|')
                .Append(package.IsPresent).Append('|')
                .Append(package.Package.Sha256).Append('|')
                .Append(package.Analysis?.State).Append('|')
                .Append(package.Analysis?.SelectedRoot).AppendLine();
        }
        foreach (var relation in relations.OrderBy(item => item.RelationId))
        {
            value.Append(relation.RelationId).Append('|')
                .Append(relation.RelationType).Append('|')
                .AppendJoin(',', relation.ChildPackageIds
                    .Select(id => id.Value)
                    .OrderBy(id => id, StringComparer.Ordinal))
                .Append('>')
                .AppendJoin(',', relation.ParentPackageIds
                    .Select(id => id.Value)
                    .OrderBy(id => id, StringComparer.Ordinal))
                .AppendLine();
        }
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value.ToString())));
    }

    private static PackageId Id(BatchPackageCandidate item) =>
        item.Package.Package.PackageId;
}
