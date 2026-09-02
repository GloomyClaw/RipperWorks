using System.IO;
using System.Windows.Threading;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Infrastructure;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

internal interface ILibraryAnalysisLifetime : IDisposable
{
    Task ActiveAnalysisCompletion { get; }
    void RequestAnalysisCancellation();
}

internal interface ILibraryUiStatePersistenceLifetime
{
    Task CompleteUiStatePersistenceAsync();
}

public sealed class LibraryModuleHost : ILibraryModuleBoundary
{
    private readonly RipperWorksPaths _paths;
    private readonly ISettingsModuleBoundary _settings;
    private readonly IGameOperationsModuleBoundary _gameOperations;
    private readonly OrganizerRepository _organizerRepository;
    private readonly DownloaderRepository _downloaderRepository;
    private readonly GameProfileService _gameProfiles;
    private readonly StartupExceptionLogger _logger;
    private readonly INexusRequirementModuleBoundary? _nexusRequirements;
    private ArchiveFamilyReconciliationService? _familyReconciliation;
    private LibraryViewModel? _presentation;
    private ILibraryAnalysisLifetime? _analysisLifetime;
    private ILibraryUiStatePersistenceLifetime? _uiStatePersistenceLifetime;
    private UnifiedModMutationCoordinator? _mutations;
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public LibraryModuleHost(
        RipperWorksPaths paths,
        ISettingsModuleBoundary settings,
        IGameOperationsModuleBoundary gameOperations,
        OrganizerRepository organizerRepository,
        DownloaderRepository downloaderRepository,
        GameProfileService gameProfiles,
        StartupExceptionLogger logger,
        INexusRequirementModuleBoundary? nexusRequirements = null)
    {
        _paths = paths;
        _settings = settings;
        _gameOperations = gameOperations;
        _organizerRepository = organizerRepository;
        _downloaderRepository = downloaderRepository;
        _gameProfiles = gameProfiles;
        _logger = logger;
        _nexusRequirements = nexusRequirements;
    }

    internal LibraryModuleHost(
        ILibraryAnalysisLifetime analysisLifetime)
    {
        _paths = null!;
        _settings = null!;
        _gameOperations = null!;
        _organizerRepository = null!;
        _downloaderRepository = null!;
        _gameProfiles = null!;
        _logger = null!;
        _nexusRequirements = null;
        _analysisLifetime = analysisLifetime;
        _uiStatePersistenceLifetime =
            analysisLifetime as ILibraryUiStatePersistenceLifetime;
        _started = true;
    }

