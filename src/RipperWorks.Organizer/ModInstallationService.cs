using RipperWorks.Core;
using SharpCompress.Archives;

namespace RipperWorks.Organizer;

public sealed partial class ModInstallationService
{
    private readonly OrganizerRepository _repository;
    private readonly InstallPlanService _installPlanService;
    private readonly GameProfileService _profileService;
    private readonly ContentStoreService _contentStore;
    private readonly string _stagingRoot;
    private readonly Func<int, bool>? _failureInjector;
    private readonly Action<Exception>? _exceptionLogger;
    private readonly ArchiveResourcePolicy _archiveResourcePolicy;
    private readonly TimeProvider _archiveTimeProvider;
    private readonly SemaphoreSlim _installationGate = new(1, 1);
    internal IApplicationMutationRoute? ApplicationMutationRoute { get; set; }
    internal ContentStoreService RecoveryContentStore => _contentStore;
    internal string RecoveryStagingRoot => _stagingRoot;
    internal GameProfileService RecoveryProfileService => _profileService;

    /// <summary>
    /// Test-only: after the internal first <see cref="InstallPlanService.BuildAsync"/>
    /// and before <see cref="ExtractToStagingAsync"/>.
    /// </summary>
    internal Func<OrganizerPackageRecord, InstallPlan, CancellationToken, Task>?
        TestOnlyAfterPlanBeforeExtract { get; set; }

    /// <summary>
    /// Test-only: after extraction finished, before second plan/trust and
    /// first game-root mutation. The string list is relative game paths already staged.
    /// </summary>
    internal Func<OrganizerPackageRecord, InstallPlan, string, IReadOnlyList<string>, CancellationToken, Task>?
        TestOnlyAfterExtractBeforeMutation { get; set; }

    internal Action? TestOnlyAfterArchiveCopyChunk { get; set; }

    /// <summary>
    /// Test-only: invoked after each completed staging SHA chunk.
    /// </summary>
    internal Action? TestOnlyAfterStagingHashChunk { get; set; }

    public ModInstallationService(
        OrganizerRepository repository,
        InstallPlanService installPlanService,
        GameProfileService profileService,
        ContentStoreService contentStore,
        string? stagingRoot = null,
        Func<int, bool>? failureInjector = null,
        Action<Exception>? exceptionLogger = null)
        : this(
            repository,
            installPlanService,
            profileService,
            contentStore,
            stagingRoot,
            ArchiveResourcePolicy.Production,
            TimeProvider.System,
            failureInjector,
            exceptionLogger)
    {
    }

