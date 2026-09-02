using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class UnifiedModMutationCoordinator
{
    internal Func<string, Task>? TestOnlyBeforeExecutionGateWait { get; set; }
    internal Func<string, Task>? TestOnlyAfterExecutionGateEntered { get; set; }

    async Task<InstallPlan> IApplicationMutationRoute.PrepareInstallAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken)
    {
        var plan = await PrepareAsync(
            [new(package, package.EffectiveDisplayName, 0)],
            profile,
            libraryRoot,
            cancellationToken);
        _singleInstallPlans[package.Package.PackageId] = plan;
        return plan.Items[0].Plan ?? new InstallPlan
        {
            PackageId = package.Package.PackageId,
            ErrorCode = plan.Items[0].Reason
        };
    }

    async Task<ModInstallationResult>
        IApplicationMutationRoute.ExecuteInstallAsync(
            OrganizerPackageRecord package,
            GameProfileRecord? profile,
            string? libraryRoot,
            IProgress<ModInstallationProgress>? progress,
            CancellationToken cancellationToken)
    {
        if (!_singleInstallPlans.TryRemove(
                package.Package.PackageId,
                out var approved) ||
            !approved.CanExecute)
        {
            return InstallFailure("PreflightRequired");
        }
        if (TestOnlyBeforeExecutionGateWait is not null)
            await TestOnlyBeforeExecutionGateWait("Install");
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            if (TestOnlyAfterExecutionGateEntered is not null)
                await TestOnlyAfterExecutionGateEntered("Install");
            var fresh = await _preflight.PrepareInstallAsync(
                approved.Items.Select(item => item.Candidate).ToArray(),
                profile,
                libraryRoot,
                cancellationToken);
            if (!fresh.CanExecute || fresh.ValidationToken !=
                approved.ValidationToken)
            {
                return InstallFailure(InvalidationReason(fresh.Items));
            }
            return await _installer.InstallPrimitiveAsync(
                fresh.ExecutionOrder[0].Candidate.Package,
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

    Task<ModRemovalPlan> IApplicationMutationRoute.PrepareRemovalAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken) =>
        ((IApplicationMutationRoute)this).PrepareRemovalAsync(
            package,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles: null);

    async Task<ModRemovalPlan> IApplicationMutationRoute.PrepareRemovalAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles)
    {
        var all = await OperationPerformanceDiagnostics.MeasureDatabaseAsync(
            () => _repository.LoadPackagesAsync(false, cancellationToken));
        var library = all.Select((item, index) =>
            new BatchPackageCandidate(
                item,
                item.EffectiveDisplayName,
                index)).ToArray();
        var candidate = library.SingleOrDefault(item =>
            Id(item) == package.Package.PackageId) ??
            new(package, package.EffectiveDisplayName, 0);
        var plan = await PrepareAsync(
            [candidate],
            library,
            profile,
            libraryRoot,
            authorizedModifiedFiles,
            cancellationToken);
        _singleRemovalPlans[package.Package.PackageId] = plan;
        return plan.Items[0].Plan ?? new ModRemovalPlan
        {
            PackageId = package.Package.PackageId,
            ErrorCode = plan.UnselectedRelated.Count > 0
                ? "InstalledDependentsNotSelected"
                : plan.Items[0].Reason
        };
    }

    Task<ModRemovalResult> IApplicationMutationRoute.ExecuteRemovalAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken) =>
        ((IApplicationMutationRoute)this).ExecuteRemovalAsync(
            package,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles: null);

    async Task<ModRemovalResult> IApplicationMutationRoute.ExecuteRemovalAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles)
    {
        if (!_singleRemovalPlans.TryRemove(
                package.Package.PackageId,
                out var approved) ||
            !approved.CanExecute)
        {
            return RemoveFailure("PreflightRequired");
        }
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var currentLibrary = await LoadCurrentLibraryAsync(
                approved.Items.Select(item => item.Candidate).ToArray(),
                cancellationToken);
            var fresh = await _preflight.PrepareRemovalAsync(
                approved.Items.Select(item => item.Candidate).ToArray(),
                currentLibrary,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles);
            if (!fresh.CanExecute || fresh.ValidationToken !=
                approved.ValidationToken)
            {
                return RemoveFailure(InvalidationReason(fresh.Items));
            }
            return await _remover.RemovePrimitiveAsync(
                fresh.ExecutionOrder[0].Candidate.Package,
                profile,
                libraryRoot,
                cancellationToken,
                ignoredOperationId: null,
                authorizedModifiedFiles: authorizedModifiedFiles);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    async Task<RelationMutationResult>
        IApplicationMutationRoute.ConfirmRelationAsync(
            PackageId childPackageId,
            PackageId parentPackageId,
            PackageRelationType relationType,
            PackageRelationSource source,
            bool isConfirmed,
            CancellationToken cancellationToken)
    {
        var existing = await FindPairAsync(
            childPackageId,
            parentPackageId,
            cancellationToken);
        if (existing is not null)
            return Equivalent(existing, relationType, isConfirmed);
        var result = await _relations.AddPrimitiveAsync(
            childPackageId,
            parentPackageId,
            relationType,
            source,
            isConfirmed,
            cancellationToken);
        if (result.Success)
            return result;
        existing = await FindPairAsync(
            childPackageId,
            parentPackageId,
            cancellationToken);
        return existing is null
            ? result
            : Equivalent(existing, relationType, isConfirmed);
    }

    Task<ModArchiveSwitchPlan>
        IApplicationMutationRoute.PrepareSwitchAsync(
            OrganizerPackageRecord installedArchive,
            OrganizerPackageRecord targetArchive,
            GameProfileRecord? profile,
            string? libraryRoot,
            CancellationToken cancellationToken) =>
        ((IApplicationMutationRoute)this).PrepareSwitchAsync(
            installedArchive,
            targetArchive,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles: null);

    async Task<ModArchiveSwitchPlan>
        IApplicationMutationRoute.PrepareSwitchAsync(
            OrganizerPackageRecord installedArchive,
            OrganizerPackageRecord targetArchive,
            GameProfileRecord? profile,
            string? libraryRoot,
            CancellationToken cancellationToken,
            ModifiedFilesAuthorization? authorizedModifiedFiles)
    {
        if (_versionSwitch is null)
        {
            return new()
            {
                InstalledArchiveId = installedArchive.Package.PackageId,
                TargetArchiveId = targetArchive.Package.PackageId,
                ErrorCode = "ArchiveSwitchCoordinatorRequired"
            };
        }
        return await _versionSwitch.PrepareAsync(
            installedArchive,
            targetArchive,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles);
    }

    Task<ModArchiveSwitchResult>
        IApplicationMutationRoute.ExecuteSwitchAsync(
            OrganizerPackageRecord installedArchive,
            OrganizerPackageRecord targetArchive,
            GameProfileRecord? profile,
            string? libraryRoot,
            CancellationToken cancellationToken) =>
        ((IApplicationMutationRoute)this).ExecuteSwitchAsync(
            installedArchive,
            targetArchive,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles: null);

    async Task<ModArchiveSwitchResult>
        IApplicationMutationRoute.ExecuteSwitchAsync(
            OrganizerPackageRecord installedArchive,
            OrganizerPackageRecord targetArchive,
            GameProfileRecord? profile,
            string? libraryRoot,
            CancellationToken cancellationToken,
            ModifiedFilesAuthorization? authorizedModifiedFiles)
    {
        if (_versionSwitch is null)
        {
            return new()
            {
                ErrorCode = "ArchiveSwitchCoordinatorRequired"
            };
        }
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            return await _versionSwitch.ExecuteAsync(
                installedArchive,
                targetArchive,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles);
        }
        finally
        {
            _executionGate.Release();
        }
    }
}
