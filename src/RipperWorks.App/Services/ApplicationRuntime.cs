using System.Diagnostics;
using System.Runtime.ExceptionServices;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public enum ApplicationRuntimeState
{
    Created, Starting, Started, Stopping, Stopped, CleanupPending, Faulted,
    Disposed
}

public sealed class ApplicationRuntime : IAsyncDisposable
{
    private static readonly TimeSpan DefaultShutdownBudget =
        TimeSpan.FromSeconds(6);

    private readonly IReadOnlyList<IApplicationModule> _modules;
    private readonly ISingleInstanceCoordinator? _singleInstance;
    private readonly ISettingsModuleBoundary? _settings;
    private readonly ILibraryModuleBoundary? _library;
    private readonly IDownloadsModuleBoundary? _downloads;
    private readonly string? _databasePath;
    private readonly string? _logsDirectory;
    private readonly Action<string, Exception> _observeError;
    private readonly TimeSpan _shutdownBudget;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _applicationCancellation = new();
    private readonly List<IApplicationModule> _startedModules = [];
    private readonly object _disposeSync = new();
    private readonly OwnedLifecycleCleanup _cleanup;
    private MainWindowViewModel? _mainViewModel;
    private DiagnosticsViewModel? _diagnosticsViewModel;
    private Task<IReadOnlyList<Exception>>? _terminalCleanupTask;
    private Task? _disposeTask;
    private bool _singleInstanceStarted;
    private bool _disposeRequested;
    private int _cleanupTimeoutObserved;
    private volatile ApplicationRuntimeState _state =
        ApplicationRuntimeState.Created;

    private readonly IInstalledFrameworkStateProvider? _frameworkStateProvider;
    private readonly IShortlistStore? _shortlistStore;
    private readonly INexusModLocalStateService? _localStateService;
    private readonly INexusRequirementModuleBoundary? _nexusRequirements;

    public ApplicationRuntime(
        IEnumerable<IApplicationModule> modules,
        Action<string, Exception>? observeError = null,
        TimeSpan? shutdownBudget = null)
        : this(modules, null, null, null, null, null, null, observeError,
            shutdownBudget)
    {
    }

    public ApplicationRuntime(
        IEnumerable<IApplicationModule> modules,
        ISingleInstanceCoordinator singleInstance,
        Action<string, Exception>? observeError = null,
        TimeSpan? shutdownBudget = null)
        : this(modules, singleInstance, null, null, null, null, null,
            observeError, shutdownBudget)
    {
    }

