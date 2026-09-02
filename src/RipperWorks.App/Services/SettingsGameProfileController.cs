using System.Globalization;
using System.IO;
using RipperWorks.Core;
using RipperWorks.GameMaintenance;
using RipperWorks.Infrastructure;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

/// <summary>
/// Game-profile presentation operations for Settings. Not a settings authority.
/// Uses the Game Operations module boundary and the Settings presentation
/// edit contract; it is not a repository or lifecycle owner.
/// </summary>
internal sealed class SettingsGameProfileController
{
    private readonly IGameOperationsModuleBoundary? _operations;
    private readonly ISettingsGameProfilePresentation _edit;
    private readonly LocalizationService _localization;
    private readonly IUserDialogService _dialogs;
    private readonly IFolderPickerService _folderPicker;
    private readonly StartupExceptionLogger? _logger;
    private readonly Action<string, string?>? _recordSessionEvent;
    private readonly IGameMaintenanceService _maintenanceService;
    private readonly IGameMaintenanceManagedStateProvider _managedStateProvider;
    private GameProfileValidationResult? _lastValidation;
    private DateTime? _lastValidationAtUtc;
    private string? _lastValidationGameRoot;
    private string? _lastValidationLibraryRoot;
    private bool? _foreignModsDetected;

    public SettingsGameProfileController(
        IGameOperationsModuleBoundary? operations,
        ISettingsGameProfilePresentation edit,
        LocalizationService localization,
        IUserDialogService dialogs,
        IFolderPickerService folderPicker,
        StartupExceptionLogger? logger = null,
        Action<string, string?>? recordSessionEvent = null,
        IGameMaintenanceService? maintenanceService = null,
        IGameMaintenanceManagedStateProvider? managedStateProvider = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(folderPicker);
        _operations = operations;
        _edit = edit;
        _localization = localization;
        _dialogs = dialogs;
        _folderPicker = folderPicker;
        _logger = logger;
        _recordSessionEvent = recordSessionEvent;
        _maintenanceService = maintenanceService ?? new GameMaintenanceService(new SystemGameProcessAdapter());
        _managedStateProvider = managedStateProvider ?? NullGameMaintenanceManagedStateProvider.Instance;
        if (operations?.CurrentGameProfile is
            {
                ValidationState: GameProfileValidationState.Valid
            } savedGameProfile)
        {
            _lastValidation = new GameProfileValidationResult
            {
                IsValid = true,
                GameRoot = savedGameProfile.GameRoot,
                ExecutablePath = savedGameProfile.ExecutablePath,
                GameFolderFound = true,
                ExecutableFound = true,
                WriteAccessConfirmed = true,
                NoForeignModifications = false
            };
            _lastValidationAtUtc = savedGameProfile.LastValidatedAtUtc;
            _foreignModsDetected = null;
        }
    }

    public GameProfileValidationResult? LastValidation => _lastValidation;
    public DateTime? LastValidationAtUtc => _lastValidationAtUtc;

    public void ClearValidation()
    {
        _lastValidation = null;
        _lastValidationAtUtc = null;
        _lastValidationGameRoot = null;
        _lastValidationLibraryRoot = null;
        _foreignModsDetected = null;
        _edit.NotifyGameValidationChanged();
    }

    public async Task BrowseGameAsync()
    {
        var selected = _folderPicker.Pick(
            _localization.Get("SelectGameFolder"),
            _edit.Cyberpunk2077Root);
        if (selected is null)
            return;
        _edit.Cyberpunk2077Root = selected;
        await ValidateGameAsync().ConfigureAwait(true);
    }

    public async Task ValidateGameAsync()
    {
        if (_operations is null)
            return;
        _recordSessionEvent?.Invoke("EventGameValidationStarted", null);
        var normGame = SettingsSaveFlow.NormalizeOptionalPath(_edit.Cyberpunk2077Root);
        var normLib = SettingsSaveFlow.NormalizeOptionalPath(_edit.LibraryRoot);
        _lastValidation = await _operations.ValidateGameProfileAsync(
            _edit.Cyberpunk2077Root,
            _edit.LibraryRoot).ConfigureAwait(true);
        _lastValidationAtUtc = DateTime.UtcNow;
        _lastValidationGameRoot = normGame;
        _lastValidationLibraryRoot = normLib;

        if (_lastValidation.IsValid && !string.IsNullOrWhiteSpace(normGame))
        {
            try
            {
                var scan = await _maintenanceService.ScanAsync(normGame).ConfigureAwait(true);
                _foreignModsDetected = scan.Status == GameMaintenanceScanStatus.ForeignModificationDetected;
            }
            catch
            {
                _foreignModsDetected = null;
            }
        }
        else
        {
            _foreignModsDetected = null;
        }

        _edit.SetGameStatus(GameValidationStatus);
        _recordSessionEvent?.Invoke(
            "EventGameValidationCompleted",
            _localization.Get(
                _lastValidation.IsValid
                    ? "EventSuccessful"
                    : "EventFailed"));
        _edit.NotifyGameValidationChanged();
    }

