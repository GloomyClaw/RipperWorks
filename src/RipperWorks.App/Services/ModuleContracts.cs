using RipperWorks.App.ViewModels;
using RipperWorks.Core;
using RipperWorks.Downloader;

namespace RipperWorks.App.Services;

public interface IGameOperationsModuleBoundary : IApplicationModule
{
    GameProfileRecord? CurrentGameProfile { get; }
    bool RecoveryRequired { get; }

    Task<GameProfileValidationResult> ValidateGameProfileAsync(
        string gameRoot,
        string libraryRoot,
        CancellationToken cancellationToken = default);

    Task<GameProfileRecord?> SaveGameProfileAsync(
        GameProfileValidationResult validation,
        CancellationToken cancellationToken = default);

    Task<GameLaunchResult> LaunchGameAsync(
        GameProfileRecord? profile,
        CancellationToken cancellationToken = default);

    bool CanLaunchGame(GameProfileRecord? profile);
}

public interface ISettingsModuleBoundary : IApplicationModule
{
    RipperWorksSettings CurrentSettings { get; }
    LocalizationService Localization { get; }
    IUserDialogService Dialogs { get; }
    INexusApiClient NexusApi { get; }
    INexusUpdateApiClient NexusUpdateApi { get; }
    SettingsViewModel Presentation { get; }
    SessionEventLog SessionEventsPresentation { get; }

    void RecordSessionEvent(string localizationKey, string? detail = null);
}

public interface INexusRequirementRefreshService
{
    Task<NexusRequirementSyncResult> RefreshAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken) =>
        RefreshAsync(mod, clearDismissals: false, cancellationToken);

    Task<NexusRequirementSyncResult> RefreshAsync(
        NexusModIdentity mod,
        bool clearDismissals = false,
        CancellationToken cancellationToken = default);
}

public interface INexusRequirementRelationsService
{
    Task<NexusRequirementRelations> LoadRelationsAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken = default);

    Task DismissRelationAsync(
        NexusRequirementCanonicalKey key,
        CancellationToken cancellationToken = default);

    Task<bool> AddToDownloadsAsync(
        NexusModIdentity targetMod,
        string displayName,
        string? safeUrl = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

public interface INexusRequirementModuleBoundary :
    IApplicationModule,
    INexusRequirementRefreshService,
    INexusRequirementRelationsService
{
}

public interface ILibraryModuleBoundary : IApplicationModule
{
    LibraryViewModel Presentation { get; }

    Task RefreshAfterDownloadAsync(
        CancellationToken cancellationToken = default);

    Task ReconcileArchiveFamiliesAsync(
        CancellationToken cancellationToken = default);

    Task RequestRecoveryAsync(
        CancellationToken cancellationToken = default);
}

public interface IDownloadsModuleBoundary : IApplicationModule
{
    DownloadsViewModel Presentation { get; }

    Task HandleExternalArgumentAsync(
        string argument,
        CancellationToken cancellationToken = default);

    Task<bool> AddUnresolvedNexusEntryAsync(
        NexusModIdentity targetMod,
        string? safeUrl = null,
        string? fallbackName = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

internal interface ISettingsGameProfilePresentation
{
    string Cyberpunk2077Root { get; set; }
    string LibraryRoot { get; }
    GameProfileRecord? CurrentGameProfile { get; set; }
    Action? RequestNavigateDiagnostics { get; }

    void SetGameStatus(string value);
    void NotifyGameValidationChanged();
    void NotifyGameLaunchChanged();
}
