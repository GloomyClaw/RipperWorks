using System.IO;
using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Infrastructure;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public static class ApplicationComposition
{
    private const string VerifiedStartupBackupId =
        "RF-01-20260730-0208";

    public static ApplicationRuntime Build(
        RipperWorksPaths paths,
        StartupExceptionLogger logger,
        ISingleInstanceCoordinator? singleInstance = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        var organizerRepository = new OrganizerRepository(
            Path.Combine(paths.DatabaseDirectory, "organizer.db"),
            warning => logger.Log(
                "LibraryDuplicate",
                new InvalidOperationException(warning)));
        var downloaderRepository = new DownloaderRepository(
            paths.DownloaderDatabasePath);
        var shortlistStore = new AtomicJsonShortlistStore(paths.ShortlistPath);
        var localStateService = new NexusModLocalStateService(
            shortlistStore,
            downloaderRepository,
            organizerRepository);
        var startupDataHygiene = new StartupDataHygieneRunner(
            organizerRepository.DatabasePath,
            paths.CatalogDatabasePath,
            StartupMigrationContext.Create(
                VerifiedStartupBackupId,
                typeof(ApplicationComposition)),
            maximumCatalogSchemaVersion: CatalogSchemaContract.CurrentVersion);
        var processAdapter = new SystemGameProcessAdapter();
        var gameProfiles = new GameProfileService(processAdapter);
        var diagnosticCapture = new GameDiagnosticCaptureService(organizerRepository);
        var diagnosticParser = new GameDiagnosticParserService();
        IGameOperationsModuleBoundary gameOperations =
            new GameOperationsModuleHost(
                organizerRepository,
                startupDataHygiene,
                gameProfiles,
                new GameLauncherService(processAdapter, paths.LogsDirectory, diagnosticCapture, diagnosticParser));
        var managedStateProvider = new OrganizerGameMaintenanceManagedStateProvider(organizerRepository);
        var gameMaintenance = new RipperWorks.GameMaintenance.GameMaintenanceService(
            processAdapter,
            managedStateProvider);
        ISettingsModuleBoundary settings =
            new SettingsModuleHost(paths, gameOperations, logger, gameMaintenance, managedStateProvider);
        IDownloadsModuleBoundary? downloadsRef = null;
        INexusRequirementModuleBoundary nexusRequirements =
            new NexusRequirementModuleHost(
                paths.CatalogDatabasePath,
                organizerRepository,
                downloaderRepository,
                () => downloadsRef);
        ILibraryModuleBoundary library =
            new LibraryModuleHost(
                paths,
                settings,
                gameOperations,
                organizerRepository,
                downloaderRepository,
                gameProfiles,
                logger,
                nexusRequirements);
        IDownloadsModuleBoundary downloads =
            new DownloadsModuleHost(
                paths,
                settings,
                library,
                organizerRepository,
                downloaderRepository,
                logger,
                nexusRequirements);
        downloadsRef = downloads;
        IApplicationModule[] modules =
        [
            gameOperations,
            settings,
            nexusRequirements,
            library,
            downloads
        ];
        return new ApplicationRuntime(
            modules,
            singleInstance ?? new SingleInstanceCoordinator(),
            settings,
            library,
            downloads,
            organizerRepository.DatabasePath,
            paths.LogsDirectory,
            (stage, exception) => logger.Log(stage, exception),
            frameworkStateProvider: organizerRepository,
            shortlistStore: shortlistStore,
            localStateService: localStateService,
            nexusRequirements: nexusRequirements);
    }
}
