using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed class VersionSwitchRecoveryPlanner
{
    private readonly OrganizerRepository _repository;
    private readonly VersionSwitchRecoveryEvidence _evidence;

    public VersionSwitchRecoveryPlanner(OrganizerRepository repository)
    {
        _repository = repository;
        _evidence = new(repository);
    }

    public async Task<RecoveryPlan> PrepareAsync(
        RecoveryRepositoryEvidence evidence,
        GameProfileRecord profile,
        string? libraryRoot,
        Func<Guid, GameProfileRecord?, string?, CancellationToken,
            Task<RecoveryPlan>> prepareNested,
        CancellationToken cancellationToken)
    {
        var operation = evidence.Operation!;
        if (!VersionSwitchOperationType.TryParse(
                operation.OperationType,
                out var targetId,
                out _))
        {
            return Blocked(operation, "MalformedVersionSwitchEvidence");
        }
        var packages = await _repository.LoadPackagesAsync(
            false, cancellationToken);
        var source = packages.SingleOrDefault(value =>
            value.Package.PackageId == operation.PackageId);
        var target = packages.SingleOrDefault(value =>
            value.Package.PackageId == targetId);
        if (source is null || target is null)
            return Blocked(operation, "PackageIdentityMissing");

        var related = await _repository.LoadRecoveryOperationsSinceAsync(
            [operation.PackageId, targetId], operation.StartedAtUtc,
            operation.OperationId, cancellationToken);
        if (related.Any(value =>
                value.OperationType is not ("Install" or "Remove") ||
                value.Status == InstallOperationStatus.InProgress))
        {
            return Blocked(operation, "AmbiguousNestedRecoveryEvidence");
        }
        var nestedRows = related.Where(value =>
            value.Status == InstallOperationStatus.RecoveryRequired).ToArray();
        if (nestedRows.Length > 1)
            return Blocked(operation, "AmbiguousNestedRecoveryEvidence");
        RecoveryPlan? nestedPlan = null;
        if (nestedRows.Length == 1)
        {
            nestedPlan = await prepareNested(
                nestedRows[0].OperationId,
                profile,
                libraryRoot,
                cancellationToken);
            if (!nestedPlan.IsDeterministic)
            {
                return Blocked(operation, "NestedRecoveryUnsupported:" +
                    nestedPlan.Blockers.FirstOrDefault());
            }
        }

        var sourceRemoves = Select(
            related, operation.PackageId, "Remove");
        var targetInstalls = Select(related, targetId, "Install");
        var sourceInstalls = Select(
            related, operation.PackageId, "Install");
        var targetRemoves = Select(related, targetId, "Remove");
        if (sourceRemoves.Count > 1 ||
            targetInstalls.Count(IsCompleted) > 1 ||
            sourceInstalls.Count(IsCompleted) > 1 ||
            targetRemoves.Count(IsCompleted) > 1)
        {
            return Blocked(operation, "AmbiguousNestedRecoveryEvidence");
        }

        if (operation.PackageId == targetId)
        {
            return await PrepareReinstallAsync(
                evidence, operation, source, related, sourceRemoves,
                sourceInstalls, nestedPlan, profile, libraryRoot,
                cancellationToken);
        }

        if (sourceRemoves.Count == 0)
        {
            return await PrepareBeforeRemovalAsync(
                evidence, operation, source, target, related, nestedPlan,
                profile, libraryRoot, cancellationToken);
        }
        var sourceRemoval = sourceRemoves.Single();
        if (sourceRemoval.Status == InstallOperationStatus.RecoveryRequired)
        {
            return await PrepareSourceRemovalRecoveryAsync(
                evidence, operation, source, target, related,
                sourceRemoval, nestedPlan, profile, libraryRoot,
                cancellationToken);
        }
        if (!IsCompleted(sourceRemoval))
            return Blocked(operation, "UnsupportedVersionSwitchShape");

        return await PrepareAfterRemovalAsync(
            evidence, operation, source, target, related, sourceRemoval,
            targetInstalls, sourceInstalls, targetRemoves, nestedPlan,
            profile, libraryRoot, cancellationToken);
    }

    private async Task<RecoveryPlan> PrepareReinstallAsync(
        RecoveryRepositoryEvidence evidence,
        InstallOperationRecord operation,
        OrganizerPackageRecord package,
        IReadOnlyList<InstallOperationRecord> related,
        IReadOnlyList<InstallOperationRecord> removes,
        IReadOnlyList<InstallOperationRecord> installs,
        RecoveryPlan? nestedPlan,
        GameProfileRecord profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        if (removes.Count == 0)
        {
            if (nestedPlan is not null || related.Count != 0 ||
                operation.CurrentPhase is not (
                    InstallOperationPhase.Created or
                    InstallOperationPhase.Staging) ||
                package.Package.InstallationState !=
                    PackageInstallationState.Installed)
            {
                return Blocked(operation, "UnsupportedVersionSwitchShape");
            }
            var installedEvidence = await _evidence.PrepareInstalledSourceAsync(
                package, package, null, profile, cancellationToken);
            if (installedEvidence.Blocker is not null)
                return Blocked(operation, installedEvidence.Blocker);
            return await BuildPlanAsync(
                evidence, operation, package, package, related, null,
                installedEvidence,
                VersionSwitchRecoveryAction.VerifyOriginalSource,
                sourceAlreadyRestored: true, libraryRoot, cancellationToken);
        }

        var removal = removes.Single();
        if (removal.Status == InstallOperationStatus.RecoveryRequired)
        {
            if (nestedPlan?.OperationId != removal.OperationId ||
                nestedPlan.Kind != RecoveryOperationKind.Remove ||
                related.Any(value => value.OperationId != removal.OperationId))
            {
                return Blocked(operation, "UnsupportedVersionSwitchShape");
            }
            var installedEvidence = await _evidence.PrepareInstalledSourceAsync(
                package, package, nestedPlan, profile, cancellationToken);
            if (installedEvidence.Blocker is not null)
                return Blocked(operation, installedEvidence.Blocker);
            return await BuildPlanAsync(
                evidence, operation, package, package, related, nestedPlan,
                installedEvidence,
                VersionSwitchRecoveryAction.RecoverSourceRemoval,
                sourceAlreadyRestored: true, libraryRoot, cancellationToken);
        }
        if (!IsCompleted(removal))
            return Blocked(operation, "UnsupportedVersionSwitchShape");
        if (nestedPlan is not null &&
            (nestedPlan.Kind != RecoveryOperationKind.Install ||
             nestedPlan.PackageIds.Single() != package.Package.PackageId))
        {
            return Blocked(operation, "AmbiguousNestedRecoveryEvidence");
        }
        var completedInstall = installs.SingleOrDefault(IsCompleted);
        var alreadyRestored = package.Package.InstallationState ==
            PackageInstallationState.Installed;
        if (alreadyRestored != (completedInstall is not null))
            return Blocked(operation, "UnsupportedVersionSwitchShape");
        if (alreadyRestored)
        {
            if (nestedPlan is not null)
                return Blocked(operation, "AmbiguousNestedRecoveryEvidence");
            var installedEvidence = await _evidence.PrepareInstalledSourceAsync(
                package, package, null, profile, cancellationToken);
            if (installedEvidence.Blocker is not null)
                return Blocked(operation, installedEvidence.Blocker);
            return await BuildPlanAsync(
                evidence, operation, package, package, related, null,
                installedEvidence,
                VersionSwitchRecoveryAction.VerifyOriginalSource,
                sourceAlreadyRestored: true, libraryRoot, cancellationToken);
        }
        if (!IsRecoverableSourceState(package, evidence,
                nestedPlan is null
                    ? null
                    : related.Single(value =>
                        value.OperationId == nestedPlan.OperationId)))
        {
            return Blocked(operation, "UnsupportedVersionSwitchShape");
        }
        var removedEvidence = await _evidence.PrepareRemovedSourceAsync(
            operation, package, removal, false, nestedPlan, null,
            profile, cancellationToken);
        if (removedEvidence.Blocker is not null)
            return Blocked(operation, removedEvidence.Blocker);
        var trust = await ArchiveTrustGate.AssessForInstallAsync(
            package, cancellationToken).ConfigureAwait(false);
        if (!trust.IsTrusted)
            return Blocked(operation, "SourceArchiveEvidenceMissing");
        removedEvidence.Identities.Add(trust.Identity!.ArchiveSha256);
        return await BuildPlanAsync(
            evidence, operation, package, package, related, nestedPlan,
            removedEvidence,
            VersionSwitchRecoveryAction.RestoreOriginalSource,
            sourceAlreadyRestored: false, libraryRoot, cancellationToken,
            [package.Package.ArchivePath]);
    }

    private async Task<RecoveryPlan> PrepareBeforeRemovalAsync(
        RecoveryRepositoryEvidence evidence,
        InstallOperationRecord operation,
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        IReadOnlyList<InstallOperationRecord> related,
        RecoveryPlan? nestedPlan,
        GameProfileRecord profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        if (nestedPlan is not null || related.Count != 0 ||
            operation.CurrentPhase is not (
                InstallOperationPhase.Created or InstallOperationPhase.Staging) ||
            source.Package.InstallationState !=
                PackageInstallationState.Installed ||
            !await IsCleanTargetAsync(target, cancellationToken))
        {
            return Blocked(operation, "UnsupportedVersionSwitchShape");
        }
        var sourceEvidence = await _evidence.PrepareInstalledSourceAsync(
            source, target, null, profile, cancellationToken);
        if (sourceEvidence.Blocker is not null)
            return Blocked(operation, sourceEvidence.Blocker);
        return await BuildPlanAsync(
            evidence, operation, source, target, related, null,
            sourceEvidence, VersionSwitchRecoveryAction.VerifyOriginalSource,
            sourceAlreadyRestored: true, libraryRoot, cancellationToken);
    }

    private async Task<RecoveryPlan> PrepareSourceRemovalRecoveryAsync(
        RecoveryRepositoryEvidence evidence,
        InstallOperationRecord operation,
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        IReadOnlyList<InstallOperationRecord> related,
        InstallOperationRecord sourceRemoval,
        RecoveryPlan? nestedPlan,
        GameProfileRecord profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        if (nestedPlan?.OperationId != sourceRemoval.OperationId ||
            nestedPlan.Kind != RecoveryOperationKind.Remove ||
            source.Package.InstallationState !=
                PackageInstallationState.PartiallyInstalled ||
            !await IsCleanTargetAsync(target, cancellationToken) ||
            related.Any(value => value.OperationId != sourceRemoval.OperationId))
        {
            return Blocked(operation, "UnsupportedVersionSwitchShape");
        }
        var sourceEvidence = await _evidence.PrepareInstalledSourceAsync(
            source, target, nestedPlan, profile, cancellationToken);
        if (sourceEvidence.Blocker is not null)
            return Blocked(operation, sourceEvidence.Blocker);
        return await BuildPlanAsync(
            evidence, operation, source, target, related, nestedPlan,
            sourceEvidence, VersionSwitchRecoveryAction.RecoverSourceRemoval,
            sourceAlreadyRestored: true, libraryRoot, cancellationToken);
    }

    private async Task<RecoveryPlan> PrepareAfterRemovalAsync(
        RecoveryRepositoryEvidence evidence,
        InstallOperationRecord operation,
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        IReadOnlyList<InstallOperationRecord> related,
        InstallOperationRecord sourceRemoval,
        IReadOnlyList<InstallOperationRecord> targetInstalls,
        IReadOnlyList<InstallOperationRecord> sourceInstalls,
        IReadOnlyList<InstallOperationRecord> targetRemoves,
        RecoveryPlan? nestedPlan,
        GameProfileRecord profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        var completedTargetInstall = targetInstalls.SingleOrDefault(IsCompleted);
        var completedTargetRemove = targetRemoves.SingleOrDefault(IsCompleted);
        var completedSourceInstall = sourceInstalls.SingleOrDefault(IsCompleted);
        if (completedTargetRemove is not null && completedTargetInstall is null)
            return Blocked(operation, "UnsupportedVersionSwitchShape");
        var nestedRow = nestedPlan is null
            ? null
            : related.Single(value => value.OperationId == nestedPlan.OperationId);
        if (!IsSupportedNested(
                nestedRow, operation.PackageId, target.Package.PackageId,
                completedTargetInstall is not null))
        {
            return Blocked(operation, "AmbiguousNestedRecoveryEvidence");
        }

        var sourceAlreadyRestored = source.Package.InstallationState ==
            PackageInstallationState.Installed;
        if (sourceAlreadyRestored != (completedSourceInstall is not null) ||
            sourceAlreadyRestored && nestedPlan is not null ||
            !sourceAlreadyRestored && !IsRecoverableSourceState(
                source, evidence, nestedRow))
        {
            return Blocked(operation, "UnsupportedVersionSwitchShape");
        }
        var targetRollbackRequired = completedTargetInstall is not null &&
            completedTargetRemove is null;
        if (!TargetStateMatches(
                target, targetRollbackRequired, nestedRow,
                target.Package.PackageId))
        {
            return Blocked(operation, "UnsupportedVersionSwitchShape");
        }
        if (!targetRollbackRequired &&
            !await IsCleanTargetAsync(target, cancellationToken) &&
            nestedRow is not { OperationType: "Install" })
        {
            return Blocked(operation, "TargetManagedDamagePresent");
        }

        VersionSwitchTargetRollbackEvidence? targetRollback = null;
        if (targetRollbackRequired)
        {
            targetRollback = await _evidence.PrepareTargetRollbackAsync(
                target, completedTargetInstall!, nestedPlan, profile,
                cancellationToken);
            if (targetRollback.Blocker is not null)
                return Blocked(operation, targetRollback.Blocker);
        }
        var sourceEvidence = await _evidence.PrepareRemovedSourceAsync(
            operation, source, sourceRemoval, sourceAlreadyRestored,
            nestedPlan, targetRollback, profile, cancellationToken);
        if (sourceEvidence.Blocker is not null)
            return Blocked(operation, sourceEvidence.Blocker);
        var trust = await ArchiveTrustGate.AssessForInstallAsync(
            source, cancellationToken).ConfigureAwait(false);
        if (!trust.IsTrusted)
            return Blocked(operation, "SourceArchiveEvidenceMissing");
        sourceEvidence.Identities.Add(trust.Identity!.ArchiveSha256);
        if (targetRollback is not null)
            sourceEvidence.Identities.AddRange(targetRollback.Identities);
        var action = targetRollbackRequired
            ? VersionSwitchRecoveryAction
                .RollbackTargetAndRestoreOriginalSource
            : VersionSwitchRecoveryAction.RestoreOriginalSource;
        return await BuildPlanAsync(
            evidence, operation, source, target, related, nestedPlan,
            sourceEvidence, action, sourceAlreadyRestored, libraryRoot,
            cancellationToken, [source.Package.ArchivePath]);
    }

    private async Task<RecoveryPlan> BuildPlanAsync(
        RecoveryRepositoryEvidence evidence,
        InstallOperationRecord operation,
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        IReadOnlyList<InstallOperationRecord> related,
        RecoveryPlan? nestedPlan,
        VersionSwitchSourceEvidence sourceEvidence,
        VersionSwitchRecoveryAction action,
        bool sourceAlreadyRestored,
        string? libraryRoot,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? dependencies = null)
    {
        var identities = related.Select(value =>
                $"{value.OperationId:N}:{value.PackageId.Value}:" +
                $"{value.OperationType}:{value.Status}:{value.CurrentPhase}")
            .Concat(sourceEvidence.Identities).ToList();
        identities.AddRange((await _repository.LoadPackageRelationsAsync(
                target.Package.PackageId, cancellationToken))
            .Select(RecoveryPlanner.RelationIdentity));
        if (nestedPlan is not null)
            identities.Add(nestedPlan.ValidationToken);
        return RecoveryPlanner.Ready(
            operation,
            RecoveryOperationKind.VersionSwitch,
            [source.Package.PackageId, target.Package.PackageId],
            "OriginalSourceInstalled",
            nestedPlan?.PathActions ?? [],
            (dependencies ?? [])
                .Concat(nestedPlan?.EvidenceDependencies ?? []).ToArray(),
            evidence,
            identities,
            libraryRoot) with
        {
            SourcePackageId = source.Package.PackageId,
            TargetPackageId = target.Package.PackageId,
            NestedRecoveryOperationId = nestedPlan?.OperationId,
            NestedRecoveryKind = nestedPlan?.Kind ??
                RecoveryOperationKind.Unknown,
            NestedRecoveryPackageId = nestedPlan is null
                ? null
                : nestedPlan.PackageIds.Single(),
            VersionSwitchAction = action,
            SourceAlreadyRestored = sourceAlreadyRestored,
            SourceLayers = sourceEvidence.Plans
        };
    }

    private async Task<bool> IsCleanTargetAsync(
        OrganizerPackageRecord target,
        CancellationToken cancellationToken) =>
        target.Package.InstallationState ==
            PackageInstallationState.NotInstalled &&
        await _repository.LoadManagedPathLayersForPackageAsync(
            target.Package.PackageId, cancellationToken) is { Count: 0 };

    private static bool IsSupportedNested(
        InstallOperationRecord? nested,
        PackageId sourceId,
        PackageId targetId,
        bool targetWasInstalled) => nested is null || nested switch
        {
            { OperationType: "Install" } when
                nested.PackageId == targetId && !targetWasInstalled => true,
            { OperationType: "Install" } when
                nested.PackageId == sourceId => true,
            { OperationType: "Remove" } when
                nested.PackageId == targetId && targetWasInstalled => true,
            _ => false
        };

    private static bool IsRecoverableSourceState(
        OrganizerPackageRecord source,
        RecoveryRepositoryEvidence evidence,
        InstallOperationRecord? nested) =>
        source.Package.InstallationState ==
            PackageInstallationState.NotInstalled ||
        source.Package.InstallationState ==
            PackageInstallationState.PartiallyInstalled &&
        (nested is { OperationType: "Install" } &&
             nested.PackageId == source.Package.PackageId ||
         nested is null && evidence.InstalledMod is null &&
             evidence.InstalledFiles.Count == 0 &&
             evidence.PackageLayers.Count == 0);

    private static bool TargetStateMatches(
        OrganizerPackageRecord target,
        bool rollbackRequired,
        InstallOperationRecord? nested,
        PackageId targetId) => rollbackRequired
        ? target.Package.InstallationState == PackageInstallationState.Installed ||
          target.Package.InstallationState ==
              PackageInstallationState.PartiallyInstalled &&
          nested is { OperationType: "Remove" } && nested.PackageId == targetId
        : target.Package.InstallationState ==
              PackageInstallationState.NotInstalled ||
          target.Package.InstallationState ==
              PackageInstallationState.PartiallyInstalled &&
          nested is { OperationType: "Install" } && nested.PackageId == targetId;

    private static IReadOnlyList<InstallOperationRecord> Select(
        IEnumerable<InstallOperationRecord> operations,
        PackageId packageId,
        string operationType) => operations.Where(value =>
            value.PackageId == packageId &&
            value.OperationType == operationType).ToArray();

    private static bool IsCompleted(InstallOperationRecord operation) =>
        operation.Status == InstallOperationStatus.Completed;

    private static RecoveryPlan Blocked(
        InstallOperationRecord operation,
        string blocker) => RecoveryPlanner.Blocked(
        operation, RecoveryOperationKind.VersionSwitch, blocker);
}
