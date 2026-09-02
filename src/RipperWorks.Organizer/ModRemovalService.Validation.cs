using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class ModRemovalService
{
    private async Task<ValidatedRemoval> ValidateAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        Guid? ignoredOperationId,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        if (profile is null ||
            profile.ValidationState != GameProfileValidationState.Valid)
        {
            return Invalid(mod, "GameProfileMissing");
        }

        var mutationBlock = await OperationPerformanceDiagnostics
            .MeasureDatabaseAsync(() =>
                _repository.LoadPackageMutationBlockAsync(
                    mod.Package.PackageId,
                    ignoredOperationId,
                    cancellationToken)).ConfigureAwait(false);
        if (mutationBlock.IsBlocked)
        {
            return Invalid(
                mod,
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
            return Invalid(mod, "GameProfileMissing");
        }
        if (mod.Package.InstallationState !=
            PackageInstallationState.Installed)
        {
            return Invalid(
                mod,
                mod.Package.InstallationState is
                    PackageInstallationState.PartiallyInstalled or
                    PackageInstallationState.Unknown
                    ? "InstallationStateRecoveryRequired"
                    : "PackageNotInstalled");
        }
        GameProfileValidationResult fastValidation;
        using (OperationPerformanceDiagnostics.MeasureMetric(
                   "PRECONDITIONS"))
        {
            fastValidation = await _profileService.ValidateFastAsync(
                profile.GameRoot,
                libraryRoot,
                cancellationToken);
        }
        if (!fastValidation.IsValid)
        {
            return Invalid(
                mod,
                fastValidation.ErrorCode ?? "GameProfileInvalid",
                fastValidation.ErrorDetail);
        }

        InstalledModRecord? installedMod;
        IReadOnlyList<InstalledFileRecord> files;
        using (OperationPerformanceDiagnostics.MeasureMetric(
                   "INSTALLED_FILES_LOAD"))
        {
            installedMod = await OperationPerformanceDiagnostics
                .MeasureDatabaseAsync(() =>
                    _repository.LoadInstalledModAsync(
                        mod.Package.PackageId,
                        cancellationToken));
            files = await OperationPerformanceDiagnostics
                .MeasureDatabaseAsync(() =>
                    _repository.LoadInstalledFilesAsync(
                        mod.Package.PackageId,
                        cancellationToken));
        }
        OperationPerformanceDiagnostics.ObserveMaximum(
            "managed_file_count",
            files.Count);
        if (installedMod is null ||
            installedMod.InstallationState !=
            PackageInstallationState.Installed ||
            files.Count == 0 ||
            files.Any(file =>
                file.PackageId != mod.Package.PackageId) ||
            !files.Select(file => file.Sequence)
                .Order()
                .SequenceEqual(Enumerable.Range(1, files.Count)))
        {
            return Invalid(mod, "RemovalManifestMissing");
        }

        IReadOnlyList<ManagedPathLayerRecord> packageLayers;
        using (OperationPerformanceDiagnostics.MeasureMetric(
                   "LAYER_LOOKUP",
                   "removal_layer_lookup"))
        {
            packageLayers = await OperationPerformanceDiagnostics
                .MeasureDatabaseAsync(() =>
                    _repository.LoadManagedPathLayersForPackageAsync(
                        mod.Package.PackageId,
                        cancellationToken));
        }
        if (packageLayers.Count != files.Count)
            return Invalid(mod, "RemovalManifestMissing");

        var unsafePaths = new List<string>();
        var changedPaths = new List<string>();
        var eligibleModifiedPaths = new List<string>();
        var eligibleModifiedIdentities = new List<ModifiedFileConfirmationIdentity>();
        var ineligiblePaths = new List<string>();
        var unauthorizedModifiedPaths = new List<string>();
        var removalFiles = new List<InstalledFileRecord>();
        foreach (var file in files)
        {
            string destination;
            try
            {
                destination = ArchivePathSafety.ResolveSafeGamePath(
                    profile.GameRoot,
                    file.RelativeGamePath);
                ArchivePathSafety.EnsureNoReparsePoints(
                    profile.GameRoot,
                    Path.GetDirectoryName(destination)!);
                if (File.Exists(destination) &&
                    (File.GetAttributes(destination) &
                     FileAttributes.ReparsePoint) != 0)
                {
                    unsafePaths.Add(file.RelativeGamePath);
                    ineligiblePaths.Add(file.RelativeGamePath);
                    continue;
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException)
            {
                unsafePaths.Add(file.RelativeGamePath);
                ineligiblePaths.Add(file.RelativeGamePath);
                continue;
            }

            ManagedPathRecord? managedPath;
            IReadOnlyList<ManagedPathLayerRecord> layers;
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "LAYER_LOOKUP",
                       "removal_layer_lookup"))
            {
                managedPath = await OperationPerformanceDiagnostics
                    .MeasureDatabaseAsync(() =>
                        _repository.LoadManagedPathAsync(
                            file.RelativeGamePath,
                            cancellationToken));
                layers = await OperationPerformanceDiagnostics
                    .MeasureDatabaseAsync(() =>
                        _repository.LoadManagedPathLayersAsync(
                            file.RelativeGamePath,
                            cancellationToken));
            }
            var targetLayer = layers.FirstOrDefault(layer =>
                layer.PackageId == mod.Package.PackageId);
            var topLayer = layers.LastOrDefault();
            if (managedPath is null ||
                targetLayer is null ||
                topLayer is null)
            {
                changedPaths.Add(file.RelativeGamePath);
                ineligiblePaths.Add(file.RelativeGamePath);
                continue;
            }

            var nextLayer = layers
                .Where(layer => layer.LayerOrder < targetLayer.LayerOrder)
                .OrderByDescending(layer => layer.LayerOrder)
                .FirstOrDefault();
            var previousFileExisted =
                nextLayer is not null || managedPath.BaseFileExisted;
            var previousContentHash =
                nextLayer?.ContentHash ?? managedPath.BaseContentHash;

            var liveFileExists = File.Exists(destination) && !Directory.Exists(destination);
            if (!liveFileExists)
            {
                changedPaths.Add(file.RelativeGamePath);
                ineligiblePaths.Add(file.RelativeGamePath);
                continue;
            }

            var liveHash = await ComputeLiveFileHashAsync(destination, cancellationToken);
            var liveHashMatchesTop = string.Equals(
                liveHash,
                topLayer.ContentHash,
                StringComparison.Ordinal);

            if (!liveHashMatchesTop)
            {
                changedPaths.Add(file.RelativeGamePath);
                if (topLayer.PackageId == mod.Package.PackageId)
                {
                    eligibleModifiedPaths.Add(file.RelativeGamePath);
                    eligibleModifiedIdentities.Add(
                        new ModifiedFileConfirmationIdentity(
                            file.RelativeGamePath,
                            liveHash));
                    var isAuthorized = authorizedModifiedFiles?.IsAuthorized(
                        mod.Package.PackageId,
                        file.RelativeGamePath,
                        liveHash) == true;
                    if (isAuthorized)
                    {
                        removalFiles.Add(file with
                        {
                            InstalledContentHash = targetLayer.ContentHash,
                            PreviousFileExisted = previousFileExisted,
                            PreviousContentHash = previousContentHash
                        });
                    }
                    else
                    {
                        unauthorizedModifiedPaths.Add(file.RelativeGamePath);
                    }
                }
                else
                {
                    ineligiblePaths.Add(file.RelativeGamePath);
                }
                continue;
            }

            if (topLayer.PackageId != mod.Package.PackageId)
                continue;

            removalFiles.Add(file with
            {
                InstalledContentHash = targetLayer.ContentHash,
                PreviousFileExisted = previousFileExisted,
                PreviousContentHash = previousContentHash
            });
        }
        if (unsafePaths.Count > 0)
        {
            return Invalid(
                mod,
                "RemovalUnsafePath",
                problemPaths: unsafePaths,
                eligiblePaths: eligibleModifiedPaths,
                eligibleIdentities: eligibleModifiedIdentities,
                ineligiblePaths: unsafePaths);
        }
        if (ineligiblePaths.Count > 0)
        {
            return Invalid(
                mod,
                "InstalledFilesModified",
                problemPaths: changedPaths,
                eligiblePaths: eligibleModifiedPaths,
                eligibleIdentities: eligibleModifiedIdentities,
                ineligiblePaths: ineligiblePaths);
        }
        if (unauthorizedModifiedPaths.Count > 0)
        {
            return Invalid(
                mod,
                "InstalledFilesModified",
                problemPaths: changedPaths,
                eligiblePaths: eligibleModifiedPaths,
                eligibleIdentities: eligibleModifiedIdentities,
                ineligiblePaths: []);
        }

        var backupProblems = new List<string>();
        foreach (var file in removalFiles.Where(
                     item => item.PreviousFileExisted))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(file.PreviousContentHash))
                {
                    backupProblems.Add(file.RelativeGamePath);
                    continue;
                }
                var objectPath = _contentStore.GetObjectPath(
                    file.PreviousContentHash);
                if (!File.Exists(objectPath) ||
                    !string.Equals(
                        await ComputeContentStoreHashAsync(
                            objectPath,
                            cancellationToken),
                        file.PreviousContentHash,
                        StringComparison.Ordinal))
                {
                    backupProblems.Add(file.RelativeGamePath);
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                ArgumentException)
            {
                backupProblems.Add(file.RelativeGamePath);
            }
        }
        if (backupProblems.Count > 0)
        {
            return Invalid(
                mod,
                "RemovalBackupUnavailable",
                problemPaths: backupProblems,
                eligiblePaths: eligibleModifiedPaths,
                ineligiblePaths: backupProblems);
        }

        return new ValidatedRemoval(
            new ModRemovalPlan
            {
                PackageId = mod.Package.PackageId,
                FilesToDelete = removalFiles.Count(
                    item => !item.PreviousFileExisted),
                FilesToRestore = removalFiles.Count(
                    item => item.PreviousFileExisted),
                LayersToDetach = files.Count - removalFiles.Count,
                EligibleModifiedPaths = eligibleModifiedPaths
            },
            removalFiles);
    }

    private static async Task<string> ComputeLiveFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        OperationPerformanceDiagnostics.AddCounter("live_file_hash_count");
        ObserveFileBytes(path, "live_file_bytes_hashed");
        using var timing = OperationPerformanceDiagnostics.MeasureMetric(
            "LIVE_FILE_HASH",
            "live_file_hash");
        return await ContentStoreService.ComputeHashAsync(
            path,
            cancellationToken);
    }

    private static async Task<string> ComputeContentStoreHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        OperationPerformanceDiagnostics.AddCounter(
            "content_store_verify_count");
        OperationPerformanceDiagnostics.AddCounter(
            "content_store_read_count");
        ObserveFileBytes(path, "content_store_bytes_read");
        using var timing = OperationPerformanceDiagnostics.MeasureMetric(
            "CONTENT_STORE_VERIFY",
            "content_store_verify");
        return await ContentStoreService.ComputeHashAsync(
            path,
            cancellationToken);
    }

    private static void ObserveFileBytes(string path, string counter)
    {
        if (!OperationPerformanceDiagnostics.IsEnabled)
            return;
        try
        {
            OperationPerformanceDiagnostics.AddCounter(
                counter,
                new FileInfo(path).Length);
        }
        catch
        {
            // The authoritative operation owns any real I/O failure.
        }
    }

    private static void DeleteEmptyParents(
        string directory,
        string gameRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(gameRoot));
        var current = Path.GetFullPath(directory);
        while (!string.Equals(
                   current,
                   root,
                   StringComparison.OrdinalIgnoreCase) &&
               current.StartsWith(
                   root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(
                   Path.GetDirectoryName(current),
                   root,
                   StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(current) &&
               (File.GetAttributes(current) &
                FileAttributes.ReparsePoint) == 0 &&
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

    private static ValidatedRemoval Invalid(
        OrganizerPackageRecord mod,
        string errorCode,
        string? errorDetail = null,
        IReadOnlyList<string>? problemPaths = null,
        IReadOnlyList<string>? eligiblePaths = null,
        IReadOnlyList<ModifiedFileConfirmationIdentity>? eligibleIdentities = null,
        IReadOnlyList<string>? ineligiblePaths = null) =>
        new(
            new ModRemovalPlan
            {
                PackageId = mod.Package.PackageId,
                ErrorCode = errorCode,
                ErrorDetail = errorDetail,
                ProblemPaths = problemPaths ?? [],
                EligibleModifiedPaths = eligiblePaths ?? [],
                EligibleModifiedIdentities = eligibleIdentities ?? [],
                IneligibleProblemPaths = ineligiblePaths ?? []
            },
            []);

    private static ModRemovalResult Failure(
        string errorCode,
        string? errorMessage = null,
        Guid? operationId = null,
        InstallOperationStatus status = InstallOperationStatus.Blocked,
        PackageInstallationState installationState =
            PackageInstallationState.Installed,
        IReadOnlyList<string>? problemPaths = null) =>
        new()
        {
            OperationId = operationId,
            Status = status,
            InstallationState = installationState,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ProblemPaths = problemPaths ?? []
        };

    private sealed record ValidatedRemoval(
        ModRemovalPlan Plan,
        IReadOnlyList<InstalledFileRecord> Files);
}
