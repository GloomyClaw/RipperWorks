using System.Security.Cryptography;
using System.Text;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed class VersionSwitchPreflight(
    OrganizerRepository repository,
    ModInstallationService installer,
    ModRemovalService remover)
{
    public async Task<ModArchiveSwitchPlan> PrepareAsync(
        OrganizerPackageRecord requestedSource,
        OrganizerPackageRecord requestedTarget,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        var requestedOperation = await repository.GetArchiveOperationAsync(
            requestedSource.Package.PackageId,
            requestedTarget.Package.PackageId,
            cancellationToken);
        var requestedPairError = ValidatePair(
            requestedSource,
            requestedTarget,
            requestedOperation);
        if (requestedPairError is not null)
        {
            return Error(
                requestedSource,
                requestedTarget,
                requestedPairError,
                requestedOperation);
        }

        var all = await repository.LoadPackagesAsync(false, cancellationToken);
        var source = Find(all, requestedSource.Package.PackageId);
        var target = Find(all, requestedTarget.Package.PackageId);
        if (source is null)
            return Error(requestedSource, requestedTarget, "ArchiveSwitchSourceMissing");
        if (target is null)
            return Error(source, requestedTarget, "ArchiveSwitchTargetMissing");

        var operation = await repository.GetArchiveOperationAsync(
            source.Package.PackageId,
            target.Package.PackageId,
            cancellationToken);
        var pairError = ValidatePair(source, target, operation);
        if (pairError is not null)
            return Error(source, target, pairError, operation);

        var pairRelations = await repository.LoadPackageRelationsAsync(
            source.Package.PackageId,
            cancellationToken);
        if (pairRelations.Any(value =>
                value.Relation.IsConfirmed &&
                value.Relation.RelationType == PackageRelationType.AddOnOf &&
                IsPair(value.Relation, source.Package.PackageId,
                    target.Package.PackageId)))
        {
            return Error(
                source,
                target,
                "ArchiveSwitchExplicitComponentRelation",
                operation);
        }

        var removalPlan = await remover.BuildPrimitivePreflightPlanAsync(
            source,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles: authorizedModifiedFiles);
        if (!removalPlan.CanRemove)
        {
            return Error(
                source,
                target,
                removalPlan.ErrorCode ?? "ArchiveSwitchRemovalBlocked",
                operation,
                removalPlan.ErrorDetail,
                problemPaths: removalPlan.ProblemPaths,
                eligiblePaths: removalPlan.EligibleModifiedPaths,
                ineligiblePaths: removalPlan.IneligibleProblemPaths);
        }

        var targetPlan = await installer.BuildVersionSwitchPreflightPlanAsync(
            target,
            source.Package.PackageId,
            profile,
            libraryRoot,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(targetPlan.ErrorCode))
        {
            return Error(
                source,
                target,
                targetPlan.ErrorCode,
                operation,
                targetPlan.ErrorDetail);
        }

        var relations = await BatchRelationGraph.LoadAsync(
            repository,
            all.Select(value => value.Package.PackageId),
            cancellationToken);
        var installedIds = all.Where(value => value.Package.InstallationState ==
                PackageInstallationState.Installed)
            .Select(value => value.Package.PackageId)
            .ToHashSet();
        var dependentIds = relations.Where(value =>
                value.MatchesParent(source.Package.PackageId))
            .SelectMany(value => value.ChildPackageIds)
            .Where(id => id != source.Package.PackageId &&
                installedIds.Contains(id))
            .Distinct()
            .ToArray();
        if (dependentIds.Length > 0)
        {
            return Error(
                source,
                target,
                "ArchiveSwitchInstalledDependent",
                operation,
                Detail(all, dependentIds),
                dependentIds);
        }

        var projectedInstalled = installedIds
            .Where(id => id != source.Package.PackageId)
            .ToHashSet();
        var missingParents = relations.Where(value =>
                value.MatchesChild(target.Package.PackageId) &&
                !value.ParentPackageIds.Any(projectedInstalled.Contains))
            .SelectMany(value => value.ParentPackageIds)
            .Distinct()
            .ToArray();
        if (missingParents.Length > 0)
        {
            return Error(
                source,
                target,
                "RequiredParentNotInstalled",
                operation,
                Detail(all, missingParents),
                missingParents);
        }

        var projection = await ProjectAsync(
            source,
            target,
            operation,
            targetPlan,
            cancellationToken);
        var errorCode = projection.UpperLayerConflict
            ? "ArchiveSwitchUpperLayerConflict"
            : projection.ConflictCount > 0
                ? "ArchiveSwitchConflict"
                : projection.BlockedCount > 0 || targetPlan.Entries.Count == 0
                    ? "ArchiveSwitchBlocked"
                    : null;
        var token = await TokenAsync(
            source,
            target,
            profile,
            libraryRoot,
            operation,
            removalPlan,
            targetPlan,
            projection.Entries,
            relations,
            cancellationToken);
        return new()
        {
            InstalledArchiveId = source.Package.PackageId,
            TargetArchiveId = target.Package.PackageId,
            FilesToAdd = projection.Entries.Count(value =>
                value.Action == ArchiveSwitchPlanAction.Add),
            FilesToReplace = projection.Entries.Count(value =>
                value.Action is ArchiveSwitchPlanAction.ReplaceVersion or
                    ArchiveSwitchPlanAction.Reinstall),
            ObsoleteFiles = projection.ObsoleteCount,
            LowerLayersToRestore = projection.Entries.Count(value =>
                value.Action == ArchiveSwitchPlanAction.RestoreLowerLayer),
            ConflictCount = projection.ConflictCount,
            BlockedCount = projection.BlockedCount,
            Operation = operation,
            Entries = projection.Entries,
            BlockingPackageIds = projection.BlockingPackageIds,
            ValidationToken = token,
            ErrorCode = errorCode,
            EligibleModifiedPaths = removalPlan.EligibleModifiedPaths
        };
    }

    private async Task<ProjectedPlan> ProjectAsync(
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        ArchiveVersionOperation operation,
        InstallPlan targetPlan,
        CancellationToken cancellationToken)
    {
        var sourceFiles = await repository.LoadInstalledFilesAsync(
            source.Package.PackageId,
            cancellationToken);
        var sourcePaths = sourceFiles.Select(value => value.RelativeGamePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetEntries = targetPlan.Entries
            .GroupBy(value => value.RelativeGamePath,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value.Key, value => value.First(),
                StringComparer.OrdinalIgnoreCase);
        var targetPaths = targetEntries.Keys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var entries = new List<ArchiveSwitchPlanEntry>();
        var blockingIds = new HashSet<PackageId>();
        var conflicts = 0;
        var blocked = 0;
        var upperLayerConflict = false;

        foreach (var path in targetPaths.OrderBy(value => value,
                     StringComparer.OrdinalIgnoreCase))
        {
            var installEntry = targetEntries[path];
            if (sourcePaths.Contains(path))
            {
                var layers = await repository.LoadManagedPathLayersAsync(
                    path,
                    cancellationToken);
                var sourceLayer = layers.FirstOrDefault(value =>
                    value.PackageId == source.Package.PackageId);
                var topLayer = layers.LastOrDefault();
                if (sourceLayer is null || topLayer is null)
                {
                    blocked++;
                    entries.Add(new(path, ArchiveSwitchPlanAction.Blocked));
                }
                else if (topLayer.PackageId != source.Package.PackageId)
                {
                    conflicts++;
                    upperLayerConflict = true;
                    blockingIds.Add(topLayer.PackageId);
                    entries.Add(new(
                        path,
                        ArchiveSwitchPlanAction.Conflict,
                        installEntry.OwnerDisplayName));
                }
                else
                {
                    entries.Add(new(
                        path,
                        operation == ArchiveVersionOperation.Reinstall
                            ? ArchiveSwitchPlanAction.Reinstall
                            : ArchiveSwitchPlanAction.ReplaceVersion));
                }
                continue;
            }

            switch (installEntry.Action)
            {
                case InstallPlanAction.Conflict:
                case InstallPlanAction.AlreadyInstalled:
                    conflicts++;
                    if (installEntry.OwnerPackageId is { } ownerId)
                        blockingIds.Add(ownerId);
                    entries.Add(new(
                        path,
                        ArchiveSwitchPlanAction.Conflict,
                        installEntry.OwnerDisplayName));
                    break;
                case InstallPlanAction.Blocked:
                    blocked++;
                    entries.Add(new(path, ArchiveSwitchPlanAction.Blocked));
                    break;
                default:
                    entries.Add(new(path, ArchiveSwitchPlanAction.Add));
                    break;
            }
        }

        foreach (var path in sourcePaths.Except(targetPaths,
                     StringComparer.OrdinalIgnoreCase)
                 .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var layers = await repository.LoadManagedPathLayersAsync(
                path,
                cancellationToken);
            var sourceLayer = layers.FirstOrDefault(value =>
                value.PackageId == source.Package.PackageId);
            if (sourceLayer is null)
            {
                blocked++;
                entries.Add(new(path, ArchiveSwitchPlanAction.Blocked));
            }
            else
            {
                entries.Add(new(
                    path,
                    layers.Count > 1
                        ? ArchiveSwitchPlanAction.RestoreLowerLayer
                        : ArchiveSwitchPlanAction.RemoveObsolete));
            }
        }

        return new(
            entries,
            blockingIds.OrderBy(value => value.Value, StringComparer.Ordinal)
                .ToArray(),
            conflicts,
            blocked,
            sourcePaths.Except(targetPaths, StringComparer.OrdinalIgnoreCase)
                .Count(),
            upperLayerConflict);
    }

    private async Task<string> TokenAsync(
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        GameProfileRecord? profile,
        string? libraryRoot,
        ArchiveVersionOperation operation,
        ModRemovalPlan removalPlan,
        InstallPlan targetPlan,
        IReadOnlyList<ArchiveSwitchPlanEntry> projected,
        IReadOnlyList<BatchDependencyRelation> relations,
        CancellationToken cancellationToken)
    {
        var value = new StringBuilder()
            .Append(profile?.GameRoot).Append('|')
            .Append(profile?.ValidationState).Append('|')
            .Append(libraryRoot).Append('|')
            .Append(operation).AppendLine();
        AppendPackage(value, source);
        AppendPackage(value, target);
        value.Append(removalPlan.FilesToDelete).Append('|')
            .Append(removalPlan.FilesToRestore).Append('|')
            .Append(removalPlan.LayersToDetach).AppendLine();
        value.Append(targetPlan.ExpectedArchiveSha256).Append('|')
            .Append(targetPlan.ExpectedAnalyzerVersion).Append('|')
            .Append(targetPlan.ExpectedPolicyVersion).Append('|')
            .Append(targetPlan.ExpectedSelectedRoot).AppendLine();
        foreach (var relation in relations.OrderBy(item => item.RelationId))
        {
            value.Append(relation.RelationId).Append('|')
                .Append(relation.RelationType).Append('|')
                .AppendJoin(',', relation.ChildPackageIds.Select(id => id.Value)
                    .OrderBy(id => id, StringComparer.Ordinal))
                .Append('>')
                .AppendJoin(',', relation.ParentPackageIds.Select(id => id.Value)
                    .OrderBy(id => id, StringComparer.Ordinal))
                .AppendLine();
        }
        foreach (var entry in projected.OrderBy(item => item.RelativeGamePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            value.Append(entry.RelativeGamePath).Append('|')
                .Append(entry.Action).Append('|')
                .Append(entry.OwnerDisplayName).AppendLine();
            foreach (var layer in await repository.LoadManagedPathLayersAsync(
                         entry.RelativeGamePath,
                         cancellationToken))
            {
                value.Append(layer.LayerOrder).Append('|')
                    .Append(layer.PackageId.Value).Append('|')
                    .Append(layer.ContentHash).AppendLine();
            }
        }
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value.ToString())));
    }

    private static void AppendPackage(
        StringBuilder value,
        OrganizerPackageRecord package) => value
        .Append(package.Package.PackageId.Value).Append('|')
        .Append(package.Package.LibraryModId?.Value).Append('|')
        .Append(package.Package.ArchiveFamilyKey).Append('|')
        .Append(package.Package.InstallationState).Append('|')
        .Append(package.IsPresent).Append('|')
        .Append(package.Package.Sha256).Append('|')
        .Append(package.Analysis?.State).Append('|')
        .Append(package.Analysis?.Fingerprint).Append('|')
        .Append(package.Analysis?.SelectedRoot).AppendLine();

    private static OrganizerPackageRecord? Find(
        IReadOnlyList<OrganizerPackageRecord> all,
        PackageId id) => all.FirstOrDefault(value =>
        value.Package.PackageId == id);

    private static bool IsPair(
        PackageRelationRecord relation,
        PackageId first,
        PackageId second) =>
        relation.FromPackageId == first && relation.ToPackageId == second ||
        relation.FromPackageId == second && relation.ToPackageId == first;

    private static string? ValidatePair(
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        ArchiveVersionOperation operation)
    {
        if (source.Package.InstallationState !=
            PackageInstallationState.Installed)
            return "ArchiveSwitchSourceNotInstalled";
        if (source.Package.LibraryModId is null ||
            source.Package.LibraryModId != target.Package.LibraryModId)
            return "ArchiveSwitchDifferentMod";
        if (string.IsNullOrWhiteSpace(source.Package.ArchiveFamilyKey) ||
            !string.Equals(
                source.Package.ArchiveFamilyKey,
                target.Package.ArchiveFamilyKey,
                StringComparison.OrdinalIgnoreCase))
            return "ArchiveSwitchDifferentFamily";
        return operation == ArchiveVersionOperation.Install
            ? "ArchiveSwitchUnconfirmedFamily"
            : null;
    }

    private static string Detail(
        IReadOnlyList<OrganizerPackageRecord> all,
        IReadOnlyList<PackageId> ids) => string.Join(
        ", ",
        ids.Select(id => Find(all, id)?.EffectiveDisplayName ?? id.Value));

    private static ModArchiveSwitchPlan Error(
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        string code,
        ArchiveVersionOperation operation = ArchiveVersionOperation.Install,
        string? detail = null,
        IReadOnlyList<PackageId>? blockingIds = null,
        IReadOnlyList<string>? problemPaths = null,
        IReadOnlyList<string>? eligiblePaths = null,
        IReadOnlyList<string>? ineligiblePaths = null) => new()
        {
            InstalledArchiveId = source.Package.PackageId,
            TargetArchiveId = target.Package.PackageId,
            Operation = operation,
            ErrorCode = code,
            ErrorDetail = detail,
            BlockingPackageIds = blockingIds ?? [],
            ProblemPaths = problemPaths ?? [],
            EligibleModifiedPaths = eligiblePaths ?? [],
            IneligibleProblemPaths = ineligiblePaths ?? []
        };

    private sealed record ProjectedPlan(
        IReadOnlyList<ArchiveSwitchPlanEntry> Entries,
        IReadOnlyList<PackageId> BlockingPackageIds,
        int ConflictCount,
        int BlockedCount,
        int ObsoleteCount,
        bool UpperLayerConflict);
}
