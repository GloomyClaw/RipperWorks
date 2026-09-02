using System.Collections.Concurrent;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed class VersionSwitchInterruptedException(string message)
    : Exception(message);

internal sealed class VersionSwitchMutationCoordinator(
    OrganizerRepository repository,
    ModInstallationService installer,
    ModRemovalService remover,
    ModArchiveSwitchService facade)
{
    private readonly VersionSwitchPreflight _preflight =
        new(repository, installer, remover);
    private readonly ConcurrentDictionary<SwitchKey, ModArchiveSwitchPlan>
        _approvedPlans = new();

    public async Task<ModArchiveSwitchPlan> PrepareAsync(
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        var plan = await _preflight.PrepareAsync(
            source,
            target,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles);
        _approvedPlans[Key(source, target)] = plan;
        return plan;
    }

    public async Task<ModArchiveSwitchResult> ExecuteAsync(
        OrganizerPackageRecord source,
        OrganizerPackageRecord target,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        if (!_approvedPlans.TryRemove(Key(source, target), out var approved))
            return Failure("PreflightRequired");
        if (!approved.CanSwitch)
            return Failure(approved.ErrorCode ?? "PreflightBlocked", approved);

        var fresh = await _preflight.PrepareAsync(
            source,
            target,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles);
        if (!fresh.CanSwitch ||
            !string.Equals(
                fresh.ValidationToken,
                approved.ValidationToken,
                StringComparison.Ordinal))
        {
            return Failure("PreflightInvalidated", approved);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var operationId = Guid.NewGuid();
        await repository.CreateVersionSwitchOperationAsync(
            operationId,
            fresh.InstalledArchiveId,
            fresh.TargetArchiveId,
            fresh.Operation,
            cancellationToken);

        var sourceRemoved = false;
        try
        {
            await repository.UpdateInstallOperationAsync(
                operationId,
                InstallOperationPhase.Staging,
                cancellationToken: CancellationToken.None);
            await facade.ReachAsync(
                VersionSwitchExecutionPoint.BeforeSourceRemoval,
                CancellationToken.None);

            if (cancellationToken.IsCancellationRequested)
            {
                return await BlockBeforeMutationAsync(
                    operationId,
                    fresh,
                    "ArchiveSwitchCanceled");
            }

            var finalBoundaryTarget = await LoadRequiredAsync(
                fresh.TargetArchiveId,
                CancellationToken.None);
            var finalTrust = await ArchiveTrustGate.AssessForInstallAsync(
                finalBoundaryTarget,
                CancellationToken.None).ConfigureAwait(false);
            if (!finalTrust.IsTrusted)
            {
                return await BlockBeforeMutationAsync(
                    operationId,
                    fresh,
                    finalTrust.Code);
            }

            VerifiedArchiveLease targetLease;
            try
            {
                targetLease = await VerifiedArchiveLease.AcquireAsync(
                    finalBoundaryTarget.Package.ArchivePath,
                    finalTrust.Identity!.ArchiveSha256,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return await BlockBeforeMutationAsync(
                    operationId,
                    fresh,
                    "AnalysisStale");
            }

            ModRemovalResult removal;
            await using (targetLease.ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return await BlockBeforeMutationAsync(
                        operationId,
                        fresh,
                        "ArchiveSwitchCanceled");
                }

                var currentSource = await LoadRequiredAsync(
                    fresh.InstalledArchiveId,
                    CancellationToken.None);
                if (cancellationToken.IsCancellationRequested)
                {
                    return await BlockBeforeMutationAsync(
                        operationId,
                        fresh,
                        "ArchiveSwitchCanceled");
                }
                removal = await remover.RemovePrimitiveAsync(
                    currentSource,
                    profile,
                    libraryRoot,
                    CancellationToken.None,
                    operationId,
                    authorizedModifiedFiles: authorizedModifiedFiles);
            }
            if (!removal.Success)
            {
                return await RemovalFailureAsync(
                    operationId,
                    fresh,
                    removal,
                    CancellationToken.None);
            }
            sourceRemoved = true;
            await repository.UpdateInstallOperationAsync(
                operationId,
                InstallOperationPhase.ManifestPlanned,
                cancellationToken: CancellationToken.None);
            await facade.ReachAsync(
                VersionSwitchExecutionPoint.AfterSourceRemoval,
                CancellationToken.None);
            if (facade.ShouldFail(VersionSwitchFaultPoint.AfterSourceRemoval))
            {
                throw new VersionSwitchInterruptedException(
                    "Synthetic interruption after source removal.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return await RestoreAsync(
                    operationId,
                    fresh,
                    profile,
                    libraryRoot,
                    "ArchiveSwitchCanceled",
                    CancellationToken.None);
            }

            await facade.ReachAsync(
                VersionSwitchExecutionPoint.BeforeTargetInstall,
                CancellationToken.None);
            await repository.UpdateInstallOperationAsync(
                operationId,
                InstallOperationPhase.Deploying,
                cancellationToken: CancellationToken.None);
            ModInstallationResult targetResult;
            if (facade.ShouldFail(VersionSwitchFaultPoint.BeforeTargetInstall))
            {
                targetResult = new()
                {
                    Status = InstallOperationStatus.RolledBack,
                    InstallationState = PackageInstallationState.NotInstalled,
                    ErrorCode = "InjectedArchiveSwitchFailure"
                };
            }
            else
            {
                var currentTarget = await LoadRequiredAsync(
                    fresh.TargetArchiveId,
                    CancellationToken.None);
                targetResult = await installer.InstallPrimitiveAsync(
                    currentTarget,
                    profile,
                    libraryRoot,
                    cancellationToken: CancellationToken.None,
                    ignoredOperationId: operationId);
            }

            if (targetResult.Success)
            {
                await facade.ReachAsync(
                    VersionSwitchExecutionPoint.AfterTargetInstall,
                    CancellationToken.None);
                await repository.UpdateInstallOperationAsync(
                    operationId,
                    InstallOperationPhase.Completed,
                    InstallOperationStatus.Completed,
                    completed: true,
                    cancellationToken: CancellationToken.None);
                return new()
                {
                    Success = true,
                    OperationId = operationId,
                    Plan = fresh
                };
            }

            if (targetResult.Status == InstallOperationStatus.RecoveryRequired ||
                targetResult.InstallationState ==
                    PackageInstallationState.PartiallyInstalled)
            {
                return await TargetRecoveryFailureAsync(
                    operationId,
                    fresh,
                    targetResult);
            }

            return await RestoreAsync(
                operationId,
                fresh,
                profile,
                libraryRoot,
                targetResult.ErrorCode ?? "ArchiveSwitchTargetInstallFailed",
                CancellationToken.None);
        }
        catch (VersionSwitchInterruptedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (sourceRemoved)
            {
                return await RestoreAsync(
                    operationId,
                    fresh,
                    profile,
                    libraryRoot,
                    exception.Message,
                    CancellationToken.None);
            }
            await repository.UpdateInstallOperationAsync(
                operationId,
                InstallOperationPhase.RollingBack,
                InstallOperationStatus.RolledBack,
                exception.Message,
                completed: true,
                cancellationToken: CancellationToken.None);
            return Failure("ArchiveSwitchRemovalFailed", fresh, operationId);
        }
    }

    private async Task<ModArchiveSwitchResult> BlockBeforeMutationAsync(
        Guid operationId,
        ModArchiveSwitchPlan plan,
        string errorCode)
    {
        await repository.UpdateInstallOperationAsync(
            operationId,
            InstallOperationPhase.Staging,
            InstallOperationStatus.Blocked,
            errorCode,
            completed: true,
            cancellationToken: CancellationToken.None);
        return Failure(errorCode, plan, operationId);
    }

    private async Task<ModArchiveSwitchResult> TargetRecoveryFailureAsync(
        Guid operationId,
        ModArchiveSwitchPlan plan,
        ModInstallationResult targetResult)
    {
        var errorCode = targetResult.ErrorCode ?? "RecoveryRequired";
        await repository.UpdateInstallOperationAsync(
            operationId,
            InstallOperationPhase.Deploying,
            InstallOperationStatus.RecoveryRequired,
            errorCode,
            completed: true,
            cancellationToken: CancellationToken.None);
        return new()
        {
            OperationId = operationId,
            RecoveryRequired = true,
            ErrorCode = errorCode,
            Plan = plan
        };
    }

    private async Task<ModArchiveSwitchResult> RestoreAsync(
        Guid operationId,
        ModArchiveSwitchPlan plan,
        GameProfileRecord? profile,
        string? libraryRoot,
        string failureCode,
        CancellationToken cancellationToken)
    {
        await repository.UpdateInstallOperationAsync(
            operationId,
            InstallOperationPhase.RollingBack,
            errorMessage: failureCode,
            cancellationToken: cancellationToken);
        await facade.ReachAsync(
            VersionSwitchExecutionPoint.BeforeSourceRestore,
            cancellationToken);
        ModInstallationResult rollback;
        if (facade.ShouldFail(VersionSwitchFaultPoint.BeforeSourceRestore))
        {
            rollback = new()
            {
                Status = InstallOperationStatus.RecoveryRequired,
                InstallationState = PackageInstallationState.PartiallyInstalled,
                ErrorCode = "InjectedArchiveSwitchRollbackFailure"
            };
        }
        else
        {
            var rollbackSource = await LoadRequiredAsync(
                plan.InstalledArchiveId,
                cancellationToken);
            rollback = await installer.InstallPrimitiveAsync(
                rollbackSource,
                profile,
                libraryRoot,
                cancellationToken: cancellationToken,
                ignoredOperationId: operationId);
        }

        if (rollback.Success)
        {
            await repository.UpdateInstallOperationAsync(
                operationId,
                InstallOperationPhase.RollingBack,
                InstallOperationStatus.RolledBack,
                failureCode,
                completed: true,
                cancellationToken: cancellationToken);
            return new()
            {
                OperationId = operationId,
                RolledBack = true,
                ErrorCode = failureCode,
                Plan = plan
            };
        }

        await repository.MarkPackageRecoveryRequiredAsync(
            plan.InstalledArchiveId,
            cancellationToken);
        await repository.UpdateInstallOperationAsync(
            operationId,
            InstallOperationPhase.RollingBack,
            InstallOperationStatus.RecoveryRequired,
            rollback.ErrorCode ?? "ArchiveSwitchRollbackFailed",
            completed: true,
            cancellationToken: cancellationToken);
        return new()
        {
            OperationId = operationId,
            RecoveryRequired = true,
            ErrorCode = "ArchiveSwitchRollbackFailed",
            Plan = plan
        };
    }

    private async Task<ModArchiveSwitchResult> RemovalFailureAsync(
        Guid operationId,
        ModArchiveSwitchPlan plan,
        ModRemovalResult removal,
        CancellationToken cancellationToken)
    {
        var recoveryRequired = removal.Status ==
            InstallOperationStatus.RecoveryRequired;
        await repository.UpdateInstallOperationAsync(
            operationId,
            recoveryRequired
                ? InstallOperationPhase.RollingBack
                : InstallOperationPhase.Staging,
            recoveryRequired
                ? InstallOperationStatus.RecoveryRequired
                : InstallOperationStatus.RolledBack,
            removal.ErrorMessage ?? removal.ErrorCode,
            completed: true,
            cancellationToken: cancellationToken);
        return new()
        {
            OperationId = operationId,
            RecoveryRequired = recoveryRequired,
            ErrorCode = removal.ErrorCode ?? "ArchiveSwitchRemovalFailed",
            Plan = plan
        };
    }

    private async Task<OrganizerPackageRecord> LoadRequiredAsync(
        PackageId packageId,
        CancellationToken cancellationToken) =>
        (await repository.LoadPackagesAsync(false, cancellationToken))
        .Single(value => value.Package.PackageId == packageId);

    private static SwitchKey Key(
        OrganizerPackageRecord source,
        OrganizerPackageRecord target) => new(
        source.Package.PackageId,
        target.Package.PackageId);

    private static ModArchiveSwitchResult Failure(
        string errorCode,
        ModArchiveSwitchPlan? plan = null,
        Guid? operationId = null) => new()
        {
            OperationId = operationId,
            ErrorCode = errorCode,
            Plan = plan
        };

    private readonly record struct SwitchKey(
        PackageId SourcePackageId,
        PackageId TargetPackageId);
}
