using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class ModRemovalService
{
    private readonly OrganizerRepository _repository;
    private readonly GameProfileService _profileService;
    private readonly ContentStoreService _contentStore;
    private readonly string _stagingRoot;
    private readonly Func<int, bool>? _failureInjector;
    private readonly Action<Exception>? _exceptionLogger;
    private readonly SemaphoreSlim _removalGate = new(1, 1);
    internal IApplicationMutationRoute? ApplicationMutationRoute { get; set; }

    public ModRemovalService(
        OrganizerRepository repository,
        GameProfileService profileService,
        ContentStoreService contentStore,
        string? stagingRoot = null,
        Func<int, bool>? failureInjector = null,
        Action<Exception>? exceptionLogger = null)
    {
        _repository = repository;
        _profileService = profileService;
        _contentStore = contentStore;
        _stagingRoot = Path.GetFullPath(stagingRoot ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "RipperWorks",
            "Staging"));
        _failureInjector = failureInjector;
        _exceptionLogger = exceptionLogger;
    }

    public async Task<ModRemovalPlan> BuildPreflightPlanAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        using var context = OperationPerformanceDiagnostics.UseContext(
            OperationPerformanceContext.RemovePreflight);
        using var total = OperationPerformanceDiagnostics.MeasureMetric(
            "TOTAL");
        if (ApplicationMutationRoute is not null)
        {
            return await ApplicationMutationRoute.PrepareRemovalAsync(
                mod,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles);
        }
        return await BuildPrimitivePreflightPlanAsync(
            mod,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles);
    }

    internal async Task<ModRemovalPlan> BuildPrimitivePreflightPlanAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var metric = OperationPerformanceDiagnostics.CurrentContext ==
            OperationPerformanceContext.RemovePreflight
                ? "PLAN_BUILD"
                : "REPLAN";
        using var planTiming = OperationPerformanceDiagnostics.MeasureMetric(
            metric,
            "removal_plan_build");
        return (await ValidateAsync(
                mod,
                profile,
                libraryRoot,
                cancellationToken,
                ignoredOperationId: null,
                authorizedModifiedFiles: authorizedModifiedFiles))
            .Plan;
    }

    public async Task<ModRemovalResult> RemoveAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        using var context = OperationPerformanceDiagnostics.UseContext(
            OperationPerformanceContext.Remove);
        using var total = OperationPerformanceDiagnostics.MeasureMetric(
            "TOTAL");
        if (ApplicationMutationRoute is not null)
        {
            return await ApplicationMutationRoute.ExecuteRemovalAsync(
                mod,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles);
        }
        return await RemovePrimitiveAsync(
            mod,
            profile,
            libraryRoot,
            cancellationToken,
            ignoredOperationId: null,
            authorizedModifiedFiles: authorizedModifiedFiles);
    }

    internal async Task<ModRemovalResult> RemovePrimitiveAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default,
        Guid? ignoredOperationId = null,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (!await _removalGate.WaitAsync(0, cancellationToken))
            return Failure("RemovalAlreadyRunning");

        Guid? operationId = null;
        string? operationStaging = null;
        var fileChangesStarted = false;
        var preserveStaging = false;
        try
        {
            ValidatedRemoval validation;
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "REPLAN",
                       "removal_plan_build"))
            {
                validation = await ValidateAsync(
                    mod,
                    profile,
                    libraryRoot,
                    cancellationToken,
                    ignoredOperationId,
                    authorizedModifiedFiles: authorizedModifiedFiles);
            }
            if (!validation.Plan.CanRemove)
            {
                return Failure(
                    validation.Plan.ErrorCode ?? "RemovalBlocked",
                    validation.Plan.ErrorDetail,
                    problemPaths: validation.Plan.ProblemPaths);
            }

            operationId = Guid.NewGuid();
            operationStaging = Path.Combine(
                _stagingRoot,
                operationId.Value.ToString("N"));
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.CreateRemoveOperationAsync(
                    operationId.Value,
                    mod.Package.PackageId,
                    cancellationToken));
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.Staging,
                    cancellationToken: cancellationToken));
            Dictionary<string, string> liveStagedHashes;
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "ROLLBACK_STAGING"))
            {
                liveStagedHashes = await StageInstalledFilesAsync(
                    mod,
                    profile!.GameRoot,
                    operationStaging,
                    validation.Files,
                    cancellationToken,
                    authorizedModifiedFiles: authorizedModifiedFiles);
            }
            var operationFiles = validation.Files.Select(file =>
                liveStagedHashes.TryGetValue(file.RelativeGamePath, out var stagedHash)
                    ? file with { InstalledContentHash = stagedHash }
                    : file).ToList();
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.SaveRemovalOperationFilesAsync(
                    operationId.Value,
                    operationFiles,
                    cancellationToken));

            using var mutationTiming =
                OperationPerformanceDiagnostics.MeasureMetric("MUTATION");
            var processed = 0;
            foreach (var file in validation.Files
                         .OrderByDescending(item => item.Sequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileChangesStarted = true;
                var expectedLiveHash = liveStagedHashes.TryGetValue(file.RelativeGamePath, out var stagedHash)
                    ? stagedHash
                    : file.InstalledContentHash;
                await RemoveFileAsync(
                    mod,
                    operationId.Value,
                    profile.GameRoot,
                    file,
                    expectedLiveHash,
                    cancellationToken);
                await OperationPerformanceDiagnostics.MeasureDatabaseAsync(
                    () => _repository.MarkInstallOperationFileAppliedAsync(
                        operationId.Value,
                        file.Sequence,
                        cancellationToken));
                processed++;
                if (_failureInjector?.Invoke(processed) == true)
                    throw new IOException("Injected removal failure.");
            }

            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.Finalizing,
                    cancellationToken: cancellationToken));
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.CompleteRemovalAsync(
                    operationId.Value,
                    mod.Package.PackageId,
                    cancellationToken));
            TryDeleteOwnedStaging(operationStaging);
            return new ModRemovalResult
            {
                Success = true,
                OperationId = operationId,
                Status = InstallOperationStatus.Completed,
                InstallationState = PackageInstallationState.NotInstalled
            };
        }
        catch (Exception exception) when (
            exception is not StackOverflowException &&
            exception is not OutOfMemoryException)
        {
            _exceptionLogger?.Invoke(exception);
            if (operationId is null)
            {
                return Failure(
                    exception is OperationCanceledException
                        ? "RemovalCanceled"
                        : "RemovalFailed",
                    exception.Message);
            }

            if (!fileChangesStarted)
            {
                await _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.Staging,
                    InstallOperationStatus.Blocked,
                    exception.ToString(),
                    completed: true,
                    cancellationToken: CancellationToken.None);
                if (operationStaging is not null)
                    TryDeleteOwnedStaging(operationStaging);
                return Failure(
                    exception is OperationCanceledException
                        ? "RemovalCanceled"
                        : "RemovalFailed",
                    exception.Message,
                    operationId);
            }

            var rollbackErrors = await RollbackAsync(
                operationId.Value,
                profile!.GameRoot,
                operationStaging!,
                CancellationToken.None);
            var recoveryRequired = rollbackErrors.Count > 0;
            preserveStaging = recoveryRequired;
            var error = recoveryRequired
                ? string.Join(
                    Environment.NewLine,
                    new[] { exception.ToString() }.Concat(rollbackErrors))
                : exception.ToString();
            await _repository.CompleteRemovalRollbackAsync(
                operationId.Value,
                mod.Package.PackageId,
                recoveryRequired,
                error,
                CancellationToken.None);
            if (!preserveStaging)
                TryDeleteOwnedStaging(operationStaging!);
            return Failure(
                recoveryRequired
                    ? "RemovalRecoveryRequired"
                    : exception is OperationCanceledException
                        ? "RemovalCanceledRolledBack"
                        : "RemovalFailedRolledBack",
                exception.Message,
                operationId,
                recoveryRequired
                    ? InstallOperationStatus.RecoveryRequired
                    : InstallOperationStatus.RolledBack,
                recoveryRequired
                    ? PackageInstallationState.PartiallyInstalled
                    : PackageInstallationState.Installed);
        }
        finally
        {
            _removalGate.Release();
        }
    }


    private async Task<Dictionary<string, string>> StageInstalledFilesAsync(
        OrganizerPackageRecord mod,
        string gameRoot,
        string operationStaging,
        IReadOnlyList<InstalledFileRecord> files,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        var liveHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(_stagingRoot);
        ArchivePathSafety.EnsureNoReparsePoints(_stagingRoot, _stagingRoot);
        ArchivePathSafety.CreateDirectoriesWithoutReparse(
            _stagingRoot, operationStaging);
        foreach (var file in files.OrderBy(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ArchivePathSafety.ResolveSafeGamePath(
                gameRoot,
                file.RelativeGamePath);
            ArchivePathSafety.EnsureNoReparsePoints(
                gameRoot,
                Path.GetDirectoryName(source)!);

            if (!File.Exists(source))
            {
                throw new IOException(
                    $"Installed file changed before staging: {file.RelativeGamePath}");
            }

            var liveHash = await ComputeLiveFileHashAsync(source, cancellationToken);
            var isAuthorized = authorizedModifiedFiles?.IsAuthorized(
                mod.Package.PackageId,
                file.RelativeGamePath,
                liveHash) == true;
            if (!string.Equals(liveHash, file.InstalledContentHash, StringComparison.Ordinal) && !isAuthorized)
            {
                throw new IOException(
                    $"Installed file changed before staging: {file.RelativeGamePath}");
            }

            liveHashes[file.RelativeGamePath] = liveHash;

            var staged = ArchivePathSafety.ResolveSafeGamePath(
                operationStaging,
                file.RelativeGamePath);
            ArchivePathSafety.CreateDirectoriesWithoutReparse(
                operationStaging,
                Path.GetDirectoryName(staged)!);
            await CopyVerifiedAsync(
                source,
                staged,
                liveHash,
                cancellationToken);
            OperationPerformanceDiagnostics.AddCounter("staging_file_count");
            ObserveFileBytes(source, "staging_bytes_written");
        }
        return liveHashes;
    }

    private async Task RemoveFileAsync(
        OrganizerPackageRecord mod,
        Guid operationId,
        string gameRoot,
        InstalledFileRecord file,
        string expectedLiveHash,
        CancellationToken cancellationToken)
    {
        var destination = ArchivePathSafety.ResolveSafeGamePath(
            gameRoot,
            file.RelativeGamePath);
        ArchivePathSafety.EnsureNoReparsePoints(
            gameRoot,
            Path.GetDirectoryName(destination)!);

        if (!File.Exists(destination))
        {
            throw new IOException(
                $"Installed file changed before removal: {file.RelativeGamePath}");
        }

        var liveHash = await ComputeLiveFileHashAsync(
            destination,
            cancellationToken);
        if (!string.Equals(liveHash, expectedLiveHash, StringComparison.Ordinal))
        {
            throw new IOException(
                $"Installed file changed before removal: {file.RelativeGamePath}");
        }

        if (file.PreviousFileExisted)
        {
            var previousHash = file.PreviousContentHash ??
                throw new InvalidDataException(
                    $"Previous content hash is missing: {file.RelativeGamePath}");
            var temporary = destination +
                $".ripperworks-remove-{operationId:N}.tmp";
            try
            {
                using (OperationPerformanceDiagnostics.MeasureMetric(
                           "CONTENT_RESTORE"))
                {
                    await _contentStore.CopyVerifiedAsync(
                        previousHash,
                        temporary,
                        cancellationToken);
                }
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        else
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            DeleteEmptyParents(
                Path.GetDirectoryName(destination)!,
                gameRoot);
        }
    }

    private async Task<IReadOnlyList<string>> RollbackAsync(
        Guid operationId,
        string gameRoot,
        string operationStaging,
        CancellationToken rollbackToken)
    {
        var errors = new List<string>();
        await _repository.UpdateInstallOperationAsync(
            operationId,
            InstallOperationPhase.RollingBack,
            cancellationToken: rollbackToken);
        var files = await _repository.LoadInstallOperationFilesAsync(
            operationId,
            rollbackToken);
        foreach (var file in files.OrderBy(item => item.Sequence))
        {
            try
            {
                var destination = ArchivePathSafety.ResolveSafeGamePath(
                    gameRoot,
                    file.RelativeGamePath);
                if (File.Exists(destination))
                {
                    var currentHash = await ContentStoreService.ComputeHashAsync(
                        destination,
                        rollbackToken);
                    if (string.Equals(currentHash, file.NewContentHash, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var isExpectedPostRemovalState = file.PreviousFileExisted &&
                        string.Equals(currentHash, file.PreviousContentHash, StringComparison.Ordinal);
                    if (!isExpectedPostRemovalState)
                    {
                        throw new InvalidDataException(
                            $"Concurrent external modification detected during rollback: {file.RelativeGamePath}");
                    }
                }

                var staged = ArchivePathSafety.ResolveSafeGamePath(
                    operationStaging,
                    file.RelativeGamePath);
                if (!File.Exists(staged) ||
                    !string.Equals(
                        await ContentStoreService.ComputeHashAsync(
                            staged,
                            rollbackToken),
                        file.NewContentHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Removal staging file is missing or corrupt.");
                }
                ArchivePathSafety.CreateDirectoriesWithoutReparse(
                    gameRoot,
                    Path.GetDirectoryName(destination)!);
                var temporary = destination +
                    $".ripperworks-remove-rollback-{operationId:N}.tmp";
                try
                {
                    await CopyVerifiedAsync(
                        staged,
                        temporary,
                        file.NewContentHash,
                        rollbackToken);
                    File.Move(temporary, destination, true);
                    var restoredHash =
                        await ContentStoreService.ComputeHashAsync(
                            destination,
                            rollbackToken);
                    if (!string.Equals(
                            restoredHash,
                            file.NewContentHash,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Removal rollback failed SHA-256 verification.");
                    }
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{file.RelativeGamePath}: {exception}");
            }
        }
        return errors;
    }

    private static async Task CopyVerifiedAsync(
        string sourcePath,
        string destinationPath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using (var source = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
        var copiedHash = await ContentStoreService.ComputeHashAsync(
            destinationPath,
            cancellationToken);
        if (!string.Equals(
                expectedHash,
                copiedHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Removal staging copy failed SHA-256 verification.");
        }
    }

}
