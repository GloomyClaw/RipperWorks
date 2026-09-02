using RipperWorks.Core;

namespace RipperWorks.Organizer;

public interface IBatchInstallCoordinator
{
    event EventHandler<BatchPackageStateChanged>? PackageStateChanged;

    Task<BatchInstallPlan> PrepareAsync(
        IReadOnlyList<BatchPackageCandidate> candidates,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default);

    Task<BatchOperationResult> ExecuteAsync(
        BatchInstallPlan plan,
        GameProfileRecord? profile,
        string? libraryRoot,
        IProgress<BatchOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IBatchRemovalCoordinator
{
    event EventHandler<BatchPackageStateChanged>? PackageStateChanged;

    Task<IReadOnlyList<BatchPackageCandidate>> ExpandRelatedAsync(
        IReadOnlyList<BatchPackageCandidate> selected,
        IReadOnlyList<BatchPackageCandidate> library,
        CancellationToken cancellationToken = default);

    Task<BatchRemovalPlan> PrepareAsync(
        IReadOnlyList<BatchPackageCandidate> candidates,
        IReadOnlyList<BatchPackageCandidate> library,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default);

    Task<BatchRemovalPlan> PrepareAsync(
        IReadOnlyList<BatchPackageCandidate> candidates,
        IReadOnlyList<BatchPackageCandidate> library,
        GameProfileRecord? profile,
        string? libraryRoot,
        ModifiedFilesAuthorization? authorizedModifiedFiles,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(
            candidates,
            library,
            profile,
            libraryRoot,
            cancellationToken);

    Task<BatchOperationResult> ExecuteAsync(
        BatchRemovalPlan plan,
        GameProfileRecord? profile,
        string? libraryRoot,
        IProgress<BatchOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal interface IApplicationMutationRoute
{
    Task<InstallPlan> PrepareInstallAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken);

    Task<ModInstallationResult> ExecuteInstallAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        IProgress<ModInstallationProgress>? progress,
        CancellationToken cancellationToken);

    Task<ModRemovalPlan> PrepareRemovalAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken);

    Task<ModRemovalPlan> PrepareRemovalAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles) =>
        PrepareRemovalAsync(package, profile, libraryRoot, cancellationToken);

    Task<ModRemovalResult> ExecuteRemovalAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken);

    Task<ModRemovalResult> ExecuteRemovalAsync(
        OrganizerPackageRecord package,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles) =>
        ExecuteRemovalAsync(package, profile, libraryRoot, cancellationToken);

    Task<RelationMutationResult> ConfirmRelationAsync(
        PackageId childPackageId,
        PackageId parentPackageId,
        PackageRelationType relationType,
        PackageRelationSource source,
        bool isConfirmed,
        CancellationToken cancellationToken);

    Task<ModArchiveSwitchPlan> PrepareSwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken);

    Task<ModArchiveSwitchPlan> PrepareSwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles) =>
        PrepareSwitchAsync(installedArchive, targetArchive, profile, libraryRoot, cancellationToken);

    Task<ModArchiveSwitchResult> ExecuteSwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken);

    Task<ModArchiveSwitchResult> ExecuteSwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles) =>
        ExecuteSwitchAsync(installedArchive, targetArchive, profile, libraryRoot, cancellationToken);
}
