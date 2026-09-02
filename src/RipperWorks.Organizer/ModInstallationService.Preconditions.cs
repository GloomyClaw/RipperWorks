using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class ModInstallationService
{
    private async Task<ModInstallationResult?> ValidatePreconditionsAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        PackageId? projectedRemovalPackageId,
        Guid? ignoredOperationId)
    {
        if (profile is null ||
            profile.ValidationState != GameProfileValidationState.Valid)
        {
            return Failure("GameProfileMissing");
        }

        var mutationBlock = await OperationPerformanceDiagnostics
            .MeasureDatabaseAsync(() =>
                _repository.LoadPackageMutationBlockAsync(
                    mod.Package.PackageId,
                    ignoredOperationId,
                    cancellationToken)).ConfigureAwait(false);
        if (mutationBlock.IsBlocked)
        {
            return Failure(
                "InstallationStateRecoveryRequired",
                mutationBlock.Reason);
        }

        var savedProfile = await OperationPerformanceDiagnostics
            .MeasureDatabaseAsync(() =>
                _repository.LoadGameProfileAsync(cancellationToken));
        if (savedProfile is null ||
            savedProfile.ValidationState != GameProfileValidationState.Valid ||
            !string.Equals(
                Path.GetFullPath(savedProfile.GameRoot),
                Path.GetFullPath(profile.GameRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure("GameProfileMissing");
        }
        if (mod.Package.InstallationState !=
                PackageInstallationState.NotInstalled &&
            mod.Package.PackageId != projectedRemovalPackageId)
        {
            return Failure("PackageAlreadyInstalled");
        }
        var validation = await _profileService.ValidateFastAsync(
            profile.GameRoot,
            libraryRoot,
            cancellationToken);
        if (!validation.IsValid)
            return Failure(validation.ErrorCode ?? "GameProfileInvalid");
        if (!mod.IsPresent || !File.Exists(mod.Package.ArchivePath))
            return Failure("ArchiveMissing");
        if (mod.Analysis is null)
            return Failure("AnalysisMissing");
        if (mod.Analysis.State != PackageAnalysisState.Ready)
            return Failure("AnalysisNotReady");
        if (!ArchiveTrustGate.IsTrustedForInstall(mod.Analysis, mod.Package))
        {
            return Failure(
                ArchiveTrustGate.IsLegacyAnalysis(mod.Analysis)
                    ? "RequiresReanalysis"
                    : "AnalysisStale");
        }
        if (mod.Analysis.DetectedRoots.Count > 1 &&
            string.IsNullOrWhiteSpace(mod.Analysis.SelectedRoot))
        {
            return Failure("InstallRootNotSelected");
        }
        return null;
    }

    private static bool CanInstall(InstallPlan plan) =>
        string.IsNullOrWhiteSpace(plan.ErrorCode) &&
        plan.Entries.Count > 0 &&
        plan.ConflictCount == 0 &&
        plan.BlockedCount == 0;

    private static ModInstallationResult PlanFailure(
        InstallPlan plan,
        Guid? operationId = null) =>
        new()
        {
            OperationId = operationId,
            Status = InstallOperationStatus.Blocked,
            InstallationState = PackageInstallationState.NotInstalled,
            ErrorCode = plan.ErrorCode ??
                (plan.ConflictCount > 0
                    ? "InstallConflict"
                    : "InstallPlanBlocked"),
            Plan = plan
        };

    private static void DeleteEmptyParents(string directory, string gameRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        var current = Path.GetFullPath(directory);
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase) &&
               current.StartsWith(
                   root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(current) &&
               !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }

    private void TryDeleteOwnedStaging(string stagingPath)
    {
        try
        {
            var normalized = Path.GetFullPath(stagingPath);
            var root = Path.TrimEndingDirectorySeparator(_stagingRoot);
            if (!normalized.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            ArchivePathSafety.EnsureNoReparsePoints(root, normalized);
            if (Directory.Exists(normalized))
                Directory.Delete(normalized, true);
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
        }
    }

    private static ModInstallationResult Failure(
        string errorCode,
        string? errorMessage = null,
        Guid? operationId = null,
        InstallOperationStatus status = InstallOperationStatus.Blocked,
        PackageInstallationState installationState =
            PackageInstallationState.NotInstalled) =>
        new()
        {
            OperationId = operationId,
            Status = status,
            InstallationState = installationState,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
}
