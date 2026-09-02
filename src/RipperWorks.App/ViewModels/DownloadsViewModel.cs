using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Downloader;

namespace RipperWorks.App.ViewModels;

public sealed class DownloadsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDownloaderRepository _repository;
    private readonly NexusDownloadCoordinator _nexus;
    private readonly IDownloadQueue _queue;
    private readonly DownloaderTableImporter _importer;
    private readonly IDownloadArchivePublisher _publisher;
    private readonly IDownloaderDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly Func<RipperWorksSettings> _settings;
    private readonly PostPublicationLibraryRefreshOwner _archiveRefreshOwner;
    private readonly Func<Exception, string>? _exceptionLogger;
    private readonly ManualBrowserQueue _manualQueue;
    private readonly NexusUpdateBatchWorkflow _updateBatchWorkflow;
    private readonly IInternalBrowserWindowService? _internalBrowser;
    private readonly IExternalNexusBrowserService? _externalBrowser;
    private readonly IDownloaderArchiveStatusReconciler?
        _archiveStatusReconciler;
    private readonly INexusModUpdateCheckService? _updateCheckService;
    private readonly Action<string, string?>? _recordSessionEvent;
    private readonly Action<string>? _showError;
    private readonly Func<Task>? _archiveFamilyReconcile;
    private readonly INexusRequirementRefreshService? _nexusRequirements;
    private readonly Dispatcher? _uiDispatcher;
    private readonly ConcurrentDictionary<Guid, DownloaderEntry>
        _pendingQueueUpdates = new();
    private int _queueUpdateScheduled;
    private DownloaderEntry? _selectedEntry;
    private string _linkText = string.Empty;
    private string _searchText = string.Empty;
    private string _statusFilter = "All";
    private string _categoryFilter = "All";
    private string _sourceFilter = "All";
    private string _statusMessage = string.Empty;
    private string _initializationError = string.Empty;
    private bool _disposed;
    private bool _synchronizingEntries;
    private bool _isCheckingUpdates;
    private CancellationTokenSource? _updateCheckCancellation;
    private Task? _updateCheckTask;
    private CancellationTokenSource? _nexusRefreshCancellation;
    private Task _nexusRefreshTask = Task.CompletedTask;
    private readonly SemaphoreSlim _unresolvedNexusEntryGate = new(1, 1);
    private readonly BulkObservableCollection<DownloaderEntry> _entries = [];

    public DownloadsViewModel(
        IDownloaderRepository repository,
        NexusDownloadCoordinator nexus,
        IDownloadQueue queue,
        DownloaderTableImporter importer,
        IDownloadArchivePublisher publisher,
        IDownloaderDialogService dialogs,
        LocalizationService localization,
        Func<RipperWorksSettings> settings,
        Func<DownloaderEntry, Task> archivePublished,
        Func<Exception, string>? exceptionLogger = null,
        ManualBrowserQueue? manualQueue = null,
        IInternalBrowserWindowService? internalBrowser = null,
        IExternalNexusBrowserService? externalBrowser = null,
        Action<string>? showError = null,
        IDownloaderArchiveStatusReconciler? archiveStatusReconciler = null,
        Action<string, string?>? recordSessionEvent = null,
        INexusModUpdateCheckService? updateCheckService = null,
        Func<Task>? archiveFamilyReconcile = null,
        INexusRequirementRefreshService? nexusRequirements = null)
    {
        _repository = repository;
        _nexus = nexus;
        _queue = queue;
        _importer = importer;
        _publisher = publisher;
        _dialogs = dialogs;
        _localization = localization;
        _settings = settings;
        _exceptionLogger = exceptionLogger;
        _archiveRefreshOwner = new PostPublicationLibraryRefreshOwner(
            archivePublished,
            exception => _exceptionLogger?.Invoke(exception));
        _manualQueue = manualQueue ??
            new ManualBrowserQueue(repository, nexus);
        _updateBatchWorkflow = new(
            _manualQueue,
            _ => OpenManualBrowserAsync());
        _internalBrowser = internalBrowser;
        _externalBrowser = externalBrowser;
        _showError = showError;
        _archiveStatusReconciler = archiveStatusReconciler;
        _recordSessionEvent = recordSessionEvent;
        _updateCheckService = updateCheckService;
        _archiveFamilyReconcile = archiveFamilyReconcile;
        _nexusRequirements = nexusRequirements;
        _uiDispatcher = Thread.CurrentThread.GetApartmentState() ==
            ApartmentState.STA
                ? Dispatcher.CurrentDispatcher
                : Application.Current?.Dispatcher;
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = Filter;
        _queue.EntryChanged += Queue_OnEntryChanged;
        _manualQueue.Changed += ManualQueue_OnChanged;
        _localization.LanguageChanged += Localization_OnLanguageChanged;

        AddLinkCommand = new AsyncRelayCommand(AddLinkAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        AddManualCommand = new AsyncRelayCommand(AddManualAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        SelectVisibleCommand = new RelayCommand(
            () => SetSelection(EntriesView.Cast<DownloaderEntry>(), true));
        ClearSelectionCommand = new RelayCommand(
            () => SetSelection(Entries, false));
        SelectAllCommand = new RelayCommand(
            () => SetSelection(Entries, true));
        RefreshStatusesCommand = new AsyncRelayCommand(
            RefreshStatusesAsync,
            () => _archiveStatusReconciler is not null);
        CheckUpdatesCommand = new AsyncRelayCommand(
            ToggleUpdateCheckAsync,
            () => _updateCheckService is not null,
            allowConcurrentExecution: true);
        CheckSelectedUpdateCommand = new AsyncRelayCommand(
            CheckSelectedUpdateAsync,
            () => _updateCheckService is not null &&
                !IsCheckingUpdates &&
                CanCheckSelectedUpdate);
        OpenSelectedUpdateCommand = new AsyncRelayCommand(
            OpenSelectedUpdateAsync,
            () => CanOpenSelectedUpdate);
        UpdateSelectedCommand = new AsyncRelayCommand(
            UpdateSelectedAsync,
            HasUpdateSelection);
        StartSelectedCommand = new AsyncRelayCommand(
            StartSelectedAsync,
            HasExplicitSelection);
        PauseSelectedCommand = new AsyncRelayCommand(
            () => ExecuteOperationsAsync(
                entry => entry.Status == DownloaderStatus.Downloading,
                entry => _queue.PauseAsync(entry.Id)),
            HasExplicitSelection);
        ResumeSelectedCommand = new AsyncRelayCommand(
            () => ExecuteOperationsAsync(
                entry => entry.Status == DownloaderStatus.Paused,
                entry => _queue.ResumeAsync(entry.Id)),
            HasExplicitSelection);
        CancelSelectedCommand = new AsyncRelayCommand(
            CancelSelectedAsync,
            HasExplicitSelection);
        RetrySelectedCommand = new AsyncRelayCommand(
            RetrySelectedAsync,
            HasExplicitSelection);
        DeleteSelectedCommand = new AsyncRelayCommand(
            DeleteSelectedAsync,
            HasExplicitSelection);
        OpenPageCommand = new AsyncRelayCommand(
            OpenPageAsync,
            HasSelectedUrl);
        AttachArchiveCommand = new AsyncRelayCommand(AttachArchiveAsync);
        OpenFileCommand = new RelayCommand(OpenFile, HasDownloadedFile);
        OpenFolderCommand = new RelayCommand(OpenFolder, HasDownloadedFile);
        CopyLinkCommand = new RelayCommand(CopyLink, HasSelectedUrl);
        CopySha256Command = new RelayCommand(
            CopySha256,
            HasSelectedSha256);
        EditCommand = new AsyncRelayCommand(EditAsync);
        RefreshChoices();
    }

    public ObservableCollection<DownloaderEntry> Entries => _entries;
    public LocalizationService Localization => _localization;
    public ICollectionView EntriesView { get; }
    public ObservableCollection<ChoiceItem> CategoryChoices { get; } = [];
    public IReadOnlyList<ChoiceItem> StatusChoices { get; private set; } = [];
    public IReadOnlyList<ChoiceItem> SourceChoices { get; private set; } = [];

    public AsyncRelayCommand AddLinkCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand AddManualCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand SelectVisibleCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public AsyncRelayCommand RefreshStatusesCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public AsyncRelayCommand CheckSelectedUpdateCommand { get; }
    public AsyncRelayCommand OpenSelectedUpdateCommand { get; }
    public AsyncRelayCommand UpdateSelectedCommand { get; }
    public AsyncRelayCommand StartSelectedCommand { get; }
    public AsyncRelayCommand PauseSelectedCommand { get; }
    public AsyncRelayCommand ResumeSelectedCommand { get; }
    public AsyncRelayCommand CancelSelectedCommand { get; }
    public AsyncRelayCommand RetrySelectedCommand { get; }
    public AsyncRelayCommand DeleteSelectedCommand { get; }
    public AsyncRelayCommand OpenPageCommand { get; }
    public AsyncRelayCommand AttachArchiveCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyLinkCommand { get; }
    public RelayCommand CopySha256Command { get; }
    public AsyncRelayCommand EditCommand { get; }
    public Action<string>? RequestNavigateNexus { get; set; }

    public DownloaderEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value))
                return;
            NotifySelectedCommands();
            OnPropertyChanged(nameof(CanStartSelectedEntry));
            OnPropertyChanged(nameof(CanPauseSelectedEntry));
            OnPropertyChanged(nameof(CanResumeSelectedEntry));
            OnPropertyChanged(nameof(CanCancelSelectedEntry));
            OnPropertyChanged(nameof(CanRetrySelectedEntry));
            OnPropertyChanged(nameof(CanOpenDownloadedEntry));
            OnPropertyChanged(nameof(CanCheckSelectedUpdate));
            OnPropertyChanged(nameof(CanOpenSelectedUpdate));
        }
    }

    public string LinkText
    {
        get => _linkText;
        set => SetProperty(ref _linkText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                EntriesView.Refresh();
        }
    }

    public void NavigateToNexusMod(NexusModIdentity identity)
    {
        StatusFilter = "All";
        CategoryFilter = "All";
        SourceFilter = "All";
        SearchText = identity.ModId.ToString(CultureInfo.InvariantCulture);

        NexusGameIdentityBridge.TryGetGameDomain(identity.GameId, out var domain);

        var matching = !string.IsNullOrWhiteSpace(domain)
            ? Entries.Where(e =>
                e.Source == DownloaderSource.Nexus &&
                e.NexusModId == identity.ModId &&
                string.Equals(e.GameDomain, domain, StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];

        SelectedEntry = matching.Length == 1 ? matching[0] : null;
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            value = string.IsNullOrWhiteSpace(value)
                ? "All"
                : value;
            if (SetProperty(ref _statusFilter, value))
                EntriesView.Refresh();
        }
    }

    public string CategoryFilter
    {
        get => _categoryFilter;
        set
        {
            value = string.IsNullOrWhiteSpace(value)
                ? "All"
                : value;
            if (SetProperty(ref _categoryFilter, value))
                EntriesView.Refresh();
        }
    }

    public string SourceFilter
    {
        get => _sourceFilter;
        set
        {
            value = string.IsNullOrWhiteSpace(value)
                ? "All"
                : value;
            if (SetProperty(ref _sourceFilter, value))
                EntriesView.Refresh();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string InitializationError
    {
        get => _initializationError;
        private set
        {
            if (!SetProperty(ref _initializationError, value))
                return;
            OnPropertyChanged(nameof(HasInitializationError));
        }
    }

    public bool HasInitializationError =>
        !string.IsNullOrWhiteSpace(InitializationError);

    public string AddSectionLabel => L("DownloaderAddSection");
    public string ManagementSectionLabel => L("DownloaderManagementSection");
    public string ImportLabel => L("DownloaderImport");
    public string AddManualLabel => L("DownloaderAddManual");
    public string ExportLabel => L("DownloaderExportCsv");
    public string AddLinkLabel => L("DownloaderAddLink");
    public string LinkPlaceholder => L("DownloaderLinkPlaceholder");
    public string SearchPlaceholder => L("SearchPlaceholder");
    public string SelectVisibleLabel => L("DownloaderSelectVisible");
    public string ClearSelectionLabel => L("DownloaderClearSelection");
    public string SelectAllLabel => L("DownloaderSelectAll");
    public string RefreshStatusesLabel =>
        L("DownloaderRefreshStatuses");
    public string CheckUpdatesLabel => _isCheckingUpdates
        ? L("DownloaderCancelUpdateCheck")
        : L("DownloaderCheckUpdates");
    public bool IsCheckingUpdates => _isCheckingUpdates;
    public string CheckUpdateLabel => L("DownloaderCheckUpdate");
    public string OpenUpdateLabel => L("DownloaderOpenUpdate");
    public string UpdateSelectedLabel => L("DownloaderUpdateSelected");
    public string StartLabel => L("DownloaderStart");
    public string PauseLabel => L("DownloaderPause");
    public string ResumeLabel => L("DownloaderResume");
    public string CancelLabel => L("DownloaderCancel");
    public string RetryLabel => L("DownloaderRetry");
    public string DeleteLabel => L("DownloaderDeleteRecord");
    public string OpenPageLabel => L("DownloaderOpenPage");
    public string EditLabel => L("Edit");
    public string AttachLabel => L("DownloaderAttachArchive");
    public string OpenFileLabel => L("DownloaderOpenFile");
    public string OpenFolderLabel => L("OpenFolder");
    public string CopyLinkLabel => L("DownloaderCopyLink");
    public string CopySha256Label => L("DownloaderCopySha256");
    public string NumberColumn => "№";
    public string NameColumn => L("DisplayName");
    public string CategoryColumn => L("Category");
    public string SourceColumn => L("ColumnSource");
    public string AuthorColumn => L("Author");
    public string UrlColumn => "URL";
    public string VersionColumn => L("ColumnVersion");
    public string UpdateColumn => L("DownloaderUpdateColumn");
    public string StatusColumn => L("ColumnStatus");
    public string ProgressColumn => L("DownloaderProgress");
    public string SizeColumn => L("ColumnSize");
    public string Sha256Column => "SHA-256";
    public string SpeedColumn => L("DownloaderSpeed");

    public bool CanStartSelectedEntry =>
        SelectedEntry is not null &&
        DownloaderStatusPolicy.CanStart(SelectedEntry.Status);
    public bool CanPauseSelectedEntry =>
        SelectedEntry?.Status == DownloaderStatus.Downloading;
    public bool CanResumeSelectedEntry =>
        SelectedEntry?.Status == DownloaderStatus.Paused;
    public bool CanCancelSelectedEntry =>
        SelectedEntry?.Status is DownloaderStatus.Waiting or
            DownloaderStatus.Downloading or
            DownloaderStatus.Paused ||
        _manualQueue.Current?.Entry.Id == SelectedEntry?.Id;
    public bool CanRetrySelectedEntry =>
        SelectedEntry is not null &&
        DownloaderStatusPolicy.CanRetry(SelectedEntry.Status);
    public bool CanOpenDownloadedEntry => HasDownloadedFile();
    public bool CanCheckSelectedUpdate =>
        SelectedEntry?.Source == DownloaderSource.Nexus &&
        SelectedEntry.NexusModId is not null &&
        !string.IsNullOrWhiteSpace(SelectedEntry.GameDomain);
    public bool CanOpenSelectedUpdate =>
        CanCheckSelectedUpdate &&
        SelectedEntry is not null &&
        NexusUpdateClassifier.IsUpdateAvailable(SelectedEntry) &&
        SelectedEntry.NexusFileId != SelectedEntry.AvailableFileId &&
        _manualQueue.Current?.Entry.Id != SelectedEntry.Id;

    public async Task InitializeAsync()
    {
        await ReloadEntriesAsync();
        await _queue.RestoreAsync();
    }

    public void ReportInitializationFailure(string logPath)
    {
        InitializationError =
            L("DownloaderInitializationFailed") +
            Environment.NewLine +
            string.Format(
                L("DownloaderInitializationLog"),
                logPath);
        StatusMessage = InitializationError;
    }

    public Task RefreshPersistedEntriesAsync() =>
        ReloadEntriesAsync();

    public async Task HandleNxmAsync(string value)
    {
        try
        {
            var entry = _manualQueue.IsActive
                ? await HandleManualNxmAsync(value)
                : await _nexus.ProcessNxmAsync(value);
            Upsert(entry);
            SelectedEntry = entry;
            StatusMessage = L("DownloaderNxmAccepted");
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private async Task<DownloaderEntry> HandleManualNxmAsync(
        string value)
    {
        var entryId = _manualQueue.Current?.Entry.Id ??
            throw new InvalidOperationException(
                "No manual Nexus entry is active.");
        await _manualQueue.HandleNxmAsync(value);
        return await _repository.LoadEntryAsync(entryId) ??
            throw new InvalidOperationException(
                "The updated Downloader entry was not found.");
    }

    private async Task AddLinkAsync()
    {
        var value = LinkText.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return;
        try
        {
            if (DownloaderLinkParser.TryParseNxm(value, out _))
            {
                await HandleNxmAsync(value);
            }
            else if (NexusBrowserNavigationPolicy.IsValidOpenNexusWebUrl(value, out _) &&
                     DownloaderLinkParser.TryParseNexusPage(value, out _))
            {
                var resolution = await _nexus.ResolveUrlAsync(value);
                var file = resolution.SelectedFile ??
                    _dialogs.SelectNexusFile(
                        resolution.Metadata,
                        _localization);
                if (file is null)
                    return;
                var entry = await _nexus.AddResolvedAsync(
                    resolution.Metadata,
                    file,
                    value);
                Upsert(entry);
                SelectedEntry = entry;
            }
            else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var name = Uri.UnescapeDataString(
                    uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
                if (string.IsNullOrWhiteSpace(name))
                    name = uri.Host;
                var entry = new DownloaderEntry
                {
                    Name = name,
                    Category = L("DownloaderUndefined"),
                    Source = DownloaderSource.Import,
                    Url = value,
                    Status = DownloaderStatus.ManualActionRequired,
                    IsSelected = true
                };
                await _repository.SaveEntryAsync(entry);
                AddTracked(entry);
            }
            else
            {
                throw new InvalidOperationException(
                    L("DownloaderUnsupportedLink"));
            }
            LinkText = string.Empty;
            Renumber();
            RefreshChoices();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private Task StartSelectedAsync() =>
        StartSelectedAsync(entry =>
            DownloaderStatusPolicy.CanStart(entry.Status));

    private async Task StartSelectedAsync(
        Func<DownloaderEntry, bool> predicate)
    {
        var queued = 0;
        var manual = 0;
        var manualEntries = new List<DownloaderEntry>();
        foreach (var entry in SelectedEntries()
                     .Where(predicate)
                     .ToArray())
        {
            try
            {
                var candidateNxm = DownloaderLinkParser.TryParseNxm(entry.AdditionalUrl, out _)
                    ? entry.AdditionalUrl
                    : entry.Url;
                if (DownloaderLinkParser.TryParseNxm(candidateNxm, out _))
                {
                    Upsert(await _nexus.ProcessNxmForEntryAsync(
                        entry.Id,
                        candidateNxm));
                    queued++;
                    continue;
                }

                if (entry.Source == DownloaderSource.Nexus &&
                    entry.NexusModId is not null &&
                    entry.NexusFileId is not null)
                {
                    Upsert(await _nexus.QueueKnownFileAsync(entry.Id));
                    queued++;
                    continue;
                }

                var isNexusPage = NexusBrowserNavigationPolicy.IsValidOpenNexusWebUrl(entry.Url, out _) &&
                                  DownloaderLinkParser.TryParseNexusPage(entry.Url, out _);

                if (isNexusPage)
                {
                    entry.Status = DownloaderStatus.ManualActionRequired;
                    entry.Error = string.Empty;
                    await _repository.SaveEntryAsync(entry);
                    manual++;
                    manualEntries.Add(entry);
                    continue;
                }

                if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var extUri) &&
                    (extUri.Scheme == Uri.UriSchemeHttp || extUri.Scheme == Uri.UriSchemeHttps))
                {
                    _dialogs.OpenUri(extUri.AbsoluteUri);
                    entry.Status = DownloaderStatus.ManualActionRequired;
                    entry.Error = string.Empty;
                    await _repository.SaveEntryAsync(entry);
                    manual++;
                    continue;
                }

                entry.Status = DownloaderStatus.ManualActionRequired;
                entry.Error = L("DownloaderUnsupportedLink");
                await _repository.SaveEntryAsync(entry);
                manual++;
            }
            catch (Exception exception)
            {
                entry.Status = DownloaderStatus.ManualActionRequired;
                entry.Error = exception.Message;
                await _repository.SaveEntryAsync(entry);
                manual++;
                if (NexusBrowserNavigationPolicy.IsValidOpenNexusWebUrl(entry.Url, out _) &&
                    DownloaderLinkParser.TryParseNexusPage(entry.Url, out _))
                {
                    manualEntries.Add(entry);
                }
            }
        }
        if (manualEntries.Count > 0)
        {
            await _manualQueue.StartAsync(manualEntries);
            try
            {
                if (_settings().NexusBrowser ==
                    NexusBrowserMode.External)
                {
                    if (_externalBrowser is null)
                    {
                        throw new InvalidOperationException(
                            L("DownloaderExternalBrowserUnavailable"));
                    }
                    await _externalBrowser.OpenAsync(
                        _manualQueue.Current ??
                        throw new InvalidOperationException(
                            L("DownloaderManualQueueEmpty")));
                }
                else
                {
                    if (_internalBrowser is null)
                    {
                        throw new InvalidOperationException(
                            L("DownloaderInternalBrowserUnavailable"));
                    }
                    await _internalBrowser.ShowAsync(
                        _manualQueue,
                        _localization);
                }
            }
            catch (Exception exception)
            {
                var logPath = _exceptionLogger?.Invoke(exception) ??
                    string.Empty;
                var message = string.Format(
                    L("DownloaderBrowserOpenFailed"),
                    exception.Message,
                    logPath);
                StatusMessage = message;
                _showError?.Invoke(message);
                NotifyMassCommands();
                NotifySelectedCommands();
                return;
            }
        }
        StatusMessage = string.Format(
            L("DownloaderBatchStartSummary"),
            queued,
            manual);
    }

    private async Task RetrySelectedAsync()
    {
        await ExecuteOperationsAsync(
            entry => entry.Status == DownloaderStatus.Error,
            entry => _queue.ResumeAsync(entry.Id));
        await StartSelectedAsync(entry =>
            entry.Status is DownloaderStatus.FileMissing or
                DownloaderStatus.FileCorrupted);
    }

    private async Task RefreshStatusesAsync()
    {
        if (_archiveStatusReconciler is null)
            return;
        _recordSessionEvent?.Invoke(
            "EventDownloaderStatusRefreshStarted",
            null);
        try
        {
            var completed = false;
            var progress =
                new Progress<DownloaderArchiveReconciliationProgress>(
                    value =>
                    {
                        if (value.UpdatedEntry is not null)
                            Upsert(value.UpdatedEntry);
                        if (!completed)
                        {
                            StatusMessage = string.Format(
                                L("DownloaderStatusRefreshProgress"),
                                value.Completed,
                                value.Total);
                        }
                    });
            var result = await _archiveStatusReconciler.ReconcileAsync(
                progress);
            completed = true;
            StatusMessage = string.Format(
                L("DownloaderStatusRefreshSummary"),
                result.Checked,
                result.Found,
                result.Missing,
                result.Corrupted,
                result.Unchanged);
            _recordSessionEvent?.Invoke(
                "EventDownloaderStatusRefreshCompleted",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "проверено {0}, найдено {1}, отсутствует {2}, " +
                    "повреждено {3}",
                    result.Checked,
                    result.Found,
                    result.Missing,
                    result.Corrupted));
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            StatusMessage = exception.Message;
        }
    }

    private async Task ToggleUpdateCheckAsync()
    {
        if (_isCheckingUpdates)
        {
            _updateCheckCancellation?.Cancel();
            return;
        }
        var operation = CheckAllUpdatesAsync();
        _updateCheckTask = operation;
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_updateCheckTask, operation))
                _updateCheckTask = null;
        }
    }

    private async Task CheckAllUpdatesAsync()
    {
        if (_updateCheckService is null || _isCheckingUpdates)
            return;
        _isCheckingUpdates = true;
        var cancellation = new CancellationTokenSource();
        _updateCheckCancellation = cancellation;
        OnPropertyChanged(nameof(IsCheckingUpdates));
        OnPropertyChanged(nameof(CheckUpdatesLabel));
        CheckSelectedUpdateCommand.NotifyCanExecuteChanged();
        _recordSessionEvent?.Invoke(
            "EventNexusUpdateCheckStarted",
            null);
        var processed = 0;
        var total = Entries.Count(entry =>
            entry.Source == DownloaderSource.Nexus &&
            entry.NexusModId is not null);
        try
        {
            var progress =
                new BufferedDispatcherProgress<NexusModUpdateCheckProgress>(
                _uiDispatcher,
                value =>
                {
                    if (_disposed)
                        return;
                    if (value.UpdatedEntry is not null)
                    {
                        UpdateUpdateCheckRow(value.UpdatedEntry);
                        if (value.UpdatedEntry.UpdateCheckStatus ==
                            NexusUpdateCheckStatus.ApiError)
                        {
                            _recordSessionEvent?.Invoke(
                                "NexusUpdateCheckEntryFailed",
                                $"{value.UpdatedEntry.Name}: " +
                                value.UpdatedEntry.UpdateCheckMessage);
                        }
                    }
                    UpdateMaximum(ref processed, value.Completed);
                    StatusMessage = string.Format(
                        L("DownloaderUpdateCheckProgress"),
                        Volatile.Read(ref processed),
                        value.Total,
                        value.UpdatesFound);
                });
            var summary = await _updateCheckService.CheckAllAsync(
                progress,
                cancellation.Token).ConfigureAwait(false);
            await progress.WaitForDrainAsync().ConfigureAwait(false);
            if (!summary.Canceled &&
                _archiveFamilyReconcile is not null)
            {
                await _archiveFamilyReconcile().ConfigureAwait(false);
            }
            await RunOnUiAsync(() =>
            {
                StatusMessage = summary.Canceled
                    ? string.Format(
                        L("DownloaderUpdateCheckCanceled"),
                        summary.Checked,
                        total)
                    : string.Format(
                        L("DownloaderUpdateCheckSummary"),
                        Math.Max(
                            0,
                            summary.Checked -
                            summary.MissingIdentity),
                        summary.UpdateAvailable,
                        summary.UpToDate,
                        summary.Errors +
                        summary.ManualReviewRequired,
                        summary.MissingIdentity);
                _recordSessionEvent?.Invoke(
                    "EventNexusUpdateCheckCompleted",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "проверено {0}, актуально {1}, обновлений {2}, " +
                        "ручная проверка {3}, недостаточно данных {4}, " +
                        "ошибок {5}, отменено {6}",
                        summary.Checked,
                        summary.UpToDate,
                        summary.UpdateAvailable,
                        summary.ManualReviewRequired,
                        summary.MissingIdentity,
                        summary.Errors,
                        summary.Canceled));
            });
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
            await RunOnUiAsync(() =>
            {
                StatusMessage = string.Format(
                    L("DownloaderUpdateCheckCanceled"),
                    Volatile.Read(ref processed),
                    total);
            });
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            await RunOnUiAsync(() =>
            {
                StatusMessage = string.Format(
                    L("DownloaderUpdateCheckStartFailed"),
                    exception.Message);
                _recordSessionEvent?.Invoke(
                    "EventNexusUpdateCheckCompleted",
                    exception.Message);
            });
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_updateCheckCancellation, cancellation))
                _updateCheckCancellation = null;
            _isCheckingUpdates = false;
            await RunOnUiAsync(() =>
            {
                OnPropertyChanged(nameof(IsCheckingUpdates));
                OnPropertyChanged(nameof(CheckUpdatesLabel));
                CheckSelectedUpdateCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private async Task RunOnUiAsync(Action action)
    {
        if (_disposed)
            return;
        var dispatcher = _uiDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        if (dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished)
        {
            return;
        }
        await dispatcher.InvokeAsync(action);
    }

    private void UpdateUpdateCheckRow(DownloaderEntry source)
    {
        var target = Entries.FirstOrDefault(entry =>
            entry.Id == source.Id);
        if (target is null)
            return;

        if (!string.Equals(
                target.NexusFileUuid,
                source.NexusFileUuid,
                StringComparison.Ordinal))
        {
            target.NexusFileUuid = source.NexusFileUuid;
            target.Notify(nameof(DownloaderEntry.NexusFileUuid));
        }
        target.AvailableVersion = source.AvailableVersion;
        target.AvailableFileId = source.AvailableFileId;
        target.AvailableFileUuid = source.AvailableFileUuid;
        target.LastUpdateCheckUtc = source.LastUpdateCheckUtc;
        target.UpdateCheckMessage = source.UpdateCheckMessage;
        target.UpdateCheckStatus = source.UpdateCheckStatus;

        if (SelectedEntry?.Id == source.Id)
            OnPropertyChanged(nameof(CanOpenSelectedUpdate));
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current >= value ||
                Interlocked.CompareExchange(
                    ref target,
                    value,
                    current) == current)
            {
                return;
            }
        }
    }

    private async Task CheckSelectedUpdateAsync()
    {
        if (_updateCheckService is null || SelectedEntry is null)
            return;
        var entryId = SelectedEntry.Id;
        var name = SelectedEntry.Name;
        SelectedEntry.UpdateCheckStatus =
            NexusUpdateCheckStatus.Checking;
        try
        {
            var result = await _updateCheckService.CheckEntryAsync(entryId);
            if (_archiveFamilyReconcile is not null)
                await _archiveFamilyReconcile();
            var persisted = await _repository.LoadEntryAsync(entryId);
            if (persisted is not null)
                Upsert(persisted);
            StatusMessage = result.Status switch
            {
                NexusUpdateCheckStatus.UpToDate or
                NexusUpdateCheckStatus.OlderVersion =>
                    $"{name}: установлена актуальная версия " +
                    $"{result.CurrentFile?.Version ?? SelectedEntry.Version}",
                NexusUpdateCheckStatus.UpdateAvailable =>
                    $"{name}: доступно обновление " +
                    $"{result.CurrentFile?.Version ?? SelectedEntry.Version} → " +
                    $"{result.LatestFileInSameChain?.Version}",
                _ => $"{name}: {result.Message}"
            };
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            StatusMessage = exception.Message;
            var persisted = await _repository.LoadEntryAsync(entryId);
            if (persisted is not null)
                Upsert(persisted);
        }
    }

    private async Task OpenSelectedUpdateAsync()
    {
        if (!CanOpenSelectedUpdate || SelectedEntry is null)
            return;
        await _manualQueue.StartAsync([SelectedEntry]);
        await OpenManualBrowserAsync();
    }

    private async Task UpdateSelectedAsync()
    {
        var result = await _updateBatchWorkflow.ExecuteAsync(
            SelectedEntries().ToArray(),
            Entries.ToArray());
        StatusMessage = string.Format(
            L("DownloaderBatchUpdateSummary"),
            result.Accepted,
            result.Skipped,
            result.AlreadyHandled,
            result.Failed);
        if (!string.IsNullOrWhiteSpace(result.BrowserError))
        {
            StatusMessage = string.Format(
                L("DownloaderBatchUpdateOpenFailed"),
                StatusMessage,
                result.BrowserError);
        }
        if (result.HasFailure)
            _showError?.Invoke(StatusMessage);
    }

    private async Task OpenManualBrowserAsync()
    {
        if (_settings().NexusBrowser == NexusBrowserMode.External)
        {
            if (_externalBrowser is null)
                throw new InvalidOperationException(
                    L("DownloaderExternalBrowserUnavailable"));
            await _externalBrowser.OpenAsync(
                _manualQueue.Current ??
                throw new InvalidOperationException(
                    L("DownloaderManualQueueEmpty")));
            return;
        }
        if (_internalBrowser is null)
            throw new InvalidOperationException(
                L("DownloaderInternalBrowserUnavailable"));
        await _internalBrowser.ShowAsync(
            _manualQueue,
            _localization);
    }

    private async Task ExecuteOperationsAsync(
        Func<DownloaderEntry, bool> predicate,
        Func<DownloaderEntry, Task> action)
    {
        foreach (var entry in OperationEntries().Where(predicate).ToArray())
            await action(entry);
    }

    private async Task CancelSelectedAsync()
    {
        foreach (var entry in SelectedEntries().ToArray())
        {
            if (_manualQueue.IsActive &&
                _manualQueue.Current?.Entry.Id == entry.Id)
            {
                await _manualQueue.CancelEntryAsync(entry.Id);
                entry.Status = DownloaderStatus.Canceled;
                entry.NotifyAll();
                continue;
            }
            if (entry.Status is DownloaderStatus.Waiting or
                DownloaderStatus.Downloading or
                DownloaderStatus.Paused)
            {
                await _queue.CancelAsync(entry.Id);
            }
        }
        NotifyMassCommands();
        NotifySelectedCommands();
    }

    private async Task ImportAsync()
    {
        var path = _dialogs.SelectImportFile();
        if (path is null)
            return;
        try
        {
            var prepared = _dialogs.ConfigureImport(
                path,
                _importer,
                Entries.ToArray(),
                _localization);
            if (prepared is null)
                return;
            foreach (var entry in prepared.Entries)
                entry.IsSelected = false;
            var result = await _repository.ApplyImportAsync(
                prepared.Entries,
                prepared.ReplaceCatalog);
            try
            {
                await ReloadEntriesAsync();
            }
            catch (Exception refreshException)
            {
                var logPath = _exceptionLogger?.Invoke(refreshException)
                    ?? string.Empty;
                StatusMessage = string.Format(
                    L("DownloaderImportRefreshFailed"),
                    logPath);
                return;
            }
            StatusMessage = string.Format(
                L("DownloaderImportCompleted"),
                result.Added,
                result.Updated,
                result.Unchanged,
                result.FinalTotal);
        }
        catch (Exception exception)
        {
            StatusMessage = string.Format(
                L("DownloaderImportFailed"),
                exception.Message);
        }
    }

    private async Task AddManualAsync()
    {
        var result = _dialogs.EditManual(null, _localization);
        if (result is null)
            return;
        var entry = new DownloaderEntry
        {
            Name = result.Name,
            Category = result.Category,
            Source = result.Source,
            Author = result.Author,
            Url = result.Url,
            Version = result.Version,
            Status = result.ArchivePath is null
                ? DownloaderStatus.Added
                : DownloaderStatus.Ready,
            IsSelected = true
        };
        await _repository.SaveEntryAsync(entry);
        AddTracked(entry);
        if (result.ArchivePath is not null)
            await PublishAttachedAsync(entry, result.ArchivePath);
        Renumber();
        RefreshChoices();
    }

    public async Task<bool> AddUnresolvedNexusEntryAsync(
        NexusModIdentity targetMod,
        string? safeUrl = null,
        string? fallbackName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NexusGameIdentityBridge.TryGetGameDomain(targetMod.GameId, out var gameDomain) ||
            string.IsNullOrWhiteSpace(gameDomain))
        {
            return false;
        }

        await _unresolvedNexusEntryGate.WaitAsync(cancellationToken);
        try
        {
            var persisted = await _repository.LoadEntriesAsync(cancellationToken);
            var persistedExact = persisted
                .Where(e => e.Source == DownloaderSource.Nexus &&
                            e.NexusModId == targetMod.ModId &&
                            string.Equals(e.GameDomain, gameDomain, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (persistedExact.Count > 0)
            {
                var missingInLive = persistedExact
                    .Where(p => !Entries.Any(live => live.Id == p.Id))
                    .ToList();

                if (missingInLive.Count > 0)
                {
                    if (_uiDispatcher is not null && !_uiDispatcher.CheckAccess())
                    {
                        await _uiDispatcher.InvokeAsync(() =>
                        {
                            foreach (var row in missingInLive)
                            {
                                Upsert(row);
                            }
                        });
                    }
                    else
                    {
                        foreach (var row in missingInLive)
                        {
                            Upsert(row);
                        }
                    }
                }

                return true;
            }

            NexusModMetadata? metadata;
            try
            {
                metadata = await _nexus.GetModMetadataAsync(gameDomain, targetMod.ModId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }

            if (metadata is null)
            {
                return false;
            }

            string url;
            if (!string.IsNullOrWhiteSpace(safeUrl) &&
                Uri.TryCreate(safeUrl, UriKind.Absolute, out var parsedUri) &&
                (parsedUri.Scheme == Uri.UriSchemeHttp || parsedUri.Scheme == Uri.UriSchemeHttps))
            {
                url = parsedUri.ToString();
            }
            else
            {
                url = $"https://www.nexusmods.com/{gameDomain}/mods/{targetMod.ModId}";
            }

            var entry = new DownloaderEntry
            {
                Name = !string.IsNullOrWhiteSpace(metadata.Name) ? metadata.Name : (fallbackName ?? $"Nexus mod {targetMod.ModId}"),
                Author = metadata.Author ?? string.Empty,
                Category = metadata.Category ?? string.Empty,
                Version = metadata.Version ?? string.Empty,
                Source = DownloaderSource.Nexus,
                GameDomain = gameDomain,
                NexusModId = targetMod.ModId,
                NexusFileId = null,
                Url = url,
                Status = DownloaderStatus.Added,
                IsSelected = false
            };

            await _repository.SaveEntryAsync(entry, cancellationToken);

            if (_uiDispatcher is not null && !_uiDispatcher.CheckAccess())
            {
                await _uiDispatcher.InvokeAsync(() => Upsert(entry));
            }
            else
            {
                Upsert(entry);
            }

            return true;
        }
        finally
        {
            _unresolvedNexusEntryGate.Release();
        }
    }

    private async Task EditAsync()
    {
        if (SelectedEntry is null)
            return;
        var result = _dialogs.EditManual(SelectedEntry, _localization);
        if (result is null)
            return;
        SelectedEntry.Name = result.Name;
        SelectedEntry.Category = result.Category;
        SelectedEntry.Source = result.Source;
        SelectedEntry.Author = result.Author;
        SelectedEntry.Url = result.Url;
        SelectedEntry.Version = result.Version;
        SelectedEntry.NotifyAll();
        await _repository.SaveEntryAsync(SelectedEntry);
        if (result.ArchivePath is not null)
            await PublishAttachedAsync(SelectedEntry, result.ArchivePath);
        EntriesView.Refresh();
        RefreshChoices();
    }

    private async Task AttachArchiveAsync()
    {
        if (SelectedEntry is null)
            return;
        var path = _dialogs.SelectArchiveFile();
        if (path is not null)
            await PublishAttachedAsync(SelectedEntry, path);
    }

    private async Task PublishAttachedAsync(
        DownloaderEntry entry,
        string path)
    {
        var publication = await _publisher.PublishAsync(
            entry,
            path,
            _settings().LibraryRoot);
        entry.LocalArchivePath = publication.ArchivePath;
        entry.Size = publication.FileSize;
        entry.Progress = 100;
        entry.Status = DownloaderStatus.Downloaded;
        if (entry.Source == DownloaderSource.Nexus)
        {
            if (entry.AvailableFileId == entry.NexusFileId &&
                !string.IsNullOrWhiteSpace(entry.AvailableFileUuid))
            {
                entry.NexusFileUuid = entry.AvailableFileUuid;
            }
            entry.UpdateCheckStatus = NexusUpdateCheckStatus.UpToDate;
            entry.AvailableVersion = string.Empty;
            entry.AvailableFileId = null;
            entry.AvailableFileUuid = string.Empty;
            entry.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            entry.UpdateCheckMessage =
                "Опубликованная версия отмечена как актуальная.";
        }
        await _repository.SaveEntryAsync(entry);
        entry.NotifyAll();
        await _archiveRefreshOwner.RequestAndWaitAsync(entry);
    }

    private async Task ExportAsync()
    {
        var path = _dialogs.SelectExportFile();
        if (path is not null)
            await _importer.ExportCsvAsync(path, Entries);
    }

    private async Task DeleteSelectedAsync()
    {
        var targets = SelectedEntries().ToArray();
        if (targets.Length == 0 ||
            !_dialogs.ConfirmDeleteRecords(
                targets,
                _localization))
        {
            return;
        }
        foreach (var entry in targets)
        {
            if (entry.Status is DownloaderStatus.Downloading or
                DownloaderStatus.Waiting or
                DownloaderStatus.Paused)
            {
                await _queue.CancelAsync(entry.Id);
            }
        }
        await _repository.DeleteEntriesAsync(
            targets.Select(entry => entry.Id).ToArray());
        foreach (var entry in targets)
        {
            entry.PropertyChanged -= Entry_OnPropertyChanged;
            Entries.Remove(entry);
        }
        Renumber();
        RefreshChoices();
        EntriesView.Refresh();
        NotifyMassCommands();
    }

    private async Task ReloadEntriesAsync()
    {
        var loaded = (await _repository.LoadEntriesAsync()).ToArray();
        var dispatcher = _uiDispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(
                () => SynchronizeEntries(loaded));
            return;
        }
        SynchronizeEntries(loaded);
    }

    private void SynchronizeEntries(
        IReadOnlyList<DownloaderEntry> loaded)
    {
        var selectedId = SelectedEntry?.Id;
        var existingById = Entries.ToDictionary(entry => entry.Id);
        var ordered = new List<DownloaderEntry>(loaded.Count);
        _synchronizingEntries = true;
        try
        {
            var loadedIds = loaded
                .Select(entry => entry.Id)
                .ToHashSet();
            foreach (var obsolete in Entries
                         .Where(entry => !loadedIds.Contains(entry.Id))
                         .ToArray())
            {
                obsolete.PropertyChanged -= Entry_OnPropertyChanged;
            }

            foreach (var source in loaded)
            {
                if (!existingById.TryGetValue(
                        source.Id,
                        out var target))
                {
                    target = source;
                    target.PropertyChanged += Entry_OnPropertyChanged;
                }
                else
                {
                    CopyEntry(source, target);
                    target.NotifyAll();
                }
                ordered.Add(target);
            }

            using (EntriesView.DeferRefresh())
            {
                _entries.ReplaceAll(ordered);
                SelectedEntry = selectedId is null
                    ? null
                    : Entries.FirstOrDefault(entry =>
                        entry.Id == selectedId);
                Renumber();
                RefreshChoices();
            }
        }
        finally
        {
            _synchronizingEntries = false;
        }
        EntriesView.Refresh();
        NotifyMassCommands();
    }

    private bool Filter(object value)
    {
        if (value is not DownloaderEntry entry)
            return false;
        var search = string.IsNullOrWhiteSpace(SearchText) ||
            new[]
            {
                entry.Name,
                entry.Author,
                entry.Url,
                entry.NexusModId?.ToString(
                    CultureInfo.InvariantCulture) ?? string.Empty,
                entry.NexusFileId?.ToString(
                    CultureInfo.InvariantCulture) ?? string.Empty
            }.Any(text => text.Contains(
                SearchText,
                StringComparison.CurrentCultureIgnoreCase));
        return search &&
            (StatusFilter == "All" ||
             (StatusFilter ==
                  NexusUpdateClassifier.UpdateAvailableFilterValue &&
              NexusUpdateClassifier.IsUpdateAvailable(entry)) ||
             (entry.Status.ToString() == StatusFilter) ||
             (StatusFilter == nameof(DownloaderStatus.Downloaded) &&
              PublicationStatusPolicy.IsPending(entry.Status))) &&
            (CategoryFilter == "All" ||
             entry.Category == CategoryFilter) &&
            (SourceFilter == "All" ||
             entry.Source.ToString() == SourceFilter);
    }

    private IEnumerable<DownloaderEntry> SelectedEntries() =>
        Entries.Where(entry => entry.IsSelected);

    private IEnumerable<DownloaderEntry> OperationEntries() =>
        SelectedEntries();

    private void SetSelection(
        IEnumerable<DownloaderEntry> entries,
        bool selected)
    {
        foreach (var entry in entries.ToArray())
            entry.IsSelected = selected;
        NotifyMassCommands();
    }

    private void AddTracked(DownloaderEntry entry)
    {
        entry.PropertyChanged += Entry_OnPropertyChanged;
        Entries.Add(entry);
    }

    private void Upsert(DownloaderEntry entry)
    {
        var existing = Entries.FirstOrDefault(candidate =>
            candidate.Id == entry.Id);
        if (existing is null)
            AddTracked(entry);
        else
        {
            if (!ReferenceEquals(existing, entry))
                CopyEntry(entry, existing);
            existing.NotifyAll();
        }
        Renumber();
        RefreshChoices();
        EntriesView.Refresh();
        if (SelectedEntry?.Id == entry.Id)
        {
            OnPropertyChanged(nameof(CanStartSelectedEntry));
            OnPropertyChanged(nameof(CanPauseSelectedEntry));
            OnPropertyChanged(nameof(CanResumeSelectedEntry));
            OnPropertyChanged(nameof(CanCancelSelectedEntry));
            OnPropertyChanged(nameof(CanRetrySelectedEntry));
            OnPropertyChanged(nameof(CanOpenDownloadedEntry));
            OnPropertyChanged(nameof(CanCheckSelectedUpdate));
            OnPropertyChanged(nameof(CanOpenSelectedUpdate));
        }
    }

    private static void CopyEntry(
        DownloaderEntry source,
        DownloaderEntry target)
    {
        target.Number = source.Number;
        target.IsSelected = source.IsSelected;
        target.Name = source.Name;
        target.Category = source.Category;
        target.Source = source.Source;
        target.Author = source.Author;
        target.Url = source.Url;
        target.AdditionalUrl = source.AdditionalUrl;
        target.GameDomain = source.GameDomain;
        target.NexusModId = source.NexusModId;
        target.NexusFileId = source.NexusFileId;
        target.NexusFileUuid = source.NexusFileUuid;
        target.Version = source.Version;
        target.UpdateCheckStatus = source.UpdateCheckStatus;
        target.AvailableVersion = source.AvailableVersion;
        target.AvailableFileId = source.AvailableFileId;
        target.AvailableFileUuid = source.AvailableFileUuid;
        target.LastUpdateCheckUtc = source.LastUpdateCheckUtc;
        target.UpdateCheckMessage = source.UpdateCheckMessage;
        target.DownloadOrder = source.DownloadOrder;
        target.CatalogId = source.CatalogId;
        target.DecisionGroup = source.DecisionGroup;
        target.Sha256 = source.Sha256;
        target.ArchiveFileName = source.ArchiveFileName;
        target.Status = source.Status;
        target.Progress = source.Progress;
        target.BytesPerSecond = source.BytesPerSecond;
        target.Size = source.Size;
        target.LocalArchivePath = source.LocalArchivePath;
        target.TemporaryPath = source.TemporaryPath;
        target.Error = source.Error;
        target.Description = source.Description;
        target.UpdatedAt = source.UpdatedAt;
    }

    private DownloaderEntry? FindDuplicate(DownloaderEntry candidate) =>
        Entries.FirstOrDefault(entry =>
            candidate.NexusModId is not null &&
            candidate.NexusFileId is not null &&
            entry.NexusModId == candidate.NexusModId &&
            entry.NexusFileId == candidate.NexusFileId ||
            !string.IsNullOrWhiteSpace(candidate.Url) &&
            string.Equals(
                entry.Url.TrimEnd('/'),
                candidate.Url.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase));

    private async void Entry_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_disposed || _synchronizingEntries)
            return;
        if (sender is DownloaderEntry entry &&
            e.PropertyName == nameof(DownloaderEntry.IsSelected))
        {
            NotifyMassCommands();
            try
            {
                await _repository.SaveEntryAsync(entry);
            }
            catch (Exception exception)
            {
                _exceptionLogger?.Invoke(exception);
            }
        }
        if (e.PropertyName is
            nameof(DownloaderEntry.UpdateCheckStatus) or
            nameof(DownloaderEntry.AvailableFileId))
        {
            EntriesView.Refresh();
            NotifyMassCommands();
        }
    }

    private void Queue_OnEntryChanged(
        object? sender,
        DownloaderEntry entry)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            UpdateQueueEntry(entry);
            if (entry.Status == DownloaderStatus.Downloaded)
                _archiveRefreshOwner.TryRequest(entry);
            return;
        }
        _pendingQueueUpdates[entry.Id] = entry;
        if (dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished ||
            Interlocked.Exchange(
                ref _queueUpdateScheduled,
                1) != 0)
        {
            return;
        }
        _ = dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(DrainQueueUpdates));
    }

    private void DrainQueueUpdates()
    {
        Interlocked.Exchange(ref _queueUpdateScheduled, 0);
        foreach (var pair in _pendingQueueUpdates.ToArray())
        {
            if (!_pendingQueueUpdates.TryRemove(
                    pair.Key,
                    out var entry))
            {
                continue;
            }
            UpdateQueueEntry(entry);
            if (entry.Status == DownloaderStatus.Downloaded)
                _archiveRefreshOwner.TryRequest(entry);
        }
    }

    private void UpdateQueueEntry(DownloaderEntry source)
    {
        var target = Entries.FirstOrDefault(entry =>
            entry.Id == source.Id);
        if (target is null)
        {
            Upsert(source);
            return;
        }

        var statusChanged = target.Status != source.Status;
        var categoryChanged = !string.Equals(
            target.Category,
            source.Category,
            StringComparison.Ordinal);
        var sourceChanged = target.Source != source.Source;
        var nameChanged = !string.Equals(
            target.Name,
            source.Name,
            StringComparison.Ordinal);
        var detailsChanged =
            nameChanged ||
            categoryChanged ||
            sourceChanged ||
            !string.Equals(target.Author, source.Author, StringComparison.Ordinal) ||
            !string.Equals(target.Url, source.Url, StringComparison.Ordinal) ||
            !string.Equals(target.AdditionalUrl, source.AdditionalUrl, StringComparison.Ordinal) ||
            !string.Equals(target.GameDomain, source.GameDomain, StringComparison.Ordinal) ||
            target.NexusModId != source.NexusModId ||
            target.NexusFileId != source.NexusFileId ||
            !string.Equals(target.NexusFileUuid, source.NexusFileUuid, StringComparison.Ordinal) ||
            !string.Equals(target.Version, source.Version, StringComparison.Ordinal) ||
            target.UpdateCheckStatus != source.UpdateCheckStatus ||
            !string.Equals(target.AvailableVersion, source.AvailableVersion, StringComparison.Ordinal) ||
            target.AvailableFileId != source.AvailableFileId ||
            !string.Equals(target.AvailableFileUuid, source.AvailableFileUuid, StringComparison.Ordinal) ||
            target.LastUpdateCheckUtc != source.LastUpdateCheckUtc ||
            !string.Equals(target.UpdateCheckMessage, source.UpdateCheckMessage, StringComparison.Ordinal) ||
            target.Size != source.Size ||
            !string.Equals(target.LocalArchivePath, source.LocalArchivePath, StringComparison.Ordinal) ||
            !string.Equals(target.TemporaryPath, source.TemporaryPath, StringComparison.Ordinal) ||
            !string.Equals(target.Error, source.Error, StringComparison.Ordinal) ||
            !string.Equals(target.Description, source.Description, StringComparison.Ordinal) ||
            target.UpdatedAt != source.UpdatedAt ||
            !string.Equals(target.Sha256, source.Sha256, StringComparison.Ordinal) ||
            !string.Equals(target.ArchiveFileName, source.ArchiveFileName, StringComparison.Ordinal);

        target.Status = source.Status;
        target.Progress = source.Progress;
        target.BytesPerSecond = source.BytesPerSecond;
        target.Name = source.Name;
        target.Category = source.Category;
        target.Source = source.Source;
        target.Author = source.Author;
        target.Url = source.Url;
        target.AdditionalUrl = source.AdditionalUrl;
        target.GameDomain = source.GameDomain;
        target.NexusModId = source.NexusModId;
        target.NexusFileId = source.NexusFileId;
        target.NexusFileUuid = source.NexusFileUuid;
        target.Version = source.Version;
        target.UpdateCheckStatus = source.UpdateCheckStatus;
        target.AvailableVersion = source.AvailableVersion;
        target.AvailableFileId = source.AvailableFileId;
        target.AvailableFileUuid = source.AvailableFileUuid;
        target.LastUpdateCheckUtc = source.LastUpdateCheckUtc;
        target.UpdateCheckMessage = source.UpdateCheckMessage;
        target.Size = source.Size;
        target.LocalArchivePath = source.LocalArchivePath;
        target.TemporaryPath = source.TemporaryPath;
        target.Error = source.Error;
        target.Description = source.Description;
        target.UpdatedAt = source.UpdatedAt;
        target.Sha256 = source.Sha256;
        target.ArchiveFileName = source.ArchiveFileName;
        if (detailsChanged)
        {
            target.Notify(
                nameof(DownloaderEntry.Name),
                nameof(DownloaderEntry.Category),
                nameof(DownloaderEntry.Source),
                nameof(DownloaderEntry.Author),
                nameof(DownloaderEntry.Url),
                nameof(DownloaderEntry.AdditionalUrl),
                nameof(DownloaderEntry.GameDomain),
                nameof(DownloaderEntry.NexusModId),
                nameof(DownloaderEntry.NexusFileId),
                nameof(DownloaderEntry.NexusFileUuid),
                nameof(DownloaderEntry.Version),
                nameof(DownloaderEntry.UpdateCheckStatus),
                nameof(DownloaderEntry.AvailableVersion),
                nameof(DownloaderEntry.AvailableFileId),
                nameof(DownloaderEntry.AvailableFileUuid),
                nameof(DownloaderEntry.LastUpdateCheckUtc),
                nameof(DownloaderEntry.UpdateCheckMessage),
                nameof(DownloaderEntry.Size),
                nameof(DownloaderEntry.LocalArchivePath),
                nameof(DownloaderEntry.TemporaryPath),
                nameof(DownloaderEntry.Error),
                nameof(DownloaderEntry.Description),
                nameof(DownloaderEntry.UpdatedAt),
                nameof(DownloaderEntry.Sha256),
                nameof(DownloaderEntry.Sha256Display),
                nameof(DownloaderEntry.ArchiveFileName));
        }

        var refreshView =
            statusChanged && StatusFilter != "All" ||
            categoryChanged && CategoryFilter != "All" ||
            sourceChanged && SourceFilter != "All" ||
            nameChanged && !string.IsNullOrWhiteSpace(SearchText);
        if (categoryChanged || sourceChanged)
            RefreshChoices();
        if (refreshView)
            EntriesView.Refresh();
        if (statusChanged || detailsChanged)
        {
            NotifySelectedCommands();
            NotifyMassCommands();
            OnPropertyChanged(nameof(CanStartSelectedEntry));
            OnPropertyChanged(nameof(CanPauseSelectedEntry));
            OnPropertyChanged(nameof(CanResumeSelectedEntry));
            OnPropertyChanged(nameof(CanCancelSelectedEntry));
            OnPropertyChanged(nameof(CanRetrySelectedEntry));
            OnPropertyChanged(nameof(CanOpenDownloadedEntry));
            OnPropertyChanged(nameof(CanCheckSelectedUpdate));
            OnPropertyChanged(nameof(CanOpenSelectedUpdate));
        }
    }

    private void ManualQueue_OnChanged(
        object? sender,
        EventArgs e)
    {
        NotifyMassCommands();
        NotifySelectedCommands();
        OnPropertyChanged(nameof(CanStartSelectedEntry));
        OnPropertyChanged(nameof(CanCancelSelectedEntry));
    }

    private void RefreshChoices()
    {
        if (StatusChoices.Count == 0)
        {
            StatusChoices =
            [
                new("All", L("DownloaderAllStatuses")),
                new(
                    NexusUpdateClassifier.UpdateAvailableFilterValue,
                    L("DownloaderFilterUpdateAvailable")),
                .. PublicationStatusPolicy.UserFacingStatuses.Select(status =>
                    new ChoiceItem(
                        status.ToString(),
                        L($"DownloaderStatus{status}")))
            ];
        }
        else
        {
            foreach (var choice in StatusChoices)
            {
                if (choice.Value == "All")
                    choice.Display = L("DownloaderAllStatuses");
                else if (choice.Value == NexusUpdateClassifier.UpdateAvailableFilterValue)
                    choice.Display = L("DownloaderFilterUpdateAvailable");
                else
                    choice.Display = L($"DownloaderStatus{choice.Value}");
            }
        }

        if (SourceChoices.Count == 0)
        {
            SourceChoices =
            [
                new("All", L("DownloaderAllSources")),
                .. Enum.GetValues<DownloaderSource>().Select(source =>
                    new ChoiceItem(
                        source.ToString(),
                        L($"DownloaderSource{source}")))
            ];
        }
        else
        {
            foreach (var choice in SourceChoices)
            {
                if (choice.Value == "All")
                    choice.Display = L("DownloaderAllSources");
                else
                    choice.Display = L($"DownloaderSource{choice.Value}");
            }
        }

        if (!StatusChoices.Any(choice =>
                choice.Value == StatusFilter))
        {
            StatusFilter = "All";
        }
        if (!SourceChoices.Any(choice =>
                choice.Value == SourceFilter))
        {
            SourceFilter = "All";
        }

        if (CategoryChoices.Count == 0)
        {
            CategoryChoices.Add(new("All", L("DownloaderAllCategories")));
        }
        else
        {
            CategoryChoices[0].Display = L("DownloaderAllCategories");
        }

        var distinctCategories = Entries
            .Select(entry => entry.Category)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .OrderBy(value => value)
            .ToHashSet();

        for (var i = CategoryChoices.Count - 1; i >= 1; i--)
        {
            if (!distinctCategories.Contains(CategoryChoices[i].Value))
                CategoryChoices.RemoveAt(i);
        }

        var existingSet = CategoryChoices.Select(c => c.Value).ToHashSet();
        foreach (var category in distinctCategories)
        {
            if (!existingSet.Contains(category))
                CategoryChoices.Add(new(category, category));
        }

        if (!CategoryChoices.Any(choice => choice.Value == CategoryFilter))
            CategoryFilter = "All";

        OnPropertyChanged(nameof(StatusChoices));
        OnPropertyChanged(nameof(SourceChoices));
        OnPropertyChanged(nameof(StatusFilter));
        OnPropertyChanged(nameof(SourceFilter));
        OnPropertyChanged(nameof(CategoryFilter));
    }

    private void Renumber()
    {
        for (var index = 0; index < Entries.Count; index++)
        {
            Entries[index].Number = index + 1;
            Entries[index].NotifyAll();
        }
    }

    private Task OpenPageAsync()
    {
        if (SelectedEntry is not { } entry || string.IsNullOrWhiteSpace(entry.Url))
            return Task.CompletedTask;

        var value = entry.Url;
        if (NexusBrowserNavigationPolicy.IsValidOpenNexusWebUrl(value, out var nexusUri) && nexusUri is not null)
        {
            var filesPageUri = DownloaderLinkParser.BuildNexusFilesPageUri(nexusUri);
            if (_settings().NexusBrowser == NexusBrowserMode.Internal &&
                RequestNavigateNexus is not null)
            {
                RequestNavigateNexus(filesPageUri.AbsoluteUri);
            }
            else
            {
                _dialogs.OpenUri(filesPageUri.AbsoluteUri);
            }

            if (_nexusRequirements is not null &&
                NexusRequirementRefreshEligibility.TryCreate(
                    entry,
                    out var identity))
            {
                _nexusRefreshCancellation = new CancellationTokenSource();
                _nexusRefreshTask = RefreshNexusAfterPageOpenAsync(
                    identity,
                    _nexusRefreshCancellation.Token);
                return _nexusRefreshTask;
            }

            return Task.CompletedTask;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var extUri) &&
            (extUri.Scheme == Uri.UriSchemeHttp || extUri.Scheme == Uri.UriSchemeHttps))
        {
            _dialogs.OpenUri(extUri.AbsoluteUri);
        }

        return Task.CompletedTask;
    }

    private async Task RefreshNexusAfterPageOpenAsync(
        NexusModIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _nexusRequirements!.RefreshAsync(
                identity,
                clearDismissals: false,
                cancellationToken);
            if (result.Outcome != NexusRequirementSyncOutcome.Success)
            {
                StatusMessage = _localization.Get(
                    result.Outcome == NexusRequirementSyncOutcome.Partial
                        ? "NexusRelationsAutomaticPartial"
                        : "NexusRelationsAutomaticFailed");
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            StatusMessage = _localization.Get(
                "NexusRelationsAutomaticFailed");
        }
        finally
        {
            _nexusRefreshCancellation?.Dispose();
            _nexusRefreshCancellation = null;
        }
    }

    private void OpenFile()
    {
        if (SelectedEntry is not null)
            _dialogs.OpenFile(SelectedEntry.LocalArchivePath);
    }

    private void OpenFolder()
    {
        if (SelectedEntry?.LocalArchivePath is { Length: > 0 } path)
            _dialogs.OpenFolder(Path.GetDirectoryName(path)!);
    }

    private void CopyLink()
    {
        if (SelectedEntry?.Url is { Length: > 0 } url)
            Clipboard.SetText(url);
    }

    private void CopySha256()
    {
        if (SelectedEntry?.Sha256 is { Length: > 0 } sha256)
            Clipboard.SetText(sha256);
    }

    private bool HasSelectedUrl() =>
        Uri.TryCreate(SelectedEntry?.Url, UriKind.Absolute, out _);

    private bool HasDownloadedFile() =>
        SelectedEntry?.Status == DownloaderStatus.Downloaded &&
        File.Exists(SelectedEntry.LocalArchivePath);

    private bool HasSelectedSha256() =>
        !string.IsNullOrWhiteSpace(SelectedEntry?.Sha256);

    private void NotifySelectedCommands()
    {
        OpenPageCommand.NotifyCanExecuteChanged();
        OpenFileCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        CopyLinkCommand.NotifyCanExecuteChanged();
        CopySha256Command.NotifyCanExecuteChanged();
        CheckSelectedUpdateCommand.NotifyCanExecuteChanged();
        OpenSelectedUpdateCommand.NotifyCanExecuteChanged();
    }

    private bool HasExplicitSelection() =>
        Entries.Any(entry => entry.IsSelected);

    private bool HasUpdateSelection() =>
        Entries.Any(entry =>
            entry.IsSelected &&
            NexusUpdateClassifier.IsUpdateAvailable(entry));

    private void NotifyMassCommands()
    {
        StartSelectedCommand.NotifyCanExecuteChanged();
        PauseSelectedCommand.NotifyCanExecuteChanged();
        ResumeSelectedCommand.NotifyCanExecuteChanged();
        CancelSelectedCommand.NotifyCanExecuteChanged();
        RetrySelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        UpdateSelectedCommand.NotifyCanExecuteChanged();
    }

    private void Localization_OnLanguageChanged(
        object? sender,
        EventArgs e)
    {
        foreach (var entry in Entries)
            entry.NotifyAll();
        foreach (var property in GetType().GetProperties()
                     .Where(property =>
                         property.PropertyType == typeof(string)))
        {
            OnPropertyChanged(property.Name);
        }
        RefreshChoices();
    }

    private string L(string key) => _localization.Get(key);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.EntryChanged -= Queue_OnEntryChanged;
        var archiveRefreshDisposal = _archiveRefreshOwner.DisposeAsync();
        try
        {
            _updateCheckCancellation?.Cancel();
            _nexusRefreshCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        if (_updateCheckTask is { } updateCheckTask)
        {
            try
            {
                await updateCheckTask
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException exception)
            {
                _exceptionLogger?.Invoke(exception);
            }
        }
        try
        {
            await _nexusRefreshTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await archiveRefreshDisposal;
        _manualQueue.Changed -= ManualQueue_OnChanged;
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
        foreach (var entry in Entries)
            entry.PropertyChanged -= Entry_OnPropertyChanged;
        await _queue.DisposeAsync();
    }
}