    public bool HasOperations => _operations is not null;

    public async Task<GameProfileRecord?> ValidateAndSaveGameProfileAsync(
        string normalizedGameRoot,
        string normalizedLibraryRoot)
    {
        if (_operations is null)
            return null;

        GameProfileValidationResult validation;
        if (_lastValidation is { IsValid: true } cached &&
            string.Equals(
                _lastValidationGameRoot,
                normalizedGameRoot,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                _lastValidationLibraryRoot,
                normalizedLibraryRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            validation = cached;
        }
        else
        {
            _recordSessionEvent?.Invoke("EventGameValidationStarted", null);
            validation = await _operations.ValidateGameProfileAsync(
                normalizedGameRoot,
                normalizedLibraryRoot).ConfigureAwait(true);
            _lastValidation = validation;
            _lastValidationAtUtc = DateTime.UtcNow;
            _lastValidationGameRoot = normalizedGameRoot;
            _lastValidationLibraryRoot = normalizedLibraryRoot;
            _recordSessionEvent?.Invoke(
                "EventGameValidationCompleted",
                _localization.Get(
                    validation.IsValid
                        ? "EventSuccessful"
                        : "EventFailed"));
        }

        if (!validation.IsValid)
        {
            var errorMessage = FormatValidationError(validation);
            _edit.SetGameStatus(errorMessage);
            _dialogs.ShowError(errorMessage);
            return null;
        }

        if (_operations.CurrentGameProfile is { GameRoot: { Length: > 0 } currentRoot } &&
            !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentRoot)),
                normalizedGameRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            if (await _managedStateProvider.HasManagedInstallationsAsync(currentRoot).ConfigureAwait(true))
            {
                var errorMessage = _localization.Get("SettingsCannotChangeGameRootWhileManagedModsInstalled");
                _edit.SetGameStatus(errorMessage);
                _dialogs.ShowError(errorMessage);
                return null;
            }
        }

        var profile = await _operations.SaveGameProfileAsync(validation).ConfigureAwait(true);
        if (profile is null)
        {
            var errorMessage = _localization.Get("GameValidationValidationFailed");
            _edit.SetGameStatus(errorMessage);
            _dialogs.ShowError(errorMessage);
            return null;
        }

        try
        {
            var scan = await _maintenanceService.ScanAsync(normalizedGameRoot).ConfigureAwait(true);
            _foreignModsDetected = scan.Status == GameMaintenanceScanStatus.ForeignModificationDetected;
        }
        catch
        {
            _foreignModsDetected = null;
        }

