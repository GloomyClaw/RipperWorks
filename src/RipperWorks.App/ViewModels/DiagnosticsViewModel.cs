using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.GameMaintenance;
using RipperWorks.Organizer;

namespace RipperWorks.App.ViewModels;

public enum DiagnosticEventFilter
{
    All,
    Errors,
    Warnings,
    CrashReports
}

public sealed class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;
    private readonly string _logsDirectory;
    private readonly IInstalledFrameworkStateProvider? _frameworkStateProvider;
    private readonly IGameDiagnosticSessionDiscoveryService _discoveryService;
    private readonly Action<string, string>? _shellRevealFile;
    private readonly Action<string>? _shellOpenDirectory;
    private readonly Func<string, string?>? _saveFileDialog;
    private readonly Func<string, DriveType>? _driveTypeResolver;

    private CancellationTokenSource? _refreshCts;
    private long _refreshGeneration;
    private int _selectedTabIndex = 1;
    private GameDiagnosticSessionItemViewModel? _selectedSession;
    private GameDiagnosticEventViewModel? _selectedEvent;
    private DiagnosticEventFilter _selectedFilter = DiagnosticEventFilter.All;
    private bool _isLoading;

    public DiagnosticsViewModel(
        SettingsViewModel settings,
        LibraryViewModel library,
        SessionEventLog sessionEvents,
        string databasePath,
        string logsDirectory,
        bool recoveryRequired = false,
        Func<Task>? requestRecovery = null,
        IInstalledFrameworkStateProvider? frameworkStateProvider = null,
        IGameDiagnosticSessionDiscoveryService? discoveryService = null,
        Action<string, string>? shellRevealFile = null,
        Action<string>? shellOpenDirectory = null,
        Func<string, string?>? saveFileDialog = null,
        Func<string, DriveType>? driveTypeResolver = null,
        IGameMaintenanceService? maintenanceService = null,
        IGameMaintenanceDialogService? maintenanceDialogService = null)
    {
        Settings = settings;
        Library = library;
        SessionEvents = sessionEvents;
        DatabasePath = databasePath;
        _logsDirectory = logsDirectory;
        _frameworkStateProvider = frameworkStateProvider;
        _discoveryService = discoveryService ?? new GameDiagnosticSessionDiscoveryService();
        _shellRevealFile = shellRevealFile;
        _shellOpenDirectory = shellOpenDirectory;
        _saveFileDialog = saveFileDialog;
        _driveTypeResolver = driveTypeResolver;

        var gameMaintenance = maintenanceService ?? new GameMaintenanceService(new SystemGameProcessAdapter());
        var maintenanceDialogs = maintenanceDialogService ?? new GameMaintenanceDialogService(settings.Localization, new UserDialogService(settings.Localization));
        Maintenance = new DiagnosticsGameMaintenanceController(
            settings,
            gameMaintenance,
            maintenanceDialogs,
            new UserDialogService(settings.Localization),
            settings.Localization);

        Library.SetRecoveryRequired(recoveryRequired);
        Library.PropertyChanged += Library_OnPropertyChanged;

        OpenGameFolderCommand = new RelayCommand(() => OpenFolder(Settings.Cyberpunk2077Root));
        OpenLibraryFolderCommand = new RelayCommand(() => OpenFolder(Settings.LibraryRoot));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(_logsDirectory));
        CopyStateCommand = new RelayCommand(CopyState);
        DumpLibrarySelectionTraceCommand = new RelayCommand(() => LibrarySelectionDiagnostics.Dump("Manual diagnostic dump"));
        RequestRecoveryCommand = new AsyncRelayCommand(requestRecovery ?? (() => Task.CompletedTask), () => RecoveryRequired);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenLogCommand = new RelayCommand(OpenSelectedSessionLog, () => SelectedSession != null && File.Exists(SelectedSession.FilePath));
        ExportLogCommand = new RelayCommand(ExportSelectedSessionLog, () => SelectedSession != null && File.Exists(SelectedSession.FilePath));
        OpenSourceCommand = new RelayCommand(OpenSelectedEventSource, () => SelectedEvent != null && SelectedEvent.HasSourcePath);

        FilterAllCommand = new RelayCommand(() => SelectedFilter = DiagnosticEventFilter.All);
        FilterErrorsCommand = new RelayCommand(() => SelectedFilter = DiagnosticEventFilter.Errors);
        FilterWarningsCommand = new RelayCommand(() => SelectedFilter = DiagnosticEventFilter.Warnings);
        FilterCrashReportsCommand = new RelayCommand(() => SelectedFilter = DiagnosticEventFilter.CrashReports);

        SelectRipperWorksTabCommand = new RelayCommand(() => SelectedTabIndex = 0);
        SelectGameSessionTabCommand = new RelayCommand(() => SelectedTabIndex = 1);

        _localization = settings.Localization;
        _localization.LanguageChanged += Localization_OnLanguageChanged;

        InitializeDiagnosticSources();
        _ = RefreshAsync();
    }

    public SettingsViewModel Settings { get; }
    public DiagnosticsGameMaintenanceController Maintenance { get; }
    public LibraryViewModel Library { get; }
    public SessionEventLog SessionEvents { get; }
    public string DatabasePath { get; }
    public string SchemaVersionValue => OrganizerRepository.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture);
    public string LogsDirectory => _logsDirectory;
    public bool RecoveryRequired => Library.RecoveryRequired;

    public ObservableCollection<DiagnosticSourceStatusViewModel> DiagnosticSources { get; } = [];
    public ObservableCollection<GameDiagnosticSessionItemViewModel> Sessions { get; } = [];
    public ObservableCollection<GameDiagnosticEventViewModel> FilteredEvents { get; } = [];

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                OnPropertyChanged(nameof(IsRipperWorksTabSelected));
                OnPropertyChanged(nameof(IsGameSessionTabSelected));
            }
        }
    }

    public bool IsRipperWorksTabSelected => SelectedTabIndex == 0;
    public bool IsGameSessionTabSelected => SelectedTabIndex == 1;

    public GameDiagnosticSessionItemViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                ApplyFilter();
                OpenLogCommand.NotifyCanExecuteChanged();
                ExportLogCommand.NotifyCanExecuteChanged();
                NotifySessionProperties();
            }
        }
    }

    public bool HasSelectedSession => SelectedSession != null;
    public bool SessionSummaryVisible => SelectedSession != null && !SelectedSession.IsUnsupported && !SelectedSession.IsMalformed;

    public bool IsCleanSession => SelectedSession != null && SelectedSession.HasCleanDiagnostics;
    public bool IsIncompleteSession => SelectedSession != null && (SelectedSession.IsIncomplete || (!SelectedSession.HasDiagnosticsSection && !SelectedSession.IsUnsupported && !SelectedSession.IsMalformed));
    public bool IsUnsupportedSession => SelectedSession != null && SelectedSession.IsUnsupported;
    public bool IsMalformedSession => SelectedSession != null && SelectedSession.IsMalformed;
    public bool HasUnavailableSources => SelectedSession != null && SelectedSession.HasUnavailableSources;
    public string UnavailableSourcesNotice => $"{_localization.Get("DiagnosticsSessionUnavailableSourcesNotice")} {SelectedSession?.UnavailableSourcesText}";

    public GameDiagnosticEventViewModel? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (SetProperty(ref _selectedEvent, value))
            {
                OpenSourceCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(HasSelectedEvent));
            }
        }
    }

    public bool HasSelectedEvent => SelectedEvent != null;

    public DiagnosticEventFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                ApplyFilter();
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterErrors));
                OnPropertyChanged(nameof(IsFilterWarnings));
                OnPropertyChanged(nameof(IsFilterCrashReports));
            }
        }
    }

    public bool IsFilterAll => SelectedFilter == DiagnosticEventFilter.All;
    public bool IsFilterErrors => SelectedFilter == DiagnosticEventFilter.Errors;
    public bool IsFilterWarnings => SelectedFilter == DiagnosticEventFilter.Warnings;
    public bool IsFilterCrashReports => SelectedFilter == DiagnosticEventFilter.CrashReports;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool HasSessions => Sessions.Count > 0;
    public bool HasNoSessions => Sessions.Count == 0;
    public bool HasFilteredEvents => FilteredEvents.Count > 0;
    public bool HasNoFilteredEvents => FilteredEvents.Count == 0;

    public RelayCommand OpenGameFolderCommand { get; }
    public RelayCommand OpenLibraryFolderCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand CopyStateCommand { get; }
    public RelayCommand DumpLibrarySelectionTraceCommand { get; }
    public AsyncRelayCommand RequestRecoveryCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand ExportLogCommand { get; }
    public RelayCommand OpenSourceCommand { get; }
    public RelayCommand FilterAllCommand { get; }
    public RelayCommand FilterErrorsCommand { get; }
    public RelayCommand FilterWarningsCommand { get; }
    public RelayCommand FilterCrashReportsCommand { get; }
    public RelayCommand SelectRipperWorksTabCommand { get; }
    public RelayCommand SelectGameSessionTabCommand { get; }

    public string Header => _localization.Get("Diagnostics");
    public string StateHeader => _localization.Get("DiagnosticsState");
    public string GameHeader => _localization.Get("DiagnosticsGame");
    public string LibraryHeader => _localization.Get("Library");
    public string RipperWorksHeader => "RipperWorks";
    public string EventLogHeader => _localization.Get("EventLog");
    public string NoSessionEvents => _localization.Get("NoSessionEvents");
    public string GameFolderLabel => _localization.Get("GameFolder");
    public string ExecutableLabel => "Cyberpunk2077.exe";
    public string WriteAccessLabel => _localization.Get("DiagnosticsWriteAccess");
    public string ForeignModsLabel => _localization.Get("DiagnosticsForeignMods");
    public string ProfileLabel => _localization.Get("DiagnosticsProfile");
    public string LastValidationLabel => _localization.Get("DiagnosticsLastValidation");
    public string LibraryFolderLabel => _localization.Get("LibraryFolder");
    public string DatabaseModsLabel => _localization.Get("DiagnosticsDatabaseMods");
    public string SkippedFoldersLabel => _localization.Get("DiagnosticsSkippedFolders");
    public string LastUpdateLabel => _localization.Get("DiagnosticsLastUpdate");
    public string DatabaseLabel => _localization.Get("DiagnosticsDatabase");
    public string SchemaVersionLabel => _localization.Get("DiagnosticsSchemaVersion");
    public string LogsFolderLabel => _localization.Get("DiagnosticsLogsFolder");
    public string ValidateGameLabel => _localization.Get("ValidateGameDiagnostics");
    public string OpenGameFolderLabel => _localization.Get("OpenGameFolder");
    public string OpenLibraryFolderLabel => _localization.Get("OpenLibraryFolder");
    public string OpenLogsFolderLabel => _localization.Get("OpenLogsFolder");
    public string CopyStateLabel => _localization.Get("CopyDiagnosticsState");
    public string DumpLibrarySelectionTraceLabel => _localization.CurrentLanguage == SupportedLanguages.English ? "Save Library selection trace" : "Сохранить трассировку выбора Библиотеки";
    public string RecoveryRequiredText => _localization.Get("DiagnosticsRecoveryRequired");
    public string RequestRecoveryLabel => _localization.CurrentLanguage == SupportedLanguages.English ? "Inspect and recover" : "Проверить и восстановить";
    public string RipperWorksTabLabel => _localization.Get("DiagnosticsRipperWorksTab");
    public string GameSessionTabLabel => _localization.Get("DiagnosticsGameSessionTab");
    public string DiagnosticSourcesHeader => _localization.Get("DiagnosticSources");
    public string RefreshLabel => _localization.Get("DiagnosticsRefresh");
    public string OpenLogLabel => _localization.Get("DiagnosticsOpenLog");
    public string ExportLogLabel => _localization.Get("DiagnosticsExportLog");
    public string OpenSourceLabel => _localization.Get("DiagnosticsOpenSource");
    public string NoSessionsText => _localization.Get("DiagnosticsNoSessions");
    public string NoEventsText => _localization.Get("DiagnosticsNoEvents");
    public string NoFilteredEventsText => _localization.Get("DiagnosticsNoFilteredEvents");
    public string IncompleteSessionNotice => _localization.Get("DiagnosticsSessionIncompleteNotice");
    public string UnsupportedSessionNotice => _localization.Get("DiagnosticsSessionUnsupportedNotice");
    public string MalformedSessionNotice => _localization.Get("DiagnosticsSessionMalformedNotice");
    public string StartLabel => _localization.Get("DiagnosticsStart");
    public string EndLabel => _localization.Get("DiagnosticsEnd");
    public string ResultLabel => _localization.Get("DiagnosticsResult");
    public string ExitCodeLabel => _localization.Get("DiagnosticsExitCode");
    public string SourceLabel => _localization.Get("DiagnosticsSource");
    public string TimeLabel => _localization.Get("DiagnosticsTime");
    public string SeverityLabel => _localization.Get("DiagnosticsSeverity");
    public string ContextLabel => _localization.Get("DiagnosticsContext");
    public string CodeLabel => _localization.Get("DiagnosticsCode");
    public string MessageLabel => _localization.Get("DiagnosticsMessage");
    public string SourcePathLabel => _localization.Get("DiagnosticsSourcePath");
    public string FilterAllLabel => $"{_localization.Get("DiagnosticsFilterAll")} ({SelectedSession?.AllEventsCount ?? 0})";
    public string FilterErrorsLabel => $"{_localization.Get("DiagnosticsFilterErrors")} ({SelectedSession?.ErrorCount ?? 0})";
    public string FilterWarningsLabel => $"{_localization.Get("DiagnosticsFilterWarnings")} ({SelectedSession?.WarningCount ?? 0})";
    public string FilterCrashReportsLabel => $"{_localization.Get("DiagnosticsFilterCrashReports")} ({SelectedSession?.CrashReportCount ?? 0})";

    private void InitializeDiagnosticSources()
    {
        DiagnosticSources.Clear();
        DiagnosticSources.Add(new DiagnosticSourceStatusViewModel(
            "Cyberpunk2077", "Cyberpunk 2077", DiagnosticSourceStatus.NotConfigured,
            _localization.Get("DiagnosticSourceNotConfigured"), "TextSecondaryBrush"));

        var orderedIds = new[]
        {
            "RED4ext", "redscript", "CyberEngineTweaks",
            "ArchiveXL", "TweakXL", "Codeware", "RedFileSystem"
        };

        foreach (var id in orderedIds)
        {
            var def = DiagnosticSourceRegistry.SupportedSources.FirstOrDefault(d => d.SourceId == id);
            var name = def?.DisplayName ?? id;
            DiagnosticSources.Add(new DiagnosticSourceStatusViewModel(
                id, name, DiagnosticSourceStatus.NotInstalled,
                _localization.Get("DiagnosticSourceNotInstalled"), "TextSecondaryBrush"));
        }
    }

    public async Task RefreshAsync()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var gen = Interlocked.Increment(ref _refreshGeneration);

        IsLoading = true;
        try
        {
            await RefreshSourceStatusesAsync(cts.Token, gen).ConfigureAwait(true);
            await RefreshSessionsAsync(gen, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (gen == _refreshGeneration)
            {
                IsLoading = false;
            }
        }
    }

    public async Task RefreshSourceStatusesAsync(CancellationToken cancellationToken = default, long generation = 0)
    {
        var cpSource = DiagnosticSources.FirstOrDefault(s => s.SourceId == "Cyberpunk2077");
        if (cpSource != null)
        {
            var isConfigured = !string.IsNullOrWhiteSpace(Settings.Cyberpunk2077Root) &&
                               Settings.GameRootDisplay != "—" &&
                               Settings.ExecutableStateValue != _localization.Get("DiagnosticsNotFound");
            if (isConfigured)
            {
                cpSource.Update(DiagnosticSourceStatus.Configured, _localization.Get("DiagnosticSourceConfigured"), "SuccessBrush");
            }
            else
            {
                cpSource.Update(DiagnosticSourceStatus.NotConfigured, _localization.Get("DiagnosticSourceNotConfigured"), "TextSecondaryBrush");
            }
        }

        if (_frameworkStateProvider == null) return;

        try
        {
            var state = await _frameworkStateProvider.GetInstalledFrameworkStateAsync(cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || (generation != 0 && generation != _refreshGeneration))
            {
                return;
            }

            foreach (var source in DiagnosticSources.Where(s => s.SourceId != "Cyberpunk2077"))
            {
                var def = DiagnosticSourceRegistry.SupportedSources.FirstOrDefault(d => d.SourceId == source.SourceId);
                if (def != null && def.IsActive(state))
                {
                    source.Update(DiagnosticSourceStatus.Installed, _localization.Get("DiagnosticSourceInstalled"), "SuccessBrush");
                }
                else
                {
                    source.Update(DiagnosticSourceStatus.NotInstalled, _localization.Get("DiagnosticSourceNotInstalled"), "TextSecondaryBrush");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            if (cancellationToken.IsCancellationRequested || (generation != 0 && generation != _refreshGeneration))
            {
                return;
            }

            foreach (var source in DiagnosticSources.Where(s => s.SourceId != "Cyberpunk2077"))
            {
                source.Update(DiagnosticSourceStatus.Unavailable, _localization.Get("DiagnosticSourceUnavailable"), "WarningBrush");
            }
        }
    }

    public async Task RefreshSessionsAsync(long generation = 0, CancellationToken cancellationToken = default)
    {
        var discovered = await _discoveryService.DiscoverSessionsAsync(_logsDirectory, cancellationToken).ConfigureAwait(true);
        if (cancellationToken.IsCancellationRequested || (generation != 0 && generation != _refreshGeneration))
        {
            return;
        }

        var previousSelectedPath = SelectedSession?.FilePath;

        Sessions.Clear();
        foreach (var journal in discovered)
        {
            var eventVms = journal.Events.Select(e => new GameDiagnosticEventViewModel(e, _localization)).ToList();
            Sessions.Add(new GameDiagnosticSessionItemViewModel(journal, eventVms, _localization));
        }

        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasNoSessions));

        var toSelect = (!string.IsNullOrWhiteSpace(previousSelectedPath) ? Sessions.FirstOrDefault(s => s.FilePath == previousSelectedPath) : null) ?? Sessions.FirstOrDefault();
        SelectedSession = toSelect;
    }

    private void ApplyFilter()
    {
        FilteredEvents.Clear();
        SelectedEvent = null;

        if (SelectedSession == null || SelectedSession.IsUnsupported || SelectedSession.IsMalformed)
        {
            OnPropertyChanged(nameof(HasFilteredEvents));
            OnPropertyChanged(nameof(HasNoFilteredEvents));
            return;
        }

        IEnumerable<GameDiagnosticEventViewModel> query = SelectedSession.Events;
        query = SelectedFilter switch
        {
            DiagnosticEventFilter.Errors => query.Where(e => e.Severity == GameDiagnosticSeverity.Error),
            DiagnosticEventFilter.Warnings => query.Where(e => e.Severity == GameDiagnosticSeverity.Warning),
            DiagnosticEventFilter.CrashReports => query.Where(e => e.IsCrashReport),
            _ => query
        };

        foreach (var ev in query)
            FilteredEvents.Add(ev);

        SelectedEvent = FilteredEvents.FirstOrDefault();
        OnPropertyChanged(nameof(HasFilteredEvents));
        OnPropertyChanged(nameof(HasNoFilteredEvents));
    }

    private void OpenSelectedSessionLog()
    {
        if (SelectedSession == null || !File.Exists(SelectedSession.FilePath)) return;
        try { Process.Start(new ProcessStartInfo { FileName = SelectedSession.FilePath, UseShellExecute = true }); } catch { }
    }

    private void ExportSelectedSessionLog()
    {
        if (SelectedSession == null || !File.Exists(SelectedSession.FilePath)) return;

        string? destPath;
        if (_saveFileDialog != null)
        {
            destPath = _saveFileDialog(SelectedSession.FileName);
        }
        else
        {
            var dialog = new SaveFileDialog
            {
                Title = _localization.Get("DiagnosticsExportLogTitle"),
                FileName = SelectedSession.FileName,
                DefaultExt = ".log",
                Filter = _localization.Get("DiagnosticsExportLogFilter")
            };
            if (dialog.ShowDialog() != true) return;
            destPath = dialog.FileName;
        }

        if (string.IsNullOrWhiteSpace(destPath)) return;
        if (string.Equals(SelectedSession.FilePath, destPath, StringComparison.OrdinalIgnoreCase)) return;

        try { File.Copy(SelectedSession.FilePath, destPath, overwrite: true); } catch { }
    }

    private void OpenSelectedEventSource()
    {
        if (SelectedEvent == null || !SelectedEvent.HasSourcePath) return;
        var path = SelectedEvent.SourcePath;
        if (!DiagnosticsLocalPathPolicy.IsValidLocalWindowsPath(path, _driveTypeResolver)) return;

        try
        {
            if (SelectedEvent.IsCrashReport)
            {
                if (Directory.Exists(path))
                {
                    if (_shellOpenDirectory != null) _shellOpenDirectory(path);
                    else Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = false });
                }
            }
            else
            {
                if (File.Exists(path))
                {
                    if (_shellRevealFile != null) _shellRevealFile(path, $"/select,\"{path}\"");
                    else Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = false });
                }
            }
        }
        catch { }
    }

    public static bool IsValidLocalWindowsPath(string? path, Func<string, DriveType>? driveTypeResolver = null) =>
        DiagnosticsLocalPathPolicy.IsValidLocalWindowsPath(path, driveTypeResolver);

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
    }

    private void CopyState()
    {
        var lines = new[]
        {
            $"{GameFolderLabel}: {Settings.GameRootDisplay}",
            $"{ExecutableLabel}: {Settings.ExecutableStateValue}",
            $"{WriteAccessLabel}: {Settings.WriteAccessStateValue}",
            $"{ForeignModsLabel}: {Settings.ForeignModsStateValue}",
            $"{ProfileLabel}: {Settings.ProfileStateValue}",
            $"{LastValidationLabel}: {Settings.LastValidationValue}",
            $"{LibraryFolderLabel}: {Settings.LibraryRoot}",
            $"{DatabaseModsLabel}: {Library.PackageCountValue}",
            $"{SkippedFoldersLabel}: {Library.SkippedDirectoryCountValue}",
            $"{LastUpdateLabel}: {Library.LastUpdatedValue}",
            $"{DatabaseLabel}: {DatabasePath}",
            $"{SchemaVersionLabel}: {SchemaVersionValue}",
            $"{LogsFolderLabel}: {LogsDirectory}",
            $"{RecoveryRequiredText}: {RecoveryRequired}"
        };
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); } catch { }
    }

    private void NotifySessionProperties()
    {
        string[] props = [nameof(HasSelectedSession), nameof(SessionSummaryVisible), nameof(IsCleanSession), nameof(IsIncompleteSession), nameof(IsUnsupportedSession), nameof(IsMalformedSession), nameof(HasUnavailableSources), nameof(UnavailableSourcesNotice), nameof(FilterAllLabel), nameof(FilterErrorsLabel), nameof(FilterWarningsLabel), nameof(FilterCrashReportsLabel)];
        foreach (var prop in props) OnPropertyChanged(prop);
    }

    private void RefreshLocalization()
    {
        string[] properties =
        [
            nameof(Header), nameof(StateHeader), nameof(GameHeader), nameof(LibraryHeader), nameof(EventLogHeader),
            nameof(NoSessionEvents), nameof(GameFolderLabel), nameof(WriteAccessLabel), nameof(ForeignModsLabel),
            nameof(ProfileLabel), nameof(LastValidationLabel), nameof(LibraryFolderLabel), nameof(DatabaseModsLabel),
            nameof(SkippedFoldersLabel), nameof(LastUpdateLabel), nameof(DatabaseLabel), nameof(SchemaVersionLabel),
            nameof(LogsFolderLabel), nameof(ValidateGameLabel), nameof(OpenGameFolderLabel), nameof(OpenLibraryFolderLabel),
            nameof(OpenLogsFolderLabel), nameof(CopyStateLabel), nameof(DumpLibrarySelectionTraceLabel),
            nameof(RecoveryRequiredText), nameof(RequestRecoveryLabel), nameof(RipperWorksTabLabel),
            nameof(GameSessionTabLabel), nameof(DiagnosticSourcesHeader), nameof(RefreshLabel), nameof(OpenLogLabel),
            nameof(ExportLogLabel), nameof(OpenSourceLabel), nameof(NoSessionsText), nameof(NoEventsText),
            nameof(NoFilteredEventsText), nameof(IncompleteSessionNotice), nameof(UnsupportedSessionNotice),
            nameof(MalformedSessionNotice), nameof(StartLabel), nameof(EndLabel), nameof(ResultLabel),
            nameof(ExitCodeLabel), nameof(SourceLabel), nameof(TimeLabel), nameof(SeverityLabel), nameof(ContextLabel),
            nameof(CodeLabel), nameof(MessageLabel), nameof(SourcePathLabel)
        ];
        foreach (var property in properties) OnPropertyChanged(property);

        foreach (var session in Sessions)
        {
            session.UpdateLocalization(_localization);
        }

        foreach (var source in DiagnosticSources)
        {
            source.UpdateLocalization(_localization);
        }

        Maintenance.NotifyLanguageChanged();
        NotifySessionProperties();
    }

    public void Dispose()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        Maintenance.Dispose();
        Library.PropertyChanged -= Library_OnPropertyChanged;
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
    }

    private void Library_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.RecoveryRequired))
        {
            OnPropertyChanged(nameof(RecoveryRequired));
            RequestRecoveryCommand.NotifyCanExecuteChanged();
        }
    }

    private void Localization_OnLanguageChanged(object? sender, EventArgs e) =>
        RefreshLocalization();
}
