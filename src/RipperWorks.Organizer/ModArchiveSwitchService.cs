using RipperWorks.Core;

namespace RipperWorks.Organizer;

public interface IModArchiveSwitchService
{
    Task<ModArchiveSwitchPlan> BuildPlanAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        CancellationToken cancellationToken = default);

    Task<ModArchiveSwitchPlan> BuildPlanAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles) =>
        BuildPlanAsync(installedArchive, targetArchive, cancellationToken);

    Task<ModArchiveSwitchPlan> BuildPlanAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        ModifiedFilesAuthorization? authorizedModifiedFiles,
        CancellationToken cancellationToken = default) =>
        BuildPlanAsync(installedArchive, targetArchive, cancellationToken, authorizedModifiedFiles);

    Task<ModArchiveSwitchResult> SwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default);

    Task<ModArchiveSwitchResult> SwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles) =>
        SwitchAsync(installedArchive, targetArchive, profile, libraryRoot, cancellationToken);

    Task<ModArchiveSwitchResult> SwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        ModifiedFilesAuthorization? authorizedModifiedFiles,
        CancellationToken cancellationToken = default) =>
        SwitchAsync(installedArchive, targetArchive, profile, libraryRoot, cancellationToken, authorizedModifiedFiles);
}

internal enum VersionSwitchFaultPoint
{
    AfterSourceRemoval,
    BeforeTargetInstall,
    BeforeSourceRestore
}

internal enum VersionSwitchExecutionPoint
{
    BeforeSourceRemoval,
    AfterSourceRemoval,
    BeforeTargetInstall,
    AfterTargetInstall,
    BeforeSourceRestore
}

public sealed class ModArchiveSwitchService : IModArchiveSwitchService
{
    private readonly Func<CancellationToken, Task<GameProfileRecord?>>
        _profileProvider;
    private readonly Func<OrganizerPackageRecord, string?> _libraryRootProvider;
    private readonly Func<VersionSwitchFaultPoint, bool>? _failureInjector;
    private readonly UnifiedModMutationCoordinator? _ownedCoordinator;

    internal IApplicationMutationRoute? ApplicationMutationRoute { get; set; }

    internal Func<VersionSwitchExecutionPoint, CancellationToken, Task>?
        TestOnlyExecutionHook { get; set; }

    public ModArchiveSwitchService(
        Func<GameProfileRecord?> profileProvider,
        Func<string?> libraryRootProvider)
        : this(
            _ => Task.FromResult(profileProvider()),
            _ => libraryRootProvider(),
            failureInjector: null)
    {
    }

    internal ModArchiveSwitchService(
        Func<GameProfileRecord?> profileProvider,
        Func<string?> libraryRootProvider,
        Func<VersionSwitchFaultPoint, bool>? failureInjector)
        : this(
            _ => Task.FromResult(profileProvider()),
            _ => libraryRootProvider(),
            failureInjector)
    {
    }

    public ModArchiveSwitchService(
        OrganizerRepository repository,
        ModInstallationService installation,
        ModRemovalService removal,
        Func<bool>? failureInjector = null)
        : this(
            token => repository.LoadGameProfileAsync(token),
            source => Path.GetDirectoryName(source.Package.ArchivePath),
            point => point == VersionSwitchFaultPoint.BeforeTargetInstall &&
                failureInjector?.Invoke() == true)
    {
        _ownedCoordinator = UnifiedModMutationCoordinator
            .CreateVersionSwitchCompatibilityRoute(
            installation,
            removal,
            new PackageRelationService(repository),
            repository,
            this);
    }

    private ModArchiveSwitchService(
        Func<CancellationToken, Task<GameProfileRecord?>> profileProvider,
        Func<OrganizerPackageRecord, string?> libraryRootProvider,
        Func<VersionSwitchFaultPoint, bool>? failureInjector)
    {
        _profileProvider = profileProvider;
        _libraryRootProvider = libraryRootProvider;
        _failureInjector = failureInjector;
    }

    public Task<ModArchiveSwitchPlan> BuildPlanAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        CancellationToken cancellationToken = default) =>
        BuildPlanAsync(installedArchive, targetArchive, cancellationToken, authorizedModifiedFiles: null);

    public async Task<ModArchiveSwitchPlan> BuildPlanAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles)
    {
        if (ApplicationMutationRoute is null)
        {
            return BlockedPlan(
                installedArchive,
                targetArchive,
                "ArchiveSwitchCoordinatorRequired");
        }
        return await ApplicationMutationRoute.PrepareSwitchAsync(
            installedArchive,
            targetArchive,
            await _profileProvider(cancellationToken),
            _libraryRootProvider(installedArchive),
            cancellationToken,
            authorizedModifiedFiles);
    }

    public Task<ModArchiveSwitchPlan> BuildPlanAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        ModifiedFilesAuthorization? authorizedModifiedFiles,
        CancellationToken cancellationToken = default) =>
        BuildPlanAsync(installedArchive, targetArchive, cancellationToken, authorizedModifiedFiles);

    public Task<ModArchiveSwitchResult> SwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        ModifiedFilesAuthorization? authorizedModifiedFiles,
        CancellationToken cancellationToken = default) =>
        SwitchAsync(installedArchive, targetArchive, profile, libraryRoot, cancellationToken, authorizedModifiedFiles);

    public Task<ModArchiveSwitchResult> SwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default) =>
        SwitchAsync(installedArchive, targetArchive, profile, libraryRoot, cancellationToken, authorizedModifiedFiles: null);

    public async Task<ModArchiveSwitchResult> SwitchAsync(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken,
        ModifiedFilesAuthorization? authorizedModifiedFiles)
    {
        if (ApplicationMutationRoute is null)
        {
            return new()
            {
                ErrorCode = "ArchiveSwitchCoordinatorRequired"
            };
        }
        if (_ownedCoordinator is not null)
        {
            _ = await ApplicationMutationRoute.PrepareSwitchAsync(
                installedArchive,
                targetArchive,
                profile,
                libraryRoot,
                cancellationToken,
                authorizedModifiedFiles);
        }
        return await ApplicationMutationRoute.ExecuteSwitchAsync(
            installedArchive,
            targetArchive,
            profile,
            libraryRoot,
            cancellationToken,
            authorizedModifiedFiles);
    }

    internal bool ShouldFail(VersionSwitchFaultPoint point) =>
        _failureInjector?.Invoke(point) == true;

    internal Task ReachAsync(
        VersionSwitchExecutionPoint point,
        CancellationToken cancellationToken) =>
        TestOnlyExecutionHook?.Invoke(point, cancellationToken) ??
        Task.CompletedTask;

    private static ModArchiveSwitchPlan BlockedPlan(
        OrganizerPackageRecord installedArchive,
        OrganizerPackageRecord targetArchive,
        string errorCode) => new()
        {
            InstalledArchiveId = installedArchive.Package.PackageId,
            TargetArchiveId = targetArchive.Package.PackageId,
            Operation = ArchiveVersionOperation.Install,
            ErrorCode = errorCode
        };
}
