using System.Net.Http;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Infrastructure;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public sealed class DownloadsModuleHost : IDownloadsModuleBoundary
{
    private readonly RipperWorksPaths _paths;
    private readonly ISettingsModuleBoundary _settings;
    private readonly ILibraryModuleBoundary _library;
    private readonly OrganizerRepository _organizerRepository;
    private readonly DownloaderRepository _repository;
    private readonly StartupExceptionLogger _logger;
    private readonly INexusRequirementRefreshService? _nexusRequirements;
    private HttpClient? _downloadHttpClient;
    private DownloadsViewModel? _presentation;
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public DownloadsModuleHost(
        RipperWorksPaths paths,
        ISettingsModuleBoundary settings,
        ILibraryModuleBoundary library,
        OrganizerRepository organizerRepository,
        DownloaderRepository repository,
        StartupExceptionLogger logger,
        INexusRequirementRefreshService? nexusRequirements = null)
    {
        _paths = paths;
        _settings = settings;
        _library = library;
        _organizerRepository = organizerRepository;
        _repository = repository;
        _logger = logger;
        _nexusRequirements = nexusRequirements;
    }

    public string Name => "Downloads";
    public DownloadsViewModel Presentation => RequirePresentation();

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started || _stopped)
            throw new InvalidOperationException($"{Name} cannot be restarted.");

        await _repository.InitializeAsync(cancellationToken);
        _ = await _repository.LoadEntriesAsync(cancellationToken);
        _downloadHttpClient = new HttpClient();
        DownloaderTechnicalLog downloadLog =
            (stage, entry, exception) =>
                TryLogDownloaderStage(
                    stage,
                    entry,
                    exception);
        var publisher = new DownloadArchivePublisher(
            _repository,
            _paths.CatalogDatabasePath,
            downloadLog);
        var publicationReconciliation =
            await publisher.ReconcilePendingAsync(
                _settings.CurrentSettings.LibraryRoot,
                cancellationToken);
        var queue = new DownloadQueue(
            _repository,
            new HttpDownloadTransport(_downloadHttpClient),
            publisher,
            () =>
            {
                var value = _settings.CurrentSettings.DownloaderTempRoot;
                return string.IsNullOrWhiteSpace(value)
                    ? _paths.DownloaderDirectory
                    : value;
            },
            () => _settings.CurrentSettings.LibraryRoot,
            _settings.CurrentSettings.ConcurrentDownloads,
            downloadLog);
        var nexus = new NexusDownloadCoordinator(
            _repository,
            _settings.NexusApi,
            _settings.Credentials,
            queue,
            downloadLog);
        var manualQueue = new ManualBrowserQueue(_repository, nexus);
        var downloaderDialogs = new DownloaderDialogService();
        var internalBrowser = new InternalBrowserWindowService(
            _paths.WebView2Directory);
        var archiveStatus = new DownloaderArchiveStatusReconciler(
            _repository,
            new OrganizerLibraryArchiveLookup(_organizerRepository));
        var updateCheck = new NexusModUpdateCheckService(
            _repository,
            new NexusModUpdateCheckClient(
                _settings.NexusUpdateApi,
                _settings.Credentials));
        _presentation = new DownloadsViewModel(
            _repository,
            nexus,
            queue,
            new DownloaderTableImporter(),
            publisher,
            downloaderDialogs,
            _settings.Localization,
            () => _settings.CurrentSettings,
            OnArchivePublishedAsync,
            exception => TryLogException("Downloader", exception),
            manualQueue,
            internalBrowser,
            new ExternalNexusBrowserService(downloaderDialogs),
            _settings.Dialogs.ShowError,
            archiveStatus,
            _settings.RecordSessionEvent,
            updateCheck,
            () => _library.ReconcileArchiveFamiliesAsync(),
            _nexusRequirements);
        try
        {
            await _presentation.InitializeAsync();
            if (publicationReconciliation > 0)
                await _library.RefreshAfterDownloadAsync();
        }
        catch (Exception exception)
        {
            var logPath = _logger.Log(
                "DownloaderInitialization",
                exception);
            _presentation.ReportInitializationFailure(logPath);
        }
        _started = true;
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        if (_stopped)
            return;
        _stopped = true;
        _started = false;
        if (_presentation is not null)
        {
            await _presentation.DisposeAsync()
                .AsTask()
                .WaitAsync(cancellationToken);
        }
    }

    public async Task HandleExternalArgumentAsync(
        string argument,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RequirePresentation().HandleNxmAsync(argument);
    }

    public async Task<bool> AddUnresolvedNexusEntryAsync(
        NexusModIdentity targetMod,
        string? safeUrl = null,
        string? fallbackName = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || !_started || _presentation is null)
            return false;
        return await _presentation.AddUnresolvedNexusEntryAsync(
            targetMod,
            safeUrl,
            fallbackName,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _stopped = true;
        _started = false;
        if (_presentation is not null)
            await _presentation.DisposeAsync();
        _disposed = true;
        _presentation = null;
        _downloadHttpClient?.Dispose();
        _downloadHttpClient = null;
    }

    private async Task OnArchivePublishedAsync(DownloaderEntry entry)
    {
        if (_disposed)
            return;
        if (_nexusRequirements is not null &&
            NexusRequirementRefreshEligibility.TryCreate(entry, out var identity))
        {
            try
            {
                await _nexusRequirements.RefreshAsync(identity).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                TryLogException("NexusRequirementHydration", exception);
            }
        }
        await _library.RefreshAfterDownloadAsync();
        if (_started && !_disposed && _presentation is not null)
            await _presentation.RefreshPersistedEntriesAsync();
    }

    private void TryLogDownloaderStage(
        string stage,
        DownloaderEntry entry,
        Exception? exception)
    {
        try
        {
            _logger.LogDownloaderStage(
                stage,
                entry.Id,
                entry.Name,
                exception);
        }
        catch
        {
            // Diagnostics must never control Downloader business flow.
        }
    }

    private string TryLogException(string context, Exception exception)
    {
        try
        {
            return _logger.Log(context, exception);
        }
        catch
        {
            // Diagnostics must never control Downloader business flow.
            return string.Empty;
        }
    }

    private DownloadsViewModel RequirePresentation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _started && _presentation is not null
            ? _presentation
            : throw new InvalidOperationException(
                $"{Name} has not completed startup.");
    }
}