internal sealed class BulkObservableCollection<T> :
    ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(
            new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(
            new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Reset));
    }
}

internal sealed class BufferedDispatcherProgress<T> : IProgress<T>
{
    private const int BatchSize = 6;
    private readonly Dispatcher? _dispatcher;
    private readonly Action<T> _handler;
    private readonly ConcurrentQueue<T> _pending = new();
    private readonly object _drainGate = new();
    private TaskCompletionSource<bool> _drained =
        CompletedDrain();
    private int _scheduled;

    public BufferedDispatcherProgress(
        Dispatcher? dispatcher,
        Action<T> handler)
    {
        _dispatcher = dispatcher;
        _handler = handler;
    }

    public void Report(T value)
    {
        if (_dispatcher is null)
        {
            _handler(value);
            return;
        }

        lock (_drainGate)
        {
            if (_drained.Task.IsCompleted)
            {
                _drained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        _pending.Enqueue(value);
        ScheduleDrain();
    }

    public Task WaitForDrainAsync()
    {
        lock (_drainGate)
            return _drained.Task;
    }

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
            return;
        if (_dispatcher!.HasShutdownStarted ||
            _dispatcher.HasShutdownFinished)
        {
            DiscardPending();
            return;
        }
        try
        {
            _ = _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(Drain));
        }
        catch (InvalidOperationException)
        {
            DiscardPending();
        }
    }

    private void Drain()
    {
        var processed = 0;
        while (processed < BatchSize &&
               _pending.TryDequeue(out var value))
        {
            _handler(value);
            processed++;
        }

        if (!_pending.IsEmpty)
        {
            _ = _dispatcher!.BeginInvoke(
                DispatcherPriority.Background,
                new Action(Drain));
            return;
        }

        Interlocked.Exchange(ref _scheduled, 0);
        if (!_pending.IsEmpty)
        {
            ScheduleDrain();
            return;
        }
        CompleteDrain();
    }

    private void DiscardPending()
    {
        while (_pending.TryDequeue(out _))
        {
        }
        Interlocked.Exchange(ref _scheduled, 0);
        CompleteDrain();
    }

    private void CompleteDrain()
    {
        lock (_drainGate)
        {
            if (_pending.IsEmpty &&
                Volatile.Read(ref _scheduled) == 0)
            {
                _drained.TrySetResult(true);
            }
        }
    }

    private static TaskCompletionSource<bool> CompletedDrain()
    {
        var value = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        value.SetResult(true);
        return value;
    }
}
