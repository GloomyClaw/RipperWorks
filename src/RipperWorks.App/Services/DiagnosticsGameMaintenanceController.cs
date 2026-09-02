using System.IO;
using System.Windows.Input;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;
using RipperWorks.GameMaintenance;

namespace RipperWorks.App.Services;

public sealed class DiagnosticsGameMaintenanceController : ObservableObject, IDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly IGameMaintenanceService _maintenanceService;
    private readonly IGameMaintenanceDialogService _dialogService;
    private readonly IUserDialogService _dialogs;
    private readonly LocalizationService _localization;

    private GameMaintenanceScanResult? _lastScanResult;
    private bool _hasBackups;
    private bool _isBusy;
    private string? _customStatusMessage;
    private string? _lastObservedPersistedRoot;

    public DiagnosticsGameMaintenanceController(
        SettingsViewModel settings,
        IGameMaintenanceService maintenanceService,
        IGameMaintenanceDialogService dialogService,
        IUserDialogService dialogs,
        LocalizationService localization)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _maintenanceService = maintenanceService ?? throw new ArgumentNullException(nameof(maintenanceService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

        _lastObservedPersistedRoot = GetPersistedGameRoot();
        _settings.SettingsSaved += OnSettingsSaved;

        CheckGameCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(GetPersistedGameRoot()));
        ViewAndCleanCommand = new AsyncRelayCommand(ViewAndCleanAsync, () => !IsBusy && HasCandidates);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => CanRestoreBackup);
    }

    public void Dispose()
    {
        _settings.SettingsSaved -= OnSettingsSaved;
    }

    public string GetPersistedGameRoot()
    {
        var raw = _settings.CurrentGameProfile?.GameRoot;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = _settings.CurrentSettings?.Cyberpunk2077Root;
        }

        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim()));
        }
        catch
        {
            return raw.Trim();
        }
    }

    private void OnSettingsSaved(object? sender, RipperWorksSettings settings)
    {
        var currentPersistedRoot = GetPersistedGameRoot();
        if (!string.Equals(_lastObservedPersistedRoot, currentPersistedRoot, StringComparison.OrdinalIgnoreCase) ||
            (LastScanResult is not null && !string.Equals(LastScanResult.GameRoot, currentPersistedRoot, StringComparison.OrdinalIgnoreCase)))
        {
            InvalidateScanState();
        }
        _lastObservedPersistedRoot = currentPersistedRoot;
        CheckGameCommand.NotifyCanExecuteChanged();
    }

    public void InvalidateScanState()
    {
        LastScanResult = null;
        HasBackups = false;
        ViewAndCleanCommand.NotifyCanExecuteChanged();
        RestoreBackupCommand.NotifyCanExecuteChanged();
    }

    public AsyncRelayCommand CheckGameCommand { get; }
    public AsyncRelayCommand ViewAndCleanCommand { get; }
    public AsyncRelayCommand RestoreBackupCommand { get; }

    public string SectionHeading => _localization.Get("DiagnosticsGameMaintenanceHeading");
    public string CheckGameLabel => _localization.Get("DiagnosticsCheckGame");
    public string ViewAndCleanLabel => _localization.Get("DiagnosticsViewAndClean");
    public string RestoreBackupLabel => _localization.Get("DiagnosticsRestoreBackup");

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CheckGameCommand.NotifyCanExecuteChanged();
                ViewAndCleanCommand.NotifyCanExecuteChanged();
                RestoreBackupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public GameMaintenanceScanResult? LastScanResult
    {
        get => _lastScanResult;
        private set
        {
            if (SetProperty(ref _lastScanResult, value))
            {
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(DetailMessage));
                OnPropertyChanged(nameof(HasCandidates));
                OnPropertyChanged(nameof(CanRestoreBackup));
                ViewAndCleanCommand.NotifyCanExecuteChanged();
                RestoreBackupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasCandidates =>
        LastScanResult is not null &&
        LastScanResult.Candidates.Count > 0 &&
        LastScanResult.Status == GameMaintenanceScanStatus.ForeignModificationDetected &&
        string.Equals(LastScanResult.GameRoot, GetPersistedGameRoot(), StringComparison.OrdinalIgnoreCase);

    public bool CanRestoreBackup =>
        !IsBusy &&
        HasBackups &&
        (LastScanResult is null || LastScanResult.Status != GameMaintenanceScanStatus.ManagedContentPresent);

    public bool HasBackups
    {
        get => _hasBackups;
        private set
        {
            if (SetProperty(ref _hasBackups, value))
            {
                OnPropertyChanged(nameof(CanRestoreBackup));
                RestoreBackupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get
        {
            if (!string.IsNullOrEmpty(_customStatusMessage))
                return _customStatusMessage;

            if (LastScanResult is null)
                return "—";

            return LastScanResult.Status switch
            {
                GameMaintenanceScanStatus.Clean => _localization.Get("DiagnosticsCleanStatus"),
                GameMaintenanceScanStatus.ForeignModificationDetected => _localization.Get("DiagnosticsDetectedStatus"),
                GameMaintenanceScanStatus.ManagedContentPresent => _localization.Get("DiagnosticsGameMaintenanceManagedModsPresent"),
                GameMaintenanceScanStatus.GameRunning => _localization.Get("GameValidationGameRunning"),
                GameMaintenanceScanStatus.InvalidGameRoot => _localization.Get("GameValidationGameFolderNotFound"),
                _ => LastScanResult.ErrorMessage ?? _localization.Get("GameValidationValidationFailed")
            };
        }
    }

    public string DetailMessage
    {
        get
        {
            if (LastScanResult is null)
                return string.Empty;

            if (LastScanResult.Status == GameMaintenanceScanStatus.ManagedContentPresent)
                return _localization.Get("DiagnosticsGameMaintenanceManagedModsGuidance");

            if (LastScanResult.Candidates.Count == 0)
                return string.Empty;

            return string.Format(_localization.Get("DiagnosticsDetectedCount"), LastScanResult.Candidates.Count);
        }
    }

    public async Task ScanAsync()
    {
        var gameRoot = GetPersistedGameRoot();
        if (string.IsNullOrWhiteSpace(gameRoot))
            return;

        IsBusy = true;
        _customStatusMessage = null;
        try
        {
            var scan = await Task.Run(() => _maintenanceService.ScanAsync(gameRoot));
            LastScanResult = scan;

            var backups = await Task.Run(() => _maintenanceService.ListBackupsAsync(gameRoot));
            HasBackups = backups.Count > 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshBackupsAvailabilityAsync()
    {
        var gameRoot = GetPersistedGameRoot();
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            HasBackups = false;
            return;
        }

        try
        {
            var backups = await Task.Run(() => _maintenanceService.ListBackupsAsync(gameRoot));
            HasBackups = backups.Count > 0;
        }
        catch
        {
            HasBackups = false;
        }
    }

    public async Task ViewAndCleanAsync()
    {
        var gameRoot = GetPersistedGameRoot();
        if (LastScanResult is null ||
            LastScanResult.Candidates.Count == 0 ||
            string.IsNullOrWhiteSpace(gameRoot) ||
            !string.Equals(LastScanResult.GameRoot, gameRoot, StringComparison.OrdinalIgnoreCase))
        {
            InvalidateScanState();
            return;
        }

        var confirmed = _dialogService.PromptCleanupPreview(
            LastScanResult,
            out var createBackup);
        if (!confirmed)
            return;

        // Recheck persisted root identity immediately before executing mutation
        var rootBeforeMutation = GetPersistedGameRoot();
        if (string.IsNullOrWhiteSpace(rootBeforeMutation) ||
            !string.Equals(LastScanResult.GameRoot, rootBeforeMutation, StringComparison.OrdinalIgnoreCase))
        {
            InvalidateScanState();
            return;
        }

        IsBusy = true;
        try
        {
            var cleanupResult = await Task.Run(() =>
                _maintenanceService.CleanupAsync(
                    LastScanResult,
                    new GameMaintenanceCleanupOptions(createBackup)));

            if (cleanupResult.Status == GameMaintenanceCleanupStatus.Success)
            {
                var successMsg = createBackup && !string.IsNullOrEmpty(cleanupResult.BackupId)
                    ? string.Format(_localization.Get("GameMaintenanceCleanupSuccessWithBackup"), cleanupResult.DeletedCount)
                    : string.Format(_localization.Get("GameMaintenanceCleanupSuccess"), cleanupResult.DeletedCount);

                _dialogs.ShowInfo(successMsg);

                // Re-scan after cleanup
                await ScanAsync();
            }
            else
            {
                _dialogs.ShowError(cleanupResult.ErrorMessage ?? "Cleanup failed.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RestoreBackupAsync()
    {
        var gameRoot = GetPersistedGameRoot();
        if (string.IsNullOrWhiteSpace(gameRoot))
            return;

        if (LastScanResult?.Status == GameMaintenanceScanStatus.ManagedContentPresent)
        {
            _dialogs.ShowError(_localization.Get("DiagnosticsGameMaintenanceManagedModsGuidance"));
            return;
        }

        var backups = await Task.Run(() => _maintenanceService.ListBackupsAsync(gameRoot));
        if (backups.Count == 0)
        {
            _dialogs.ShowInfo(_localization.Get("GameMaintenanceNoBackupsFound"));
            HasBackups = false;
            return;
        }

        var selected = _dialogService.PromptRestoreSelection(backups);
        if (selected is null)
            return;

        // Recheck persisted root identity before executing preflight and restore
        var rootBeforeRestore = GetPersistedGameRoot();
        if (string.IsNullOrWhiteSpace(rootBeforeRestore) ||
            !string.Equals(gameRoot, rootBeforeRestore, StringComparison.OrdinalIgnoreCase))
        {
            InvalidateScanState();
            return;
        }

        IsBusy = true;
        try
        {
            var preflight = await Task.Run(() =>
                _maintenanceService.PreflightRestoreAsync(selected.BackupId, rootBeforeRestore));

            if (preflight.Status == GameMaintenanceRestorePreflightStatus.Conflict)
            {
                _dialogService.ShowConflicts(preflight.Conflicts);
                return;
            }

            if (preflight.Status != GameMaintenanceRestorePreflightStatus.Ready)
            {
                _dialogs.ShowError(preflight.ErrorMessage ?? "Restore preflight failed.");
                return;
            }

            var restoreResult = await Task.Run(() =>
                _maintenanceService.RestoreAsync(selected.BackupId, rootBeforeRestore));

            if (restoreResult.Status == GameMaintenanceRestoreStatus.Success)
            {
                var msg = string.Format(_localization.Get("GameMaintenanceRestoreSuccess"), restoreResult.RestoredCount);
                _dialogs.ShowInfo(msg);

                // Re-scan after restore
                await ScanAsync();
            }
            else
            {
                _dialogs.ShowError(restoreResult.ErrorMessage ?? "Restore failed.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(SectionHeading));
        OnPropertyChanged(nameof(CheckGameLabel));
        OnPropertyChanged(nameof(ViewAndCleanLabel));
        OnPropertyChanged(nameof(RestoreBackupLabel));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(DetailMessage));
    }
}
