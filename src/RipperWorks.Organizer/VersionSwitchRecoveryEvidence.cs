using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed record VersionSwitchSourceEvidence(
    IReadOnlyList<RecoverySourceLayerPlan> Plans,
    List<string> Identities,
    string? Blocker)
{
    public static VersionSwitchSourceEvidence Failed(string blocker) =>
        new([], [], blocker);
}

internal sealed record VersionSwitchTargetPathEvidence(
    string ExpectedAfterRemoval,
    IReadOnlyList<string> AllowedCurrentHashes);

internal sealed record VersionSwitchTargetRollbackEvidence(
    PackageId TargetPackageId,
    IReadOnlyDictionary<string, VersionSwitchTargetPathEvidence> Paths,
    IReadOnlyList<string> Identities,
    string? Blocker)
{
    public static VersionSwitchTargetRollbackEvidence Failed(string blocker) =>
        new(default, new Dictionary<string,
            VersionSwitchTargetPathEvidence>(), [], blocker);
}

internal sealed class VersionSwitchRecoveryEvidence(
    OrganizerRepository repository)
{
    public async Task<VersionSwitchSourceEvidence> PrepareInstalledSourceAsync(
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        RecoveryPlan? nestedPlan,
        GameProfileRecord profile,
        CancellationToken cancellationToken)
    {
        var installed = await repository.LoadInstalledModAsync(
            source.Package.PackageId, cancellationToken);
        var files = await repository.LoadInstalledFilesAsync(
            source.Package.PackageId, cancellationToken);
        var packageLayers = await repository.LoadManagedPathLayersForPackageAsync(
            source.Package.PackageId, cancellationToken);
        if (installed is null || files.Count == 0 ||
            packageLayers.Count != files.Count ||
            source.Package.PackageId != target.Package.PackageId &&
            await repository.LoadManagedPathLayersForPackageAsync(
                target.Package.PackageId, cancellationToken) is { Count: > 0 })
        {
            return VersionSwitchSourceEvidence.Failed(
                "SourceRestoreVerificationFailed");
        }
        var plans = new List<RecoverySourceLayerPlan>();
        var identities = new List<string>();
        foreach (var file in files.OrderBy(value => value.Sequence))
        {
            var layers = await repository.LoadManagedPathLayersAsync(
                file.RelativeGamePath, cancellationToken);
            var sourceLayer = layers.SingleOrDefault(value =>
                value.PackageId == source.Package.PackageId);
            if (sourceLayer is null ||
                sourceLayer.ContentHash != file.InstalledContentHash)
            {
                return VersionSwitchSourceEvidence.Failed(
                    "SourceRestoreVerificationFailed");
            }
            var path = RecoveryPlanner.Resolve(
                profile.GameRoot, file.RelativeGamePath);
            if (path is null)
                return VersionSwitchSourceEvidence.Failed("UnsafeRecoveryPath");
            var live = await RecoveryPlanner.IdentityAsync(
                path, cancellationToken);
            var desiredLive = layers[^1].ContentHash;
            var sourceIndex = Array.FindIndex(
                layers.ToArray(), value => value == sourceLayer);
            var nestedAction = FindAction(nestedPlan, file.RelativeGamePath);
            var nestedProvesTransition = nestedAction is not null &&
                (nestedAction.TargetContentHash ??
                    RecoveryPlanner.MissingIdentity) == desiredLive &&
                nestedAction.AllowedCurrentContentHashes.Contains(
                    live, StringComparer.Ordinal);
            if (live != desiredLive && !nestedProvesTransition)
            {
                return VersionSwitchSourceEvidence.Failed(
                    "SourceRestoreVerificationFailed");
            }
            plans.Add(new(
                file.RelativeGamePath,
                file.InstalledContentHash,
                file.PreviousContentHash,
                file.PreviousFileExisted,
                sourceIndex,
                desiredLive));
            identities.Add($"installed-source:{file.RelativeGamePath}:" +
                $"{file.InstalledContentHash}:{sourceIndex}:" +
                $"{desiredLive}:{live}");
            identities.Add(await OwnershipIdentityAsync(
                file.RelativeGamePath, cancellationToken));
        }
        return new(plans, identities, null);
    }

    public async Task<VersionSwitchSourceEvidence> PrepareRemovedSourceAsync(
        InstallOperationRecord composite,
        OrganizerPackageRecord source,
        InstallOperationRecord removal,
        bool sourceAlreadyRestored,
        RecoveryPlan? nestedPlan,
        VersionSwitchTargetRollbackEvidence? targetRollback,
        GameProfileRecord profile,
        CancellationToken cancellationToken)
    {
        var originalInstall = await repository
            .LoadLatestCompletedOperationBeforeAsync(
                source.Package.PackageId, "Install",
                composite.StartedAtUtc, cancellationToken);
        if (originalInstall is null)
            return VersionSwitchSourceEvidence.Failed(
                "SourceInstallEvidenceMissing");
        var originalFiles = await repository.LoadInstallOperationFilesAsync(
            originalInstall.OperationId, cancellationToken);
        var removalFiles = await repository.LoadInstallOperationFilesAsync(
            removal.OperationId, cancellationToken);
        var sourcePathCount = source.Analysis?.Entries.Count(value =>
            value.IsInstallable && !value.IsDirectory) ?? 0;
        if (sourcePathCount == 0 || originalFiles.Count != sourcePathCount ||
            RecoveryPlanner.HasDuplicatePaths(originalFiles) ||
            RecoveryPlanner.HasDuplicatePaths(removalFiles))
        {
            return VersionSwitchSourceEvidence.Failed(
                "SourceLayerEvidenceIncomplete");
        }
        var removalByPath = removalFiles.ToDictionary(
            value => value.RelativeGamePath, StringComparer.OrdinalIgnoreCase);
        var installedFiles = sourceAlreadyRestored
            ? await repository.LoadInstalledFilesAsync(
                source.Package.PackageId, cancellationToken)
            : [];
        if (sourceAlreadyRestored && installedFiles.Count != originalFiles.Count)
        {
            return VersionSwitchSourceEvidence.Failed(
                "SourceRestoreVerificationFailed");
        }
        var installedByPath = installedFiles.ToDictionary(
            value => value.RelativeGamePath, StringComparer.OrdinalIgnoreCase);
        var plans = new List<RecoverySourceLayerPlan>();
        var identities = new List<string>
        {
            $"source-install:{originalInstall.OperationId:N}"
        };
        foreach (var original in originalFiles.OrderBy(value => value.Sequence))
        {
            var result = await PrepareRemovedSourcePathAsync(
                source, original, removalByPath, installedByPath,
                sourceAlreadyRestored, nestedPlan, targetRollback,
                profile, cancellationToken);
            if (result.Blocker is not null)
                return VersionSwitchSourceEvidence.Failed(result.Blocker);
            plans.Add(result.Plan!);
            identities.Add(result.Identity!);
        }
        if (removalByPath.Keys.Any(path => plans.All(plan =>
                !string.Equals(plan.RelativeGamePath, path,
                    StringComparison.OrdinalIgnoreCase))))
        {
            return VersionSwitchSourceEvidence.Failed(
                "SourceRemovalEvidenceIncomplete");
        }
        return new(plans, identities, null);
    }

    private async Task<SourcePathResult> PrepareRemovedSourcePathAsync(
        OrganizerPackageRecord source,
        InstallOperationFileRecord original,
        IReadOnlyDictionary<string, InstallOperationFileRecord> removalByPath,
        IReadOnlyDictionary<string, InstalledFileRecord> installedByPath,
        bool sourceAlreadyRestored,
        RecoveryPlan? nestedPlan,
        VersionSwitchTargetRollbackEvidence? targetRollback,
        GameProfileRecord profile,
        CancellationToken cancellationToken)
    {
        if (sourceAlreadyRestored &&
            (!installedByPath.TryGetValue(
                original.RelativeGamePath, out var installedFile) ||
             installedFile.InstalledContentHash != original.NewContentHash))
        {
            return SourcePathResult.Failed("SourceRestoreVerificationFailed");
        }
        var managed = await repository.LoadManagedPathAsync(
            original.RelativeGamePath, cancellationToken);
        var layers = await repository.LoadManagedPathLayersAsync(
            original.RelativeGamePath, cancellationToken);
        var sourceLayers = layers.Where(value =>
            value.PackageId == source.Package.PackageId).ToArray();
        if (sourceLayers.Length != (sourceAlreadyRestored ? 1 : 0) ||
            sourceLayers.Any(value =>
                value.ContentHash != original.NewContentHash))
        {
            return SourcePathResult.Failed("SourceRestoreVerificationFailed");
        }
        var transientTarget = targetRollback?.TargetPackageId;
        var others = layers.Where(value =>
                value.PackageId != source.Package.PackageId &&
                value.PackageId != transientTarget)
            .OrderBy(value => value.LayerOrder).ToArray();
        var insertion = FindInsertionOrder(original, managed, others);
        if (insertion is null)
            return SourcePathResult.Failed("SourceLayerOrderAmbiguous");
        var wasTop = insertion.Value == others.Length;
        if (removalByPath.TryGetValue(
                original.RelativeGamePath, out var removed) != wasTop ||
            removed is not null &&
            (removed.NewContentHash != original.NewContentHash ||
             removed.PreviousContentHash != original.PreviousContentHash ||
             removed.PreviousFileExisted != original.PreviousFileExisted))
        {
            return SourcePathResult.Failed("SourceRemovalEvidenceIncomplete");
        }
        var removedLive = others.LastOrDefault()?.ContentHash ??
            (original.PreviousFileExisted
                ? original.PreviousContentHash
                : RecoveryPlanner.MissingIdentity);
        if (removedLive is null)
            return SourcePathResult.Failed("SourceRemovalEvidenceIncomplete");
        var destination = RecoveryPlanner.Resolve(
            profile.GameRoot, original.RelativeGamePath);
        if (destination is null)
            return SourcePathResult.Failed("UnsafeRecoveryPath");
        var live = await RecoveryPlanner.IdentityAsync(
            destination, cancellationToken);
        var nestedAction = FindAction(nestedPlan, original.RelativeGamePath);
        var nestedProvesTransition = nestedAction is not null &&
            (nestedAction.TargetContentHash ??
                RecoveryPlanner.MissingIdentity) == removedLive &&
            nestedAction.AllowedCurrentContentHashes.Contains(
                live, StringComparer.Ordinal);
        var targetProvesTransition = targetRollback?.Paths.TryGetValue(
            original.RelativeGamePath, out var targetPath) == true &&
            targetPath.ExpectedAfterRemoval == removedLive &&
            targetPath.AllowedCurrentHashes.Contains(
                live, StringComparer.Ordinal);
        if (live != removedLive && !nestedProvesTransition &&
            !targetProvesTransition &&
            (!sourceAlreadyRestored || live != original.NewContentHash))
        {
            return SourcePathResult.Failed(
                "SourceRemovedStateContradictsEvidence");
        }
        var desiredLive = wasTop
            ? original.NewContentHash
            : others[^1].ContentHash;
        var plan = new RecoverySourceLayerPlan(
            original.RelativeGamePath,
            original.NewContentHash,
            original.PreviousContentHash,
            original.PreviousFileExisted,
            insertion.Value,
            desiredLive);
        var identity = $"source:{original.RelativeGamePath}:" +
            $"{original.NewContentHash}:{original.PreviousContentHash}:" +
            $"{original.PreviousFileExisted}:{insertion}:{live}:" +
            $"{desiredLive}:" + string.Join(",", others.Select(value =>
                $"{value.PackageId.Value}/{value.ContentHash}"));
        return new(plan, identity, null);
    }

    public async Task<VersionSwitchTargetRollbackEvidence>
        PrepareTargetRollbackAsync(
            OrganizerPackageRecord target,
            InstallOperationRecord completedInstall,
            RecoveryPlan? nestedPlan,
            GameProfileRecord profile,
            CancellationToken cancellationToken)
    {
        var files = await repository.LoadInstalledFilesAsync(
            target.Package.PackageId, cancellationToken);
        var operationFiles = await repository.LoadInstallOperationFilesAsync(
            completedInstall.OperationId, cancellationToken);
        var packageLayers = await repository.LoadManagedPathLayersForPackageAsync(
            target.Package.PackageId, cancellationToken);
        if (files.Count == 0 || files.Count != operationFiles.Count ||
            files.Count != packageLayers.Count)
        {
            return VersionSwitchTargetRollbackEvidence.Failed(
                "TargetManagedDamagePresent");
        }
        var operationByPath = operationFiles.ToDictionary(
            value => value.RelativeGamePath, StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, VersionSwitchTargetPathEvidence>(
            StringComparer.OrdinalIgnoreCase);
        var identities = new List<string>();
        foreach (var file in files)
        {
            var result = await PrepareTargetPathAsync(
                target, file, operationByPath, nestedPlan, profile,
                cancellationToken);
            if (result.Blocker is not null)
            {
                return VersionSwitchTargetRollbackEvidence.Failed(
                    result.Blocker);
            }
            paths.Add(file.RelativeGamePath, result.Evidence!);
            identities.Add(result.Identity!);
            identities.Add(await OwnershipIdentityAsync(
                file.RelativeGamePath, cancellationToken));
        }
        return new(target.Package.PackageId, paths, identities, null);
    }

    private async Task<TargetPathResult> PrepareTargetPathAsync(
        OrganizerPackageRecord target,
        InstalledFileRecord file,
        IReadOnlyDictionary<string, InstallOperationFileRecord> operationByPath,
        RecoveryPlan? nestedPlan,
        GameProfileRecord profile,
        CancellationToken cancellationToken)
    {
        if (!operationByPath.TryGetValue(
                file.RelativeGamePath, out var operationFile) ||
            operationFile.NewContentHash != file.InstalledContentHash)
        {
            return TargetPathResult.Failed("TargetManagedDamagePresent");
        }
        var managed = await repository.LoadManagedPathAsync(
            file.RelativeGamePath, cancellationToken);
        var layers = await repository.LoadManagedPathLayersAsync(
            file.RelativeGamePath, cancellationToken);
        var targetLayer = layers.SingleOrDefault(value =>
            value.PackageId == target.Package.PackageId);
        if (targetLayer is null || targetLayer != layers.LastOrDefault() ||
            targetLayer.ContentHash != file.InstalledContentHash)
        {
            return TargetPathResult.Failed("TargetManagedDamagePresent");
        }
        var targetIndex = Array.FindIndex(
            layers.ToArray(), value => value == targetLayer);
        var underlying = targetIndex > 0
            ? layers[targetIndex - 1].ContentHash
            : managed?.BaseFileExisted == true
                ? managed.BaseContentHash
                : RecoveryPlanner.MissingIdentity;
        if (underlying is null ||
            file.PreviousFileExisted !=
                (underlying != RecoveryPlanner.MissingIdentity) ||
            file.PreviousContentHash !=
                (underlying == RecoveryPlanner.MissingIdentity
                    ? null
                    : underlying))
        {
            return TargetPathResult.Failed("TargetManagedDamagePresent");
        }
        var destination = RecoveryPlanner.Resolve(
            profile.GameRoot, file.RelativeGamePath);
        if (destination is null)
            return TargetPathResult.Failed("UnsafeRecoveryPath");
        var live = await RecoveryPlanner.IdentityAsync(
            destination, cancellationToken);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            file.InstalledContentHash,
            underlying
        };
        var nestedAction = FindAction(nestedPlan, file.RelativeGamePath);
        if (nestedAction is not null)
        {
            foreach (var value in nestedAction.AllowedCurrentContentHashes)
                allowed.Add(value);
        }
        if (!allowed.Contains(live))
            return TargetPathResult.Failed("TargetManagedDamagePresent");
        return new(
            new VersionSwitchTargetPathEvidence(underlying, allowed.ToArray()),
            $"target:{file.RelativeGamePath}:" +
            $"{file.InstalledContentHash}:{underlying}:{live}",
            null);
    }

    private async Task<string> OwnershipIdentityAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var managed = await repository.LoadManagedPathAsync(
            relativePath, cancellationToken);
        var layers = await repository.LoadManagedPathLayersAsync(
            relativePath, cancellationToken);
        return $"managed:{relativePath}:{managed?.BaseFileExisted}:" +
            $"{managed?.BaseContentHash}:" + string.Join(",", layers.Select(
                layer => $"{layer.LayerOrder}/{layer.PackageId.Value}/" +
                    $"{layer.ContentHash}/{layer.OperationId}"));
    }

    private static RecoveryPathAction? FindAction(
        RecoveryPlan? nestedPlan,
        string relativePath) => nestedPlan?.PathActions.SingleOrDefault(value =>
        string.Equals(value.RelativeGamePath, relativePath,
            StringComparison.OrdinalIgnoreCase));

    private static int? FindInsertionOrder(
        InstallOperationFileRecord original,
        ManagedPathRecord? managed,
        IReadOnlyList<ManagedPathLayerRecord> otherLayers)
    {
        if (!original.PreviousFileExisted)
            return managed is null || !managed.BaseFileExisted ? 0 : null;
        if (string.IsNullOrWhiteSpace(original.PreviousContentHash))
            return null;
        var candidates = new List<int>();
        if (managed?.BaseFileExisted == true &&
            managed.BaseContentHash == original.PreviousContentHash)
            candidates.Add(0);
        for (var index = 0; index < otherLayers.Count; index++)
        {
            if (otherLayers[index].ContentHash == original.PreviousContentHash)
                candidates.Add(index + 1);
        }
        if (managed is null && otherLayers.Count == 0)
            candidates.Add(0);
        return candidates.Distinct().Count() == 1
            ? candidates[0]
            : null;
    }

    private sealed record SourcePathResult(
        RecoverySourceLayerPlan? Plan,
        string? Identity,
        string? Blocker)
    {
        public static SourcePathResult Failed(string blocker) =>
            new(null, null, blocker);
    }

    private sealed record TargetPathResult(
        VersionSwitchTargetPathEvidence? Evidence,
        string? Identity,
        string? Blocker)
    {
        public static TargetPathResult Failed(string blocker) =>
            new(null, null, blocker);
    }
}