    internal ModInstallationService(
        OrganizerRepository repository,
        InstallPlanService installPlanService,
        GameProfileService profileService,
        ContentStoreService contentStore,
        string? stagingRoot,
        ArchiveResourcePolicy archiveResourcePolicy,
        TimeProvider archiveTimeProvider,
        Func<int, bool>? failureInjector = null,
        Action<Exception>? exceptionLogger = null)
    {
        _repository = repository;
        _installPlanService = installPlanService;
        _profileService = profileService;
        _contentStore = contentStore;
        _stagingRoot = Path.GetFullPath(stagingRoot ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "RipperWorks",
            "Staging"));
        _failureInjector = failureInjector;
        _exceptionLogger = exceptionLogger;
        _archiveResourcePolicy = archiveResourcePolicy ??
            throw new ArgumentNullException(nameof(archiveResourcePolicy));
        _archiveTimeProvider = archiveTimeProvider ??
            throw new ArgumentNullException(nameof(archiveTimeProvider));
    }

    public async Task<ModInstallationResult> InstallAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        IProgress<ModInstallationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var context = OperationPerformanceDiagnostics.UseContext(
            OperationPerformanceContext.Install);
        using var total = OperationPerformanceDiagnostics.MeasureMetric(
            "TOTAL");
        if (ApplicationMutationRoute is not null)
        {
            return await ApplicationMutationRoute.ExecuteInstallAsync(
                mod,
                profile,
                libraryRoot,
                progress,
                cancellationToken);
        }
        return await InstallPrimitiveAsync(
            mod,
            profile,
            libraryRoot,
            progress,
            cancellationToken);
    }

    internal async Task<ModInstallationResult> InstallPrimitiveAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        IProgress<ModInstallationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Guid? ignoredOperationId = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (!await _installationGate.WaitAsync(0, cancellationToken))
            return Failure("InstallationAlreadyRunning");

        Guid? operationId = null;
        string? operationStaging = null;
        var deploymentStarted = false;
        var preserveStaging = false;
        try
        {
            ModInstallationResult? precondition;
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "PRECONDITIONS"))
            {
                precondition = await ValidatePreconditionsAsync(
                    mod,
                    profile,
                    libraryRoot,
                    cancellationToken,
                    projectedRemovalPackageId: null,
                    ignoredOperationId);
            }
            if (precondition is not null)
                return precondition;

            InstallPlan freshPlan;
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "INITIAL_PLAN",
                       "install_plan_build"))
            {
                freshPlan = await _installPlanService.BuildAsync(
                    mod,
                    profile,
                    cancellationToken);
            }
            if (!CanInstall(freshPlan))
                return PlanFailure(freshPlan);

            if (TestOnlyAfterPlanBeforeExtract is not null)
            {
                await TestOnlyAfterPlanBeforeExtract(
                        mod,
                        freshPlan,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            operationId = Guid.NewGuid();
            operationStaging = Path.Combine(
                _stagingRoot,
                operationId.Value.ToString("N"));
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.CreateInstallOperationAsync(
                    operationId.Value,
                    mod.Package.PackageId,
                    cancellationToken));
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.Staging,
                    cancellationToken: cancellationToken));

            progress?.Report(new ModInstallationProgress(
                ModInstallationProgressPhase.PreparingArchive,
                0,
                freshPlan.Entries.Count));
            IReadOnlyList<StagedFile> staged;
            try
            {
                using (OperationPerformanceDiagnostics.MeasureMetric(
                           "EXTRACTION"))
                {
                    staged = await ExtractToStagingAsync(
                        mod,
                        freshPlan,
                        operationStaging,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ArchiveResourceBudgetException exception)
            {
                if (operationStaging is not null)
                    TryDeleteOwnedStaging(operationStaging);
                await _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.Staging,
                    InstallOperationStatus.Blocked,
                    exception.Code,
                    completed: true,
                    cancellationToken: CancellationToken.None);
                return PlanFailure(
                    freshPlan with
                    {
                        ErrorCode = exception.Code,
                        ErrorDetail = exception.Message
                    },
                    operationId);
            }
            catch (Exception exception) when (
                (exception is InvalidDataException or InvalidOperationException) &&
                (exception.Message.Contains("Archive content", StringComparison.Ordinal) ||
                 exception.Message.Contains("ExpectedArchiveSha256", StringComparison.Ordinal) ||
                 exception.Message.Contains("content identity", StringComparison.OrdinalIgnoreCase) ||
                 exception.Message.Contains("content lease", StringComparison.OrdinalIgnoreCase) ||
                 exception.Message.Contains("identity mismatch", StringComparison.OrdinalIgnoreCase) ||
                 exception.Message.Contains("lease refused", StringComparison.OrdinalIgnoreCase)))
            {
                // No staging mutation on identity mismatch (or fully cleaned).
                if (operationStaging is not null)
                    TryDeleteOwnedStaging(operationStaging);
                await _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.Staging,
                    InstallOperationStatus.Blocked,
                    "AnalysisStale",
                    completed: true,
                    cancellationToken: CancellationToken.None);
                return PlanFailure(
                    freshPlan with
                    {
                        ErrorCode = "AnalysisStale",
                        ErrorDetail = "Archive content identity mismatch."
                    },
                    operationId);
            }

            if (TestOnlyAfterExtractBeforeMutation is not null)
            {
                await TestOnlyAfterExtractBeforeMutation(
                        mod,
                        freshPlan,
                        operationStaging,
                        staged.Select(static s => s.RelativeGamePath).ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Ownership and destination state are read again after extraction,
            // immediately before any game file can be changed.
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "POST_EXTRACTION_PLAN",
                       "install_plan_build"))
            {
                freshPlan = await _installPlanService.BuildAsync(
                    mod,
                    profile,
                    cancellationToken);
            }
            if (!CanInstall(freshPlan))
            {
                TryDeleteOwnedStaging(operationStaging);
                await _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.Staging,
                    InstallOperationStatus.Blocked,
                    freshPlan.ErrorCode ?? "Install plan is blocked.",
                    completed: true,
                    cancellationToken: CancellationToken.None);
                return PlanFailure(freshPlan, operationId);
            }
            ReconcileStaging(freshPlan, staged);

            // RF-05: revalidate archive content identity before first game-root write.
            ArchiveTrustAssessment preMutation;
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "FINAL_TRUST"))
            {
                preMutation = await ArchiveTrustGate.AssessBeforeMutationAsync(
                    freshPlan,
                    mod,
                    cancellationToken).ConfigureAwait(false);
            }
            if (!preMutation.IsTrusted)
            {
                TryDeleteOwnedStaging(operationStaging);
                await _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.Staging,
                    InstallOperationStatus.Blocked,
                    preMutation.Code,
                    completed: true,
                    cancellationToken: CancellationToken.None);
                return PlanFailure(
                    freshPlan with
                    {
                        ErrorCode = preMutation.Code,
                        ErrorDetail = preMutation.Detail
                    },
                    operationId);
            }

            progress?.Report(new ModInstallationProgress(
                ModInstallationProgressPhase.PreservingOriginals,
                0,
                staged.Count));
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.BackingUp,
                    cancellationToken: cancellationToken));
            IReadOnlyList<InstalledFileRecord> manifest;
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "ROLLBACK_PREPARATION"))
            {
                manifest = await BuildManifestAndPreserveOriginalsAsync(
                    mod,
                    profile!,
                    staged,
                    progress,
                    cancellationToken);
            }
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.SavePlannedInstallManifestAsync(
                    operationId.Value,
                    mod,
                    manifest,
                    cancellationToken));

            using var mutationTiming =
                OperationPerformanceDiagnostics.MeasureMetric("MUTATION");
            deploymentStarted = true;
            for (var index = 0; index < staged.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedFile = staged[index];
                var plannedFile = manifest[index];
                await DeployFileAsync(
                    operationId.Value,
                    profile!.GameRoot,
                    stagedFile,
                    plannedFile,
                    cancellationToken);
                await OperationPerformanceDiagnostics.MeasureDatabaseAsync(
                    () => _repository.MarkInstallOperationFileAppliedAsync(
                        operationId.Value,
                        plannedFile.Sequence,
                        cancellationToken));
                progress?.Report(new ModInstallationProgress(
                    ModInstallationProgressPhase.Installing,
                    index + 1,
                    staged.Count));
                if (_failureInjector?.Invoke(index + 1) == true)
                    throw new IOException("Injected deployment failure.");
            }

            progress?.Report(new ModInstallationProgress(
                ModInstallationProgressPhase.Finalizing,
                staged.Count,
                staged.Count));
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.UpdateInstallOperationAsync(
                    operationId.Value,
                    InstallOperationPhase.Finalizing,
                    cancellationToken: cancellationToken));
            await OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
                _repository.CompleteInstallAsync(
                    operationId.Value,
                    mod.Package.PackageId,
                    cancellationToken));
            TryDeleteOwnedStaging(operationStaging);
            return new ModInstallationResult
            {
                Success = true,
                OperationId = operationId,
                Status = InstallOperationStatus.Completed,
                InstallationState = PackageInstallationState.Installed,
                Plan = freshPlan
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
                        ? "InstallationCanceled"
                        : "InstallationFailed",
                    exception.Message);
            }

            if (!deploymentStarted)
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
                        ? "InstallationCanceled"
                        : "InstallationFailed",
                    exception.Message,
                    operationId,
                    InstallOperationStatus.Blocked);
            }

            progress?.Report(new ModInstallationProgress(
                ModInstallationProgressPhase.RollingBack,
                0,
                0));
            var rollbackErrors = await RollbackAsync(
                operationId.Value,
                profile!.GameRoot,
                CancellationToken.None);
            var recoveryRequired = rollbackErrors.Count > 0;
            preserveStaging = recoveryRequired;
            var error = recoveryRequired
                ? string.Join(
                    Environment.NewLine,
                    new[] { exception.ToString() }.Concat(rollbackErrors))
                : exception.ToString();
            await _repository.CompleteInstallRollbackAsync(
                operationId.Value,
                mod.Package.PackageId,
                recoveryRequired,
                error,
                CancellationToken.None);
            if (!preserveStaging && operationStaging is not null)
                TryDeleteOwnedStaging(operationStaging);
            return Failure(
                recoveryRequired
                    ? "RecoveryRequired"
                    : exception is OperationCanceledException
                        ? "InstallationCanceledRolledBack"
                        : "InstallationFailedRolledBack",
                exception.Message,
                operationId,
                recoveryRequired
                    ? InstallOperationStatus.RecoveryRequired
                    : InstallOperationStatus.RolledBack,
                recoveryRequired
                    ? PackageInstallationState.PartiallyInstalled
                    : PackageInstallationState.NotInstalled);
        }
        finally
        {
            _installationGate.Release();
        }
    }

    public async Task<InstallPlan> BuildPreflightPlanAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default)
    {
        using var context = OperationPerformanceDiagnostics.UseContext(
            OperationPerformanceContext.InstallPreflight);
        using var total = OperationPerformanceDiagnostics.MeasureMetric(
            "TOTAL");
        if (ApplicationMutationRoute is not null)
        {
            return await ApplicationMutationRoute.PrepareInstallAsync(
                mod,
                profile,
                libraryRoot,
                cancellationToken);
        }
        return await BuildPrimitivePreflightPlanAsync(
            mod,
            profile,
            libraryRoot,
            cancellationToken);
    }

    internal async Task<InstallPlan> BuildPrimitivePreflightPlanAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default)
    {
        ModInstallationResult? precondition;
        using (OperationPerformanceDiagnostics.MeasureMetric(
                   "PRECONDITIONS"))
        {
            precondition = await ValidatePreconditionsAsync(
                mod,
                profile,
                libraryRoot,
                cancellationToken,
                projectedRemovalPackageId: null,
                ignoredOperationId: null);
        }
        if (precondition is not null)
        {
            return new InstallPlan
            {
                PackageId = mod.Package.PackageId,
                ErrorCode = precondition.ErrorCode,
                ErrorDetail = precondition.ErrorMessage
            };
        }
        var metric = OperationPerformanceDiagnostics.CurrentContext ==
            OperationPerformanceContext.InstallPreflight
                ? "PLAN_BUILD"
                : "INITIAL_PLAN";
        using var planTiming = OperationPerformanceDiagnostics.MeasureMetric(
            metric,
            "install_plan_build");
        return await _installPlanService.BuildAsync(
            mod,
            profile,
            cancellationToken);
    }

    internal async Task<InstallPlan> BuildVersionSwitchPreflightPlanAsync(
        OrganizerPackageRecord mod,
        PackageId projectedRemovalPackageId,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default)
    {
        var precondition = await ValidatePreconditionsAsync(
            mod,
            profile,
            libraryRoot,
            cancellationToken,
            projectedRemovalPackageId,
            ignoredOperationId: null);
        if (precondition is not null)
        {
            return new InstallPlan
            {
                PackageId = mod.Package.PackageId,
                ErrorCode = precondition.ErrorCode,
                ErrorDetail = precondition.ErrorMessage
            };
        }
        var trust = await ArchiveTrustGate.AssessForInstallAsync(
            mod,
            cancellationToken).ConfigureAwait(false);
        if (!trust.IsTrusted)
        {
            return new InstallPlan
            {
                PackageId = mod.Package.PackageId,
                ErrorCode = trust.Code,
                ErrorDetail = trust.Detail
            };
        }
        return await _installPlanService.BuildAsync(
            mod,
            profile,
            cancellationToken);
    }


}
