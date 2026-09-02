using RipperWorks.Core;
using RipperWorks.Infrastructure;
using RipperWorks.Organizer;
using RipperWorks.Organizer.GameOperations;

namespace RipperWorks.App.Services;

public sealed class GameOperationsModuleHost :
    IGameOperationsModuleBoundary
{
    private readonly OrganizerRepository _repository;
    private readonly StartupDataHygieneRunner _startupDataHygiene;
    private readonly GameProfileService _profiles;
    private readonly GameLauncherService _launcher;
    private readonly object _coordinatorGate = new();
    private GameOperationCoordinator? _shadowCoordinator;
    private bool _started;
    private bool _disposed;
    private Task? _disposeTask;

    public GameOperationsModuleHost(
        OrganizerRepository repository,
        StartupDataHygieneRunner startupDataHygiene,
        GameProfileService profiles,
        GameLauncherService launcher)
    {
        _repository = repository;
        _startupDataHygiene = startupDataHygiene;
        _profiles = profiles;
        _launcher = launcher;
        // RF-06: construct coordinator without journal path / filesystem I/O.
        // Production UI never submits; journal opens only when tests configure a path.
        _shadowCoordinator = GameOperationCoordinator.CreateLazyShadow();
    }

    public string Name => "Game Operations";
    public GameProfileRecord? CurrentGameProfile { get; private set; }
    public bool RecoveryRequired { get; private set; }

    /// <summary>
    /// RF-06 shadow coordinator. Not used by Library/UI install routes.
    /// </summary>
    public GameOperationCoordinator ShadowCoordinator
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_coordinatorGate)
            {
                return _shadowCoordinator
                    ?? throw new ObjectDisposedException(nameof(GameOperationsModuleHost));
            }
        }
    }

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException($"{Name} is already started.");

        await _startupDataHygiene.RunWithOrganizerInitializationAsync(
            _repository.InitializeAsync,
            cancellationToken);
        await _repository.ClassifyInterruptedInstallOperationsAsync(
            cancellationToken);
        CurrentGameProfile =
            await _repository.LoadGameProfileAsync(cancellationToken);
        RecoveryRequired =
            await _repository.HasRecoveryRequiredOperationsAsync(cancellationToken);
        // RF-06: do not open journal v2 on startup.
        _started = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _started = false;
        await _launcher.StopAsync(cancellationToken).ConfigureAwait(false);
        GameOperationCoordinator? coordinator;
        lock (_coordinatorGate)
            coordinator = _shadowCoordinator;
        if (coordinator is not null)
            await coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<GameProfileValidationResult> ValidateGameProfileAsync(
        string gameRoot,
        string libraryRoot,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return _profiles.ValidateAsync(
            gameRoot,
            libraryRoot,
            cancellationToken);
    }

    public async Task<GameProfileRecord?> SaveGameProfileAsync(
        GameProfileValidationResult validation,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        CurrentGameProfile = await _repository.SaveGameProfileAsync(
            validation,
            cancellationToken);
        return CurrentGameProfile;
    }

    public Task<GameLaunchResult> LaunchGameAsync(
        GameProfileRecord? profile,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return _launcher.LaunchAsync(profile, cancellationToken);
    }

    public bool CanLaunchGame(GameProfileRecord? profile) =>
        _started && _launcher.CanLaunch(profile);

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_coordinatorGate)
        {
            if (_disposeTask is not null)
            {
                disposeTask = _disposeTask;
            }
            else
            {
                _disposeTask = DisposeCoreAsync();
                disposeTask = _disposeTask;
            }
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        GameOperationCoordinator? coordinator;
        lock (_coordinatorGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _started = false;
            coordinator = _shadowCoordinator;
            _shadowCoordinator = null;
        }

        await _launcher.DisposeAsync().ConfigureAwait(false);
        if (coordinator is not null)
            await coordinator.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            throw new InvalidOperationException(
                $"{Name} has not completed startup.");
        }
    }
}
