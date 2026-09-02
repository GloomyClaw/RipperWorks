using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed record InstalledPathOwner(
    PackageId PackageId,
    string DisplayName,
    bool AllowsOverlay);

public interface IInstalledPathOwnerProvider
{
    Task<InstalledPathOwner?> FindOwnerAsync(
        string relativeGamePath,
        PackageId requestingPackageId,
        CancellationToken cancellationToken = default);
}

public sealed class EmptyInstalledPathOwnerProvider
    : IInstalledPathOwnerProvider
{
    public Task<InstalledPathOwner?> FindOwnerAsync(
        string relativeGamePath,
        PackageId requestingPackageId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<InstalledPathOwner?>(null);
}

public sealed class OrganizerInstalledPathOwnerProvider(
    OrganizerRepository repository)
    : IInstalledPathOwnerProvider
{
    public Task<InstalledPathOwner?> FindOwnerAsync(
        string relativeGamePath,
        PackageId requestingPackageId,
        CancellationToken cancellationToken = default) =>
        OperationPerformanceDiagnostics.MeasureDatabaseAsync(() =>
            repository.FindInstalledPathOwnerForCoordinatorAsync(
                relativeGamePath,
                requestingPackageId,
                cancellationToken));
}

public sealed class InstallPlanService(
    IInstalledPathOwnerProvider ownerProvider)
{
    private static int _observedBuildAsyncCalls;

    /// <summary>
    /// Observed <see cref="BuildAsync"/> entries (test/diagnostics).
    /// </summary>
    internal static int ObservedBuildAsyncCalls =>
        System.Threading.Volatile.Read(ref _observedBuildAsyncCalls);

    internal static void ResetObservedBuildAsyncCalls() =>
        System.Threading.Interlocked.Exchange(ref _observedBuildAsyncCalls, 0);

    public async Task<InstallPlan> BuildAsync(
        OrganizerPackageRecord mod,
        GameProfileRecord? profile,
        CancellationToken cancellationToken = default)
    {
        System.Threading.Interlocked.Increment(ref _observedBuildAsyncCalls);
        ArgumentNullException.ThrowIfNull(mod);
        if (profile is null ||
            profile.ValidationState != GameProfileValidationState.Valid)
        {
            return Error(mod, "GameProfileMissing");
        }
        if (!mod.IsPresent || !File.Exists(mod.Package.ArchivePath))
            return Error(mod, "ArchiveMissing");
        if (mod.Analysis is null)
            return Error(mod, "AnalysisMissing");

        // Domain trust gate: live content SHA + RF-05 identity (not UI state).
        var trust = await ArchiveTrustGate.AssessForInstallAsync(
            mod,
            cancellationToken).ConfigureAwait(false);
        if (!trust.IsTrusted)
            return Error(mod, trust.Code, trust.Detail);

        if (mod.Analysis.State != PackageAnalysisState.Ready)
            return Error(mod, "AnalysisNotReady");
        if (mod.Analysis.DetectedRoots.Count > 1 &&
            string.IsNullOrWhiteSpace(mod.Analysis.SelectedRoot))
        {
            return Error(mod, "InstallRootNotSelected");
        }

        var identity = trust.Identity!;
        var entries = new List<InstallPlanEntry>();
        foreach (var entry in mod.Analysis.Entries.Where(entry =>
                     entry.IsInstallable &&
                     !entry.IsDirectory &&
                     !string.IsNullOrWhiteSpace(
                         entry.RelativeInstallPath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = entry.RelativeInstallPath!;
            try
            {
                string destination;
                using (OperationPerformanceDiagnostics.MeasureMetric(
                           "PATH_VALIDATION",
                           "install_path_validation"))
                {
                    destination = ArchivePathSafety.ResolveSafeGamePath(
                        profile.GameRoot,
                        relativePath);
                }
                InstalledPathOwner? owner;
                using (OperationPerformanceDiagnostics.MeasureMetric(
                           "OWNERSHIP_LOOKUP",
                           "install_ownership_lookup"))
                {
                    owner = await ownerProvider.FindOwnerAsync(
                        relativePath,
                        mod.Package.PackageId,
                        cancellationToken);
                }
                if (owner is not null)
                {
                    entries.Add(new InstallPlanEntry
                    {
                        RelativeGamePath = relativePath,
                        Action = owner.PackageId == mod.Package.PackageId
                            ? InstallPlanAction.AlreadyInstalled
                            : owner.AllowsOverlay
                                ? InstallPlanAction.OverlayMod
                            : InstallPlanAction.Conflict,
                        Reason = owner.DisplayName,
                        OwnerPackageId = owner.PackageId,
                        OwnerDisplayName = owner.DisplayName
                    });
                }
                else if (Directory.Exists(destination))
                {
                    entries.Add(new InstallPlanEntry
                    {
                        RelativeGamePath = relativePath,
                        Action = InstallPlanAction.Blocked,
                        Reason = "DestinationIsDirectory"
                    });
                }
                else
                {
                    entries.Add(new InstallPlanEntry
                    {
                        RelativeGamePath = relativePath,
                        Action = File.Exists(destination)
                            ? InstallPlanAction.ReplaceExisting
                            : InstallPlanAction.Add,
                        Reason = File.Exists(destination)
                            ? "ExistingGameFile"
                            : "NewFile"
                    });
                }
            }
            catch (Exception exception) when (
                exception is InvalidDataException or
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                entries.Add(new InstallPlanEntry
                {
                    RelativeGamePath = relativePath,
                    Action = InstallPlanAction.Blocked,
                    Reason = exception.Message
                });
            }
        }

        return new InstallPlan
        {
            PackageId = mod.Package.PackageId,
            Entries = entries,
            ExpectedArchiveSha256 = identity.ArchiveSha256,
            ExpectedAnalyzerVersion = identity.AnalyzerVersion,
            ExpectedPolicyVersion = identity.PolicyVersion,
            ExpectedSelectedRoot = identity.SelectedRootNormalized
        };
    }

    private static InstallPlan Error(
        OrganizerPackageRecord mod,
        string errorCode,
        string? detail = null) =>
        new()
        {
            PackageId = mod.Package.PackageId,
            ErrorCode = errorCode,
            ErrorDetail = detail
        };
}