        _edit.CurrentGameProfile = profile;
        _lastValidation = new GameProfileValidationResult
        {
            IsValid = true,
            GameRoot = profile.GameRoot,
            ExecutablePath = profile.ExecutablePath,
            GameFolderFound = true,
            ExecutableFound = true,
            WriteAccessConfirmed = true,
            NoForeignModifications = false
        };
        _lastValidationAtUtc = profile.LastValidatedAtUtc;
        _lastValidationGameRoot = normalizedGameRoot;
        _lastValidationLibraryRoot = normalizedLibraryRoot;
        _edit.SetGameStatus(GameValidationStatus);
        _recordSessionEvent?.Invoke("EventGameProfileSaved", profile.GameRoot);
        _edit.NotifyGameValidationChanged();
        _edit.NotifyGameLaunchChanged();
        return profile;
    }

    public async Task LaunchGameAsync()
    {
        if (_operations is null || _edit.CurrentGameProfile is null)
            return;

        try
        {
            var scan = await _maintenanceService.ScanAsync(_edit.CurrentGameProfile.GameRoot).ConfigureAwait(true);
            if (scan.Status == GameMaintenanceScanStatus.ForeignModificationDetected)
            {
                var title = _localization.Get("GameFolderRequiresMaintenanceTitle");
                var message = _localization.Get("GameFolderRequiresMaintenanceMessage");
                var cancelLabel = _localization.Get("Cancel");
                var diagLabel = _localization.Get("SettingsGoToDiagnostics");
                var navigate = _dialogs.Confirm(message, title, cancelLabel, diagLabel);
                if (navigate)
                {
                    _edit.RequestNavigateDiagnostics?.Invoke();
                }
                return;
            }
            if (scan.Status == GameMaintenanceScanStatus.GameRunning)
            {
                _dialogs.ShowError(_localization.Get("GameValidationGameRunning"));
                return;
            }
            if (scan.Status == GameMaintenanceScanStatus.ScanFailed ||
                scan.Status == GameMaintenanceScanStatus.InvalidGameRoot)
            {
                _dialogs.ShowError(scan.ErrorMessage ?? _localization.Get("GameValidationValidationFailed"));
                return;
            }
        }
        catch (Exception ex)
        {
            _logger?.Log("GameLauncher", ex);
            _dialogs.ShowError(_localization.Get("GameValidationValidationFailed"));
            return;
        }

        var result = await _operations.LaunchGameAsync(
                _edit.CurrentGameProfile)
            .ConfigureAwait(true);
        _edit.NotifyGameLaunchChanged();
        var statusMessage = _localization.Get($"GameLaunch{result.Status}");
        _edit.SetGameStatus(statusMessage);
        if (result.Status == GameLaunchStatus.Started)
        {
            _recordSessionEvent?.Invoke("EventGameStarted", null);
            return;
        }

        if (result.Status == GameLaunchStatus.Failed)
        {
            _recordSessionEvent?.Invoke("EventGameLaunchFailed", statusMessage);
            var exception = new InvalidOperationException(
                result.ErrorMessage ?? statusMessage);
            var logPath = _logger?.Log("GameLauncher", exception);
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                statusMessage = string.Format(
                    _localization.Get("GameLaunchFailedWithLog"),
                    logPath);
            }
        }

        _dialogs.ShowError(statusMessage);
    }

    public bool CanValidate => _operations is not null;
    public bool CanLaunch =>
        _operations?.CanLaunchGame(_edit.CurrentGameProfile) == true;

    public string GameValidationStatus => _lastValidation is null
        ? _localization.Get("GameNotValidated")
        : _lastValidation.IsValid
            ? _localization.Get("GameValidationValid")
            : FormatValidationError(_lastValidation);

    public string GameFolderCheck => FormatCheck(
        _lastValidation?.GameFolderFound,
        "GameFolderCheck");
    public string ExecutableCheck => FormatCheck(
        _lastValidation?.ExecutableFound,
        "ExecutableCheck");
    public string WriteAccessCheck => FormatCheck(
        _lastValidation?.WriteAccessConfirmed,
        "WriteAccessCheck");
    public string CleanModsCheck => FormatCheck(
        _foreignModsDetected.HasValue ? !_foreignModsDetected.Value : null,
        "CleanModsCheck");
    public bool IsForeignModsWarningVisible => _foreignModsDetected == true;
    public bool? ForeignModsDetected => _foreignModsDetected;
    public string GameValidationShortStatus => _lastValidation switch
    {
        { IsValid: true } => _localization.Get("ValidationChecked"),
        not null => _localization.Get("ValidationRequired"),
        _ => _localization.Get("ValidationNotChecked")
    };
    public string GameRootDisplay =>
        string.IsNullOrWhiteSpace(_edit.Cyberpunk2077Root)
            ? "—"
            : _edit.Cyberpunk2077Root;
    public string GameFolderStateValue => FormatState(
        _lastValidation?.GameFolderFound,
        "DiagnosticsFound",
        "DiagnosticsNotFound");
    public string ExecutableStateValue => FormatState(
        _lastValidation?.ExecutableFound,
        "DiagnosticsFound",
        "DiagnosticsNotFound");
    public string WriteAccessStateValue => FormatState(
        _lastValidation?.WriteAccessConfirmed,
        "DiagnosticsAvailable",
        "DiagnosticsUnavailable");
    public string ForeignModsStateValue =>
        _foreignModsDetected switch
        {
            true => _localization.Get("DiagnosticsDetected"),
            false => _localization.Get("DiagnosticsNotDetected"),
            _ => _localization.Get("DiagnosticsUnknown")
        };
    public string ProfileStateValue =>
        _lastValidation?.IsValid == true ||
        _edit.CurrentGameProfile?.ValidationState == GameProfileValidationState.Valid
            ? _localization.Get("DiagnosticsReady")
            : _localization.Get("ValidationRequired");
    public string LastValidationValue => _lastValidationAtUtc?
        .ToLocalTime()
        .ToString(
            "g",
            CultureInfo.GetCultureInfo(_localization.CurrentLanguage))
        ?? "—";

    private string FormatCheck(bool? value, string key) =>
        $"{(value is null ? "—" : value.Value ? "✓" : "✕")} " +
        _localization.Get(key);

    private string FormatValidationError(GameProfileValidationResult result) =>
        _localization.Get($"GameValidation{result.ErrorCode}");

    private string FormatState(
        bool? value,
        string trueKey,
        string falseKey) =>
        value switch
        {
            true => _localization.Get(trueKey),
            false => _localization.Get(falseKey),
            _ => _localization.Get("DiagnosticsUnknown")
        };
}