    public string Name => "Library";
    public LibraryViewModel Presentation => RequirePresentation();

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started || _stopped)
            throw new InvalidOperationException($"{Name} cannot be restarted.");
        cancellationToken.ThrowIfCancellationRequested();

        var metadataReader = new LegacyPackageMetadataReader();
        var libraryIndex = new LibraryIndexService(
            metadataReader,
            new DownloaderArchiveMetadataLookup(_downloaderRepository));
        var archiveAnalyzer = new ArchiveAnalyzer();
        var organizerAnalysis = new OrganizerAnalysisService(
            archiveAnalyzer,
            _organizerRepository);
        var analysisQueue = new AnalysisQueueService(organizerAnalysis);
        var installedOwners =
            new OrganizerInstalledPathOwnerProvider(_organizerRepository);
        var installPlan = new InstallPlanService(installedOwners);
        var contentStore = new ContentStoreService(_paths.ContentDirectory);
        var installation = new ModInstallationService(
            _organizerRepository,
            installPlan,
            _gameProfiles,
            contentStore,
            _paths.StagingDirectory,
            exceptionLogger: exception =>
                _logger.Log("ModInstallation", exception));
        var removal = new ModRemovalService(
            _organizerRepository,
            _gameProfiles,
            contentStore,
            _paths.StagingDirectory,
            exceptionLogger: exception =>
                _logger.Log("ModRemoval", exception));
        var archiveSwitch = new ModArchiveSwitchService(
            () => _gameOperations.CurrentGameProfile,
            () => _settings.CurrentSettings.LibraryRoot);
        var archiveDeletion = new LibraryArchiveDeletionService(
            _organizerRepository,
            removal,
            message => _logger.Log(
                "LibraryArchiveDeletion",
                new InvalidOperationException(message)));
        var relations = new PackageRelationService(_organizerRepository);
        var coordinatorSessionEvents =
            CreateCoordinatorSessionEventRecorder(
                Dispatcher.CurrentDispatcher,
                _settings.RecordSessionEvent);
        var mutations = new UnifiedModMutationCoordinator(
            installation,
            removal,
            relations,
            _organizerRepository,
            coordinatorSessionEvents,
            archiveSwitch);
        _mutations = mutations;
        var libraryWindows = new LibraryWindowService(
            exception => _logger.Log("InstallationProgressUi", exception));
        _familyReconciliation = new ArchiveFamilyReconciliationService(
            _organizerRepository,
            _paths.DownloaderDatabasePath);
        _presentation = new LibraryViewModel(
            () => _settings.CurrentSettings,
            libraryIndex,
            _settings.Localization,
            _organizerRepository,
            organizerAnalysis,
            analysisQueue,
            libraryWindows,
            installPlan,
            () => _gameOperations.CurrentGameProfile,
            _settings.RecordSessionEvent,
            installation,
            _settings.Dialogs,
            exception => _logger.Log("InstallationUi", exception),
            removal,
            relations,
            mutations,
            mutations,
            archiveSwitch,
            archiveDeletion,
            _familyReconciliation,
            _nexusRequirements,
            _nexusRequirements,
            new RipperWorks.GameMaintenance.GameMaintenanceService(
                new SystemGameProcessAdapter(),
                new OrganizerGameMaintenanceManagedStateProvider(_organizerRepository)));
        _analysisLifetime = _presentation;
        _uiStatePersistenceLifetime = _presentation;
        try
        {
            await _presentation.LoadCachedAsync();
            _presentation.SetRecoveryRequired(
                _gameOperations.RecoveryRequired);
            _started = true;
        }
        catch
        {
            _presentation.Dispose();
            _presentation = null;
            _analysisLifetime = null;
            _uiStatePersistenceLifetime = null;
            _familyReconciliation = null;
            _mutations = null;
            _stopped = true;
            throw;
        }
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        if (_stopped)
            return;
        _stopped = true;
        _started = false;
        var analysisLifetime = _analysisLifetime;
        var uiStatePersistenceLifetime = _uiStatePersistenceLifetime;
        if (analysisLifetime is null && uiStatePersistenceLifetime is null)
            return;
        analysisLifetime?.RequestAnalysisCancellation();
        try
        {
            if (analysisLifetime is not null)
            {
                await analysisLifetime.ActiveAnalysisCompletion
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (uiStatePersistenceLifetime is not null)
                {
                    await uiStatePersistenceLifetime
                        .CompleteUiStatePersistenceAsync()
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                analysisLifetime?.Dispose();
                _analysisLifetime = null;
                _uiStatePersistenceLifetime = null;
                _presentation = null;
                _familyReconciliation = null;
                _mutations = null;
            }
        }
    }

    public async Task RefreshAfterDownloadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var presentation = RequirePresentation();
        await presentation.RefreshFromHostAsync();
    }

    public Task ReconcileArchiveFamiliesAsync(
        CancellationToken cancellationToken = default) =>
        RequireReconciliation().ReconcileAsync(null, cancellationToken);

    public async Task RequestRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        var presentation = RequirePresentation();
        var mutations = _mutations ?? throw new InvalidOperationException(
            "Recovery coordinator is unavailable.");
        var operationIds = await _organizerRepository
            .LoadRecoveryRequiredOperationIdsAsync(cancellationToken);
        foreach (var operationId in operationIds)
        {
            var plan = await mutations.PrepareRecoveryAsync(
                operationId,
                _gameOperations.CurrentGameProfile,
                _settings.CurrentSettings.LibraryRoot,
                cancellationToken);
            if (plan.AlreadyRecovered)
                continue;
            var details = RecoveryDetails(plan);
            if (!plan.IsDeterministic)
            {
                _settings.Dialogs.ShowError(details);
                continue;
            }
            var english = _settings.Localization.CurrentLanguage ==
                SupportedLanguages.English;
            if (!_settings.Dialogs.Confirm(
                    details,
                    english ? "Recovery required" : "Требуется восстановление",
                    english ? "Cancel" : "Отмена",
                    english ? "Restore proven state" : "Восстановить доказанное состояние"))
            {
                continue;
            }
            var result = await mutations.ExecuteRecoveryAsync(
                plan,
                _gameOperations.CurrentGameProfile,
                _settings.CurrentSettings.LibraryRoot,
                cancellationToken);
            if (!result.Success)
                _settings.Dialogs.ShowError(result.ErrorCode ?? "RecoveryFailed");
        }
        var remains = await _organizerRepository
            .HasRecoveryRequiredOperationsAsync(cancellationToken);
        presentation.SetRecoveryRequired(remains);
        if (!remains)
            await presentation.RefreshFromHostAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stopped = true;
        _started = false;
        var analysisLifetime = _analysisLifetime;
        var uiStatePersistenceLifetime = _uiStatePersistenceLifetime;
        if (analysisLifetime is not null ||
            uiStatePersistenceLifetime is not null)
        {
            analysisLifetime?.RequestAnalysisCancellation();
            try
            {
                if (analysisLifetime is not null)
                {
                    await analysisLifetime.ActiveAnalysisCompletion
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (uiStatePersistenceLifetime is not null)
                    {
                        await uiStatePersistenceLifetime
                            .CompleteUiStatePersistenceAsync()
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    analysisLifetime?.Dispose();
                }
            }
        }
        _analysisLifetime = null;
        _uiStatePersistenceLifetime = null;
        _presentation = null;
        _familyReconciliation = null;
        _mutations = null;
    }

    private LibraryViewModel RequirePresentation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _started && _presentation is not null
            ? _presentation
            : throw new InvalidOperationException(
                $"{Name} has not completed startup.");
    }

    private ArchiveFamilyReconciliationService RequireReconciliation()
    {
        _ = RequirePresentation();
        return _familyReconciliation!;
    }

    internal static Action<string, string?>
        CreateCoordinatorSessionEventRecorder(
            Dispatcher dispatcher,
            Action<string, string?> recordSessionEvent)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(recordSessionEvent);
        return (localizationKey, detail) =>
        {
            if (dispatcher.CheckAccess())
            {
                recordSessionEvent(localizationKey, detail);
                return;
            }
            dispatcher.Invoke(
                () => recordSessionEvent(localizationKey, detail),
                DispatcherPriority.Send);
        };
    }

    private static string RecoveryDetails(RecoveryPlan plan)
    {
        var packages = string.Join(", ",
            plan.PackageIds.Select(value => value.Value));
        var blockers = plan.Blockers.Count == 0
            ? "none"
            : string.Join(", ", plan.Blockers);
        return $"Operation: {plan.OperationId:N}{Environment.NewLine}" +
            $"Kind: {plan.Kind}{Environment.NewLine}" +
            $"Packages: {packages}{Environment.NewLine}" +
            $"Phase/status: {plan.DurablePhase}/{plan.DurableStatus}{Environment.NewLine}" +
            $"Target: {plan.DesiredState}{Environment.NewLine}" +
            $"Blockers: {blockers}";
    }
}
