using System.Net.Http;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.GameMaintenance;
using RipperWorks.Infrastructure;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public sealed class SettingsModuleHost : ISettingsModuleBoundary
{
    private readonly RipperWorksPaths _paths;
    private readonly IGameOperationsModuleBoundary _gameOperations;
    private readonly StartupExceptionLogger _logger;
    private SettingsSession? _session;
    private LocalizationService? _localization;
    private SessionEventLog? _sessionEvents;
    private ThemeService? _theme;
    private DpapiProtectedCredentialStore? _credentials;
    private readonly IGameMaintenanceService? _gameMaintenance;
    private readonly IGameMaintenanceManagedStateProvider? _managedStateProvider;
    private HttpClient? _httpClient;
    private NexusApiClient? _nexusApi;
    private SettingsViewModel? _presentation;
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public SettingsModuleHost(
        RipperWorksPaths paths,
        IGameOperationsModuleBoundary gameOperations,
        StartupExceptionLogger logger,
        IGameMaintenanceService? gameMaintenance = null,
        IGameMaintenanceManagedStateProvider? managedStateProvider = null)
    {
        _paths = paths;
        _gameOperations = gameOperations;
        _logger = logger;
        _gameMaintenance = gameMaintenance;
        _managedStateProvider = managedStateProvider;
    }

    public string Name => "Settings";
    public RipperWorksSettings CurrentSettings =>
        Require(_session).Current;
    public LocalizationService Localization => Require(_localization);
    public IUserDialogService Dialogs { get; private set; } = null!;
    public IProtectedCredentialStore Credentials => Require(_credentials);
    public INexusApiClient NexusApi => Require(_nexusApi);
    public INexusUpdateApiClient NexusUpdateApi => Require(_nexusApi);
    public SettingsViewModel Presentation => Require(_presentation);
    public SessionEventLog SessionEventsPresentation =>
        Require(_sessionEvents);

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started || _stopped)
            throw new InvalidOperationException($"{Name} cannot be restarted.");

        try
        {
            _session = await SettingsSession.CreateAsync(
                new AtomicJsonSettingsStore(_paths),
                cancellationToken);
            var settings = _session.Current;
            _localization = new LocalizationService(settings.Language);
            _sessionEvents = new SessionEventLog(_localization);
            _theme = new ThemeService();
            _theme.Start();
            _theme.Apply(settings.Theme);
            Dialogs = new UserDialogService(_localization);
            _credentials =
                DpapiProtectedCredentialStore.CreateProductionLayout(_paths);
            await _credentials.RunStartupMigrationAsync(cancellationToken);
            _httpClient = new HttpClient();
            _nexusApi = new NexusApiClient(_httpClient);
            _presentation = new SettingsViewModel(
                _session,
                _localization,
                _theme,
                Dialogs,
                new FolderPickerService(),
                _gameOperations,
                _logger,
                RecordSessionEvent,
                _credentials,
                _nexusApi,
                new NxmProtocolRegistration(),
                Environment.ProcessPath,
                _gameMaintenance,
                _managedStateProvider);
            await _presentation.InitializeDownloaderSettingsAsync();
            _started = true;
        }
        catch
        {
            DisposeOwnedResources();
            _stopped = true;
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped)
            return Task.CompletedTask;
        _stopped = true;
        _started = false;
        _presentation?.Dispose();
        _theme?.Stop();
        return Task.CompletedTask;
    }

    public void RecordSessionEvent(
        string localizationKey,
        string? detail = null)
    {
        if (_started)
            _sessionEvents?.Record(localizationKey, detail);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _stopped = true;
        _started = false;
        DisposeOwnedResources();
        return ValueTask.CompletedTask;
    }

    private void DisposeOwnedResources()
    {
        _presentation?.Dispose();
        _presentation = null;
        _theme?.Dispose();
        _theme = null;
        _sessionEvents?.Dispose();
        _sessionEvents = null;
        _session?.Dispose();
        _session = null;
        _credentials?.Dispose();
        _credentials = null;
        _httpClient?.Dispose();
        _httpClient = null;
        _nexusApi = null;
    }

    private T Require<T>(T? value) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return value ?? throw new InvalidOperationException(
            $"{Name} has not completed startup.");
    }
}