    internal ApplicationRuntime(
        IEnumerable<IApplicationModule> modules,
        ISingleInstanceCoordinator? singleInstance,
        ISettingsModuleBoundary? settings,
        ILibraryModuleBoundary? library,
        IDownloadsModuleBoundary? downloads,
        string? databasePath,
        string? logsDirectory,
        Action<string, Exception>? observeError = null,
        TimeSpan? shutdownBudget = null,
        IInstalledFrameworkStateProvider? frameworkStateProvider = null,
        IShortlistStore? shortlistStore = null,
        INexusModLocalStateService? localStateService = null,
        INexusRequirementModuleBoundary? nexusRequirements = null)
    {
        _modules = modules?.ToArray()
            ?? throw new ArgumentNullException(nameof(modules));
        if (_modules.Count == 0)
            throw new ArgumentException(
                "At least one module is required.", nameof(modules));
        if (_modules.Distinct(ReferenceEqualityComparer.Instance).Count() !=
            _modules.Count)
        {
            throw new ArgumentException(
                "A module instance cannot appear more than once.",
                nameof(modules));
        }
        _singleInstance = singleInstance;
        _settings = settings;
        _library = library;
        _downloads = downloads;
        _nexusRequirements = nexusRequirements;
        _databasePath = databasePath;
        _logsDirectory = logsDirectory;
        _observeError = observeError ?? ((_, _) => { });
        _cleanup = new OwnedLifecycleCleanup(Observe);
        _shutdownBudget = shutdownBudget ?? DefaultShutdownBudget;
        if (_shutdownBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shutdownBudget));
        _frameworkStateProvider = frameworkStateProvider;
        _shortlistStore = shortlistStore;
        _localStateService = localStateService;
    }

    public event EventHandler<InstanceMessage>? ActivationRequested;

    public ApplicationRuntimeState State => _state;
    public CancellationToken ApplicationCancellation =>
        _applicationCancellation.Token;
    public TimeSpan ShutdownBudget => _shutdownBudget;
    public TimeSpan? LastStartupDuration { get; private set; }
    public TimeSpan? LastShutdownDuration { get; private set; }
    public TimeSpan? LastRollbackDuration { get; private set; }
    public MainWindowViewModel MainViewModel =>
        _mainViewModel ?? throw new InvalidOperationException(
            "The application shell is not ready.");

    public async Task<bool> StartAsync(
        string? argument = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(
                _disposeRequested ||
                _state == ApplicationRuntimeState.Disposed,
                this);
            if (_state != ApplicationRuntimeState.Created)
            {
                throw new InvalidOperationException(
                    $"Application runtime cannot start from {_state}.");
            }
            _state = ApplicationRuntimeState.Starting;
            var startupTimer = Stopwatch.StartNew();
            using var startCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _applicationCancellation.Token);
            try
            {
                if (_singleInstance is not null)
                {
                    if (!await _singleInstance.StartAsync(
                            argument,
                            startCancellation.Token))
                    {
                        _state = ApplicationRuntimeState.Stopped;
                        return false;
                    }
                    _singleInstanceStarted = true;
                    _singleInstance.MessageReceived +=
                        SingleInstance_OnMessageReceived;
                }

                foreach (var module in _modules)
                {
                    await module.StartAsync(startCancellation.Token);
                    _startedModules.Add(module);
                }
                BuildShell();
                LastStartupDuration = startupTimer.Elapsed;
                _state = ApplicationRuntimeState.Started;
                return true;
            }
            catch (Exception startupException)
            {
                LastStartupDuration = startupTimer.Elapsed;
                _state = ApplicationRuntimeState.Faulted;
                var capturedStartup =
                    ExceptionDispatchInfo.Capture(startupException);
                var deadline = new ShutdownDeadline(_shutdownBudget);
                await RollBackStartedModulesAsync(deadline)
                    .ConfigureAwait(false);
                await StopSingleInstanceBoundedAsync(
                        deadline,
                        1,
                        terminal: false,
                        [])
                    .ConfigureAwait(false);
                capturedStartup.Throw();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<Exception> errors;
        try
        {
            if (_disposeRequested)
                return;
            errors = await StopCoreAsync(
                    new ShutdownDeadline(_shutdownBudget),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (errors.Count > 0)
        {
            throw new AggregateException(
                "One or more lifecycle owners failed to stop.",
                errors);
        }
    }

    public async Task HandleExternalArgumentAsync(
        string argument,
        CancellationToken cancellationToken = default)
    {
        if (_state != ApplicationRuntimeState.Started || _downloads is null)
            return;
        await _downloads.HandleExternalArgumentAsync(
            argument,
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposal = _disposeTask;
        }
        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync()
    {
        var deadline = new ShutdownDeadline(_shutdownBudget);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposeRequested = true;
            if (_state == ApplicationRuntimeState.Created)
                _state = ApplicationRuntimeState.Stopped;
            else if (_state == ApplicationRuntimeState.Started)
            {
                await StopCoreAsync(deadline, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            DisposeShell();
            EnsureOwnedCleanup();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        var terminalCleanup = _terminalCleanupTask!;
        IReadOnlyList<Exception> errors;
        try
        {
            if (terminalCleanup.IsCompleted)
                errors = await terminalCleanup.ConfigureAwait(false);
            else
            {
                var remaining = deadline.Remaining;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException();
                errors = await terminalCleanup.WaitAsync(remaining)
                    .ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            if (_state is not (ApplicationRuntimeState.Disposed or
                ApplicationRuntimeState.Faulted))
            {
                _state = ApplicationRuntimeState.CleanupPending;
            }
            var pending = new ApplicationCleanupPendingException(
                _shutdownBudget);
            if (Interlocked.Exchange(
                    ref _cleanupTimeoutObserved,
                    1) == 0)
            {
                Observe("Application.DisposeBounded", pending);
            }
            throw pending;
        }

        if (errors.Count > 0)
        {
            throw new AggregateException(
                "Application cleanup completed with lifecycle faults.",
                errors);
        }
    }

    private async Task<IReadOnlyList<Exception>> StopCoreAsync(
        ShutdownDeadline deadline,
        CancellationToken cancellationToken)
    {
        if (_state is ApplicationRuntimeState.Stopped or
            ApplicationRuntimeState.Disposed)
        {
            return [];
        }
        if (_state == ApplicationRuntimeState.CleanupPending)
            return _cleanup.PolicyFailures.ToArray();
        if (_state == ApplicationRuntimeState.Created)
        {
            _state = ApplicationRuntimeState.Stopped;
            return [];
        }
        if (_state == ApplicationRuntimeState.Faulted)
            return [];

        _state = ApplicationRuntimeState.Stopping;
        var shutdownTimer = Stopwatch.StartNew();
        await _applicationCancellation.CancelAsync()
            .ConfigureAwait(false);
        DisposeShell();
        var errors = new List<Exception>();
        var ownersRemaining = _startedModules.Count +
            (_singleInstanceStarted ? 1 : 0);

        for (var index = _startedModules.Count - 1; index >= 0; index--)
        {
            var module = _startedModules[index];
            var attempt = _cleanup.GetModuleStop(
                module, cancellationToken);
            await WaitForStopAsync(
                    attempt,
                    deadline,
                    Math.Max(1, ownersRemaining--),
                    terminal: true,
                    errors)
                .ConfigureAwait(false);
        }
        _startedModules.Clear();
        await StopSingleInstanceBoundedAsync(
                deadline,
                Math.Max(1, ownersRemaining),
                terminal: true,
                errors)
            .ConfigureAwait(false);

        var pending = _cleanup.HasPendingTerminalStops;
        _state = pending
            ? ApplicationRuntimeState.CleanupPending
            : ApplicationRuntimeState.Stopped;
        LastShutdownDuration = shutdownTimer.Elapsed;
        return errors;
    }

    private async Task RollBackStartedModulesAsync(
        ShutdownDeadline deadline)
    {
        var rollbackTimer = Stopwatch.StartNew();
        var ownersRemaining = _startedModules.Count +
            (_singleInstanceStarted ? 1 : 0);
        for (var index = _startedModules.Count - 1; index >= 0; index--)
        {
            var attempt = _cleanup.GetModuleStop(
                _startedModules[index], CancellationToken.None);
            await WaitForStopAsync(
                    attempt,
                    deadline,
                    Math.Max(1, ownersRemaining--),
                    terminal: false,
                    [])
                .ConfigureAwait(false);
        }
        _startedModules.Clear();
        LastRollbackDuration = rollbackTimer.Elapsed;
    }

    private async Task StopSingleInstanceBoundedAsync(
        ShutdownDeadline deadline,
        int ownersRemaining,
        bool terminal,
        List<Exception> errors)
    {
        if (!_singleInstanceStarted || _singleInstance is null)
        {
            return;
        }
        _singleInstanceStarted = false;
        _singleInstance.MessageReceived -= SingleInstance_OnMessageReceived;
        var singleInstanceStop = _cleanup.GetSingleInstanceStop(
            () => _singleInstance.StopAsync(CancellationToken.None));
        await WaitForStopAsync(
                singleInstanceStop,
                deadline,
                Math.Max(1, ownersRemaining),
                terminal,
                errors)
            .ConfigureAwait(false);
    }

    private async Task WaitForStopAsync(
        OwnedStopAttempt attempt,
        ShutdownDeadline deadline,
        int ownersRemaining,
        bool terminal,
        List<Exception> errors)
    {
        if (!attempt.Completion.IsCompleted)
        {
            var slice = deadline.FairShare(ownersRemaining);
            if (slice > TimeSpan.Zero)
            {
                try
                {
                    await attempt.Completion.WaitAsync(slice)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Marked and reported below.
                }
            }
        }

        if (!attempt.Completion.IsCompleted)
        {
            var timeout = attempt.MarkLate();
            if (terminal)
            {
                var failure = timeout ??
                    new TimeoutException(
                        $"{attempt.OwnerName} cleanup is still pending.");
                errors.Add(failure);
                if (timeout is not null)
                    _cleanup.MarkTerminalLate(attempt, timeout);
            }
            return;
        }

        var error = await attempt.Completion.ConfigureAwait(false);
        if (error is not null && terminal)
        {
            errors.Add(error);
            _cleanup.RecordImmediateStopFailure(error);
        }
    }

    private void EnsureOwnedCleanup()
    {
        if (_terminalCleanupTask is not null)
            return;
        var ownedCleanup = _cleanup.Start(
            _modules,
            _singleInstance,
            DisposeShell,
            _applicationCancellation);
        _terminalCleanupTask = FinalizeOwnedCleanupAsync(ownedCleanup);
    }

    private async Task<IReadOnlyList<Exception>>
        FinalizeOwnedCleanupAsync(
            Task<IReadOnlyList<Exception>> cleanup)
    {
        var errors = (await cleanup.ConfigureAwait(false)).ToList();
        errors.AddRange(_cleanup.PolicyFailures);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _state = errors.Count == 0
                ? ApplicationRuntimeState.Disposed
                : ApplicationRuntimeState.Faulted;
        }
        finally
        {
            _lifecycleGate.Release();
        }
        return errors;
    }

    private void BuildShell()
    {
        if (_settings is null || _library is null || _downloads is null)
            return;
        var settings = _settings.Presentation;
        var library = _library.Presentation;
        RipperWorks.GameMaintenance.IGameMaintenanceService? maintenanceService = null;
        if (_frameworkStateProvider is OrganizerRepository repo)
        {
            maintenanceService = new RipperWorks.GameMaintenance.GameMaintenanceService(
                new SystemGameProcessAdapter(),
                new OrganizerGameMaintenanceManagedStateProvider(repo));
        }

        _diagnosticsViewModel = new DiagnosticsViewModel(
            settings,
            library,
            _settings.SessionEventsPresentation,
            _databasePath ?? string.Empty,
            _logsDirectory ?? string.Empty,
            library.RecoveryRequired,
            () => _library.RequestRecoveryAsync(),
            _frameworkStateProvider,
            maintenanceService: maintenanceService);
        _mainViewModel = new MainWindowViewModel(
            library,
            _downloads.Presentation,
            settings,
            _diagnosticsViewModel,
            _settings.Localization,
            _shortlistStore,
            _localStateService,
            _settings.NexusApi,
            _settings.Credentials,
            _nexusRequirements,
            _nexusRequirements);
    }

    private void DisposeShell()
    {
        _mainViewModel?.Dispose();
        _mainViewModel = null;
        _diagnosticsViewModel?.Dispose();
        _diagnosticsViewModel = null;
    }

    private void SingleInstance_OnMessageReceived(
        object? sender,
        InstanceMessage message)
    {
        if (_state == ApplicationRuntimeState.Started)
            ActivationRequested?.Invoke(this, message);
    }

    private void Observe(string stage, Exception exception)
    {
        try
        {
            _observeError(stage, exception);
        }
        catch
        {
            // Error observation must never interrupt remaining cleanup.
        }
    }
}
