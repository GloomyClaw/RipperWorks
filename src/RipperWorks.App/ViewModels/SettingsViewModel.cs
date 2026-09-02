using System.IO;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Infrastructure;

namespace RipperWorks.App.ViewModels;

/// <summary>
/// Settings presentation/edit buffer. Not the runtime settings authority.
/// </summary>
public sealed class SettingsViewModel :
    ObservableObject,
    ISettingsGameProfilePresentation,
    IDisposable
{
    private readonly SettingsSession _session;
    private readonly LocalizationService _localization;
    private readonly ThemeService _theme;
    private readonly IUserDialogService _dialogs;
    private readonly IFolderPickerService _folderPicker;
    private readonly SettingsCredentialController _credentialsUi;
    private readonly SynchronizationContext? _uiContext;
    private SettingsGameProfileController _game = null!;
    private string _cyberpunk2077Root;
    private string _libraryRoot;
    private string _downloaderTempRoot;
    private int _concurrentDownloads;
    private NexusBrowserMode _selectedNexusBrowserMode;
    private string _nexusApiKey = string.Empty;
    private string _nexusStatus = string.Empty;
    private string _selectedLanguage;
    private string _selectedTheme;
    private string _statusMessage = string.Empty;
    private GameProfileRecord? _currentGameProfile;
    private bool _disposed;

    public SettingsViewModel(
        SettingsSession session,
        LocalizationService localization,
        ThemeService theme,
        IUserDialogService dialogs,
        IFolderPickerService folderPicker,
        IGameOperationsModuleBoundary? gameOperations = null,
        StartupExceptionLogger? logger = null,
        Action<string, string?>? recordSessionEvent = null,
        IProtectedCredentialStore? credentials = null,
        INexusApiClient? nexusApi = null,
        INxmProtocolRegistration? nxmProtocol = null,
        string? executablePath = null,
        RipperWorks.GameMaintenance.IGameMaintenanceService? maintenanceService = null,
        RipperWorks.GameMaintenance.IGameMaintenanceManagedStateProvider? managedStateProvider = null)
    {
        _session = session;
        _localization = localization;
        _theme = theme;
        _dialogs = dialogs;
        _folderPicker = folderPicker;
        // Capture presentation context at construction (WPF UI thread).
        // SettingsSession never depends on Dispatcher; we marshal here.
        _uiContext = SynchronizationContext.Current;
        _credentialsUi = new SettingsCredentialController(
            credentials,
            nexusApi,
            localization,
            nxmProtocol,
            executablePath ?? Environment.ProcessPath ?? string.Empty);
        _currentGameProfile = gameOperations?.CurrentGameProfile;
        var settings = session.Current;
        _cyberpunk2077Root =
            _currentGameProfile?.GameRoot ?? settings.Cyberpunk2077Root;
        _libraryRoot = settings.LibraryRoot;
        _downloaderTempRoot = settings.DownloaderTempRoot;
        _concurrentDownloads = settings.ConcurrentDownloads;
        _selectedNexusBrowserMode = settings.NexusBrowser;
        _selectedLanguage = settings.Language;
        _selectedTheme = settings.Theme;
        BrowseGameCommand = new AsyncRelayCommand(
            () => _game.BrowseGameAsync());
        BrowseLibraryCommand = new RelayCommand(BrowseLibraryFolder);
        BrowseDownloaderTempCommand = new RelayCommand(BrowseDownloaderTempFolder);
        ValidateGameCommand = new AsyncRelayCommand(
            () => _game.ValidateGameAsync(),
            () => _game.CanValidate);
        LaunchGameCommand = new AsyncRelayCommand(
            () => _game.LaunchGameAsync(),
            () => _game.CanLaunch);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        NavigateToDiagnosticsCommand = new RelayCommand(
            () => RequestNavigateDiagnostics?.Invoke());
        ValidateNexusApiKeyCommand = new AsyncRelayCommand(
            () => _credentialsUi.ValidateAsync(
                NexusApiKey,
                value => NexusStatus = value),
            () => _credentialsUi.CanValidate);
        RegisterNxmCommand = new RelayCommand(
            () => _credentialsUi.Register(
                value => NexusStatus = value,
                () => OnPropertyChanged(nameof(NxmRegistrationStatus))),
            () => _credentialsUi.CanRegister);
        UnregisterNxmCommand = new RelayCommand(
            () => _credentialsUi.Unregister(
                value => NexusStatus = value,
                () => OnPropertyChanged(nameof(NxmRegistrationStatus))),
            () => _credentialsUi.CanUnregister);
        _game = new SettingsGameProfileController(
            gameOperations,
            this,
            localization,
            dialogs,
            folderPicker,
            logger,
            recordSessionEvent,
            maintenanceService,
            managedStateProvider);
        LanguageChoices =
        [
            new(SupportedLanguages.Russian, _localization.Get("Russian")),
            new(SupportedLanguages.English, _localization.Get("English"))
        ];
        ThemeChoices =
        [
            new(SupportedThemes.System, _localization.Get("ThemeSystem")),
            new(SupportedThemes.Light, _localization.Get("ThemeLight")),
            new(SupportedThemes.Dark, _localization.Get("ThemeDark"))
        ];
        NexusBrowserChoices =
        [
            new(NexusBrowserMode.Internal, _localization.Get("NexusBrowserInternal")),
            new(NexusBrowserMode.External, _localization.Get("NexusBrowserExternal"))
        ];
        _localization.LanguageChanged += Localization_OnLanguageChanged;
        _session.SnapshotChanged += OnSessionSnapshotChanged;
    }

    /// <summary>
    /// Raised only when the Settings JSON save succeeded and either no
    /// credential save was attempted or the credential save also succeeded.
    /// MainWindow clears PasswordBox on this event — partial credential
    /// failure must not raise it.
    /// </summary>
    public event EventHandler<RipperWorksSettings>? SettingsSaved;
    public RipperWorksSettings CurrentSettings => _session.Current;
    public GameProfileRecord? CurrentGameProfile
    {
        get => _currentGameProfile;
        private set => SetProperty(ref _currentGameProfile, value);
    }
    public LocalizationService Localization => _localization;

    public string Cyberpunk2077Root
    {
        get => _cyberpunk2077Root;
        set
        {
            if (!SetProperty(ref _cyberpunk2077Root, value))
                return;
            _game.ClearValidation();
        }
    }
    public string LibraryRoot
    {
        get => _libraryRoot;
        set
        {
            if (!SetProperty(ref _libraryRoot, value))
                return;
            _game.ClearValidation();
        }
    }
    public string DownloaderTempRoot
    {
        get => _downloaderTempRoot;
        set => SetProperty(ref _downloaderTempRoot, value);
    }
    public int ConcurrentDownloads
    {
        get => _concurrentDownloads;
        set => SetProperty(ref _concurrentDownloads, Math.Clamp(value, 1, 8));
    }
    public NexusBrowserMode SelectedNexusBrowserMode
    {
        get => _selectedNexusBrowserMode;
        set => SetProperty(ref _selectedNexusBrowserMode, value);
    }
    public string NexusApiKey
    {
        get => _nexusApiKey;
        set => SetProperty(ref _nexusApiKey, value);
    }
    public string NexusStatus
    {
        get => _nexusStatus;
        private set => SetProperty(ref _nexusStatus, value);
    }
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !SupportedLanguages.IsSupported(value))
                return;
            SetProperty(ref _selectedLanguage, value);
        }
    }
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !SupportedThemes.IsSupported(value))
                return;
            SetProperty(ref _selectedTheme, value);
        }
    }
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public IReadOnlyList<ChoiceItem> LanguageChoices { get; private set; } = [];
    public IReadOnlyList<ChoiceItem> ThemeChoices { get; private set; } = [];
    public IReadOnlyList<NexusBrowserModeChoice> NexusBrowserChoices { get; private set; } = [];

    public string Header => _localization.Get("Settings");
    public string PathsSectionLabel => _localization.Get("PathsSection");
    public string InterfaceSectionLabel => _localization.Get("InterfaceSection");
    public string NexusSectionLabel => _localization.Get("NexusSection");
    public string NexusApiKeyLabel => _localization.Get("NexusApiKey");
    public string ValidateNexusApiKeyLabel => _localization.Get("ValidateNexusApiKey");
    public string DownloaderTempFolderLabel => _localization.Get("DownloaderTempFolder");
    public string ConcurrentDownloadsLabel => _localization.Get("ConcurrentDownloads");
    public string NexusBrowserLabel => _localization.Get("NexusBrowserLabel");
    public string NxmHandlerLabel => _localization.Get("NxmHandler");
    public string RegisterNxmLabel => _localization.Get("RegisterNxm");
    public string UnregisterNxmLabel => _localization.Get("UnregisterNxm");
    public string NxmRegistrationStatus => _credentialsUi.RegistrationStatus;
    public string GameFolderLabel => _localization.Get("GameFolder");
    public string LibraryFolderLabel => _localization.Get("LibraryFolder");
    public string LanguageLabel => _localization.Get("Language");
    public string ThemeLabel => _localization.Get("Theme");
    public string BrowseLabel => _localization.Get("Browse");
    public string SaveLabel => _localization.Get("Save");
    public string ValidateGameLabel => _localization.Get("ValidateGame");
    public string LaunchGameLabel => _localization.Get("LaunchGame");
    public string GameValidationStatus => _game.GameValidationStatus;
    public string GameFolderCheck => _game.GameFolderCheck;
    public string ExecutableCheck => _game.ExecutableCheck;
    public string WriteAccessCheck => _game.WriteAccessCheck;
    public string CleanModsCheck => _game.CleanModsCheck;
    public string GameValidationShortStatus => _game.GameValidationShortStatus;
    public string GameRootDisplay => _game.GameRootDisplay;
    public string GameFolderStateValue => _game.GameFolderStateValue;
    public string ExecutableStateValue => _game.ExecutableStateValue;
    public string WriteAccessStateValue => _game.WriteAccessStateValue;
    public string ForeignModsStateValue => _game.ForeignModsStateValue;
    public string ProfileStateValue => _game.ProfileStateValue;
    public string LastValidationValue => _game.LastValidationValue;

    public bool IsForeignModsWarningVisible => _game.IsForeignModsWarningVisible;
    public string ForeignModsWarningHeading => _localization.Get("SettingsForeignModsWarningHeading");
    public string ForeignModsWarningMessage => _localization.Get("SettingsForeignModsWarningMessage");
    public string GoToDiagnosticsLabel => _localization.Get("SettingsGoToDiagnostics");

    public Action? RequestNavigateDiagnostics { get; set; }
    public RelayCommand NavigateToDiagnosticsCommand { get; }

    public AsyncRelayCommand BrowseGameCommand { get; }
    public RelayCommand BrowseLibraryCommand { get; }
    public RelayCommand BrowseDownloaderTempCommand { get; }
    public AsyncRelayCommand ValidateGameCommand { get; }
    public AsyncRelayCommand LaunchGameCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ValidateNexusApiKeyCommand { get; }
    public RelayCommand RegisterNxmCommand { get; }
    public RelayCommand UnregisterNxmCommand { get; }

    public async Task InitializeDownloaderSettingsAsync()
    {
        NexusApiKey = string.Empty;
        await _credentialsUi.InitializeStatusAsync(value => NexusStatus = value);
        OnPropertyChanged(nameof(NxmRegistrationStatus));
    }

    public void RefreshLaunchAvailability() =>
        LaunchGameCommand.NotifyCanExecuteChanged();

    public async Task SaveAsync()
    {
        var normalizedGameRoot = SettingsSaveFlow.NormalizeOptionalPath(Cyberpunk2077Root);
        var normalizedLibraryRoot = SettingsSaveFlow.NormalizeOptionalPath(LibraryRoot);

        if (!string.IsNullOrWhiteSpace(normalizedLibraryRoot) &&
            !Directory.Exists(normalizedLibraryRoot))
        {
            if (!_dialogs.Confirm(
                    _localization.Get("CreateLibrary"),
                    _localization.Get("CreateLibraryTitle")))
                return;
            Directory.CreateDirectory(normalizedLibraryRoot);
        }

        string targetGameRoot;
        if (string.IsNullOrWhiteSpace(normalizedGameRoot))
        {
            targetGameRoot = string.Empty;
        }
        else if (_game.HasOperations)
        {
            var savedProfile = await _game.ValidateAndSaveGameProfileAsync(
                normalizedGameRoot,
                normalizedLibraryRoot).ConfigureAwait(true);
            if (savedProfile is null)
            {
                // Validation or profile persistence failed; keep edit buffer as is.
                return;
            }
            targetGameRoot = savedProfile.GameRoot;
        }
        else
        {
            targetGameRoot = normalizedGameRoot;
        }

        await SettingsSaveFlow.SaveAsync(
            _session,
            _localization,
            _theme,
            _dialogs,
            _credentialsUi,
            targetGameRoot,
            normalizedLibraryRoot,
            DownloaderTempRoot,
            ConcurrentDownloads,
            SelectedNexusBrowserMode,
            SelectedLanguage,
            SelectedTheme,
            NexusApiKey,
            ApplySnapshotToBuffer,
            value => NexusApiKey = value,
            value => NexusStatus = value,
            value => StatusMessage = value,
            settings => SettingsSaved?.Invoke(this, settings)).ConfigureAwait(true);
    }

    private void OnSessionSnapshotChanged(
        object? sender,
        RipperWorksSettings snapshot)
    {
        if (_disposed)
            return;
        if (_uiContext is null ||
            ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            ApplySnapshotToBuffer(snapshot);
            return;
        }

        _uiContext.Post(
            static state =>
            {
                var (vm, snap) = ((SettingsViewModel, RipperWorksSettings))state!;
                if (!vm._disposed)
                    vm.ApplySnapshotToBuffer(snap);
            },
            (this, snapshot));
    }

    private void BrowseLibraryFolder()
    {
        var selected = _folderPicker.Pick(
            _localization.Get("SelectLibraryFolder"),
            LibraryRoot);
        if (selected is not null)
            LibraryRoot = selected;
    }

    private void BrowseDownloaderTempFolder()
    {
        var selected = _folderPicker.Pick(
            _localization.Get("SelectDownloaderTempFolder"),
            DownloaderTempRoot);
        if (selected is not null)
            DownloaderTempRoot = selected;
    }

    private void ApplySnapshotToBuffer(RipperWorksSettings settings)
    {
        Cyberpunk2077Root = settings.Cyberpunk2077Root;
        LibraryRoot = settings.LibraryRoot;
        DownloaderTempRoot = settings.DownloaderTempRoot;
        ConcurrentDownloads = settings.ConcurrentDownloads;
        SelectedNexusBrowserMode = settings.NexusBrowser;
        SelectedLanguage = settings.Language;
        SelectedTheme = settings.Theme;
    }

    private void RefreshChoices()
    {
        foreach (var choice in LanguageChoices)
        {
            if (choice.Value == SupportedLanguages.Russian)
                choice.Display = _localization.Get("Russian");
            else if (choice.Value == SupportedLanguages.English)
                choice.Display = _localization.Get("English");
        }
        foreach (var choice in ThemeChoices)
        {
            if (choice.Value == SupportedThemes.System)
                choice.Display = _localization.Get("ThemeSystem");
            else if (choice.Value == SupportedThemes.Light)
                choice.Display = _localization.Get("ThemeLight");
            else if (choice.Value == SupportedThemes.Dark)
                choice.Display = _localization.Get("ThemeDark");
        }
        foreach (var choice in NexusBrowserChoices)
        {
            if (choice.Value == NexusBrowserMode.Internal)
                choice.Display = _localization.Get("NexusBrowserInternal");
            else if (choice.Value == NexusBrowserMode.External)
                choice.Display = _localization.Get("NexusBrowserExternal");
        }
    }

    private void RefreshLocalizedProperties()
    {
        foreach (var name in LocalizedPropertyNames)
            OnPropertyChanged(name);
        RefreshValidationProperties();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
        _session.SnapshotChanged -= OnSessionSnapshotChanged;
        SettingsSaved = null;
    }

    GameProfileRecord? ISettingsGameProfilePresentation.CurrentGameProfile
    {
        get => CurrentGameProfile;
        set => CurrentGameProfile = value;
    }

    Action? ISettingsGameProfilePresentation.RequestNavigateDiagnostics =>
        RequestNavigateDiagnostics;

    void ISettingsGameProfilePresentation.SetGameStatus(string value) =>
        StatusMessage = value;

    void ISettingsGameProfilePresentation.NotifyGameValidationChanged() =>
        RefreshValidationProperties();

    void ISettingsGameProfilePresentation.NotifyGameLaunchChanged() =>
        LaunchGameCommand.NotifyCanExecuteChanged();

    private void Localization_OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        RefreshChoices();
        RefreshLocalizedProperties();
    }

    private void RefreshValidationProperties()
    {
        foreach (var name in ValidationPropertyNames)
            OnPropertyChanged(name);
    }

    private static readonly string[] LocalizedPropertyNames =
    [
        nameof(Header), nameof(PathsSectionLabel), nameof(InterfaceSectionLabel),
        nameof(NexusSectionLabel), nameof(NexusApiKeyLabel),
        nameof(ValidateNexusApiKeyLabel), nameof(DownloaderTempFolderLabel),
        nameof(ConcurrentDownloadsLabel), nameof(NexusBrowserLabel),
        nameof(NxmHandlerLabel), nameof(RegisterNxmLabel),
        nameof(UnregisterNxmLabel), nameof(NxmRegistrationStatus),
        nameof(GameFolderLabel), nameof(LibraryFolderLabel),
        nameof(LanguageLabel), nameof(ThemeLabel), nameof(BrowseLabel),
        nameof(SaveLabel), nameof(ValidateGameLabel),
        nameof(LaunchGameLabel), nameof(ForeignModsWarningHeading),
        nameof(ForeignModsWarningMessage), nameof(GoToDiagnosticsLabel)
    ];

    private static readonly string[] ValidationPropertyNames =
    [
        nameof(GameValidationStatus), nameof(GameFolderCheck),
        nameof(ExecutableCheck), nameof(WriteAccessCheck),
        nameof(CleanModsCheck), nameof(GameValidationShortStatus),
        nameof(GameRootDisplay), nameof(GameFolderStateValue),
        nameof(ExecutableStateValue), nameof(WriteAccessStateValue),
        nameof(ForeignModsStateValue), nameof(ProfileStateValue),
        nameof(LastValidationValue), nameof(IsForeignModsWarningVisible),
        nameof(ForeignModsWarningHeading), nameof(ForeignModsWarningMessage),
        nameof(GoToDiagnosticsLabel)
    ];
}

public sealed class NexusBrowserModeChoice : ObservableObject
{
    private string _display;

    public NexusBrowserMode Value { get; }
    public string Display
    {
        get => _display;
        set => SetProperty(ref _display, value);
    }

    public NexusBrowserModeChoice(NexusBrowserMode value, string display)
    {
        Value = value;
        _display = display;
    }
}
