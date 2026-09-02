using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.GameMaintenance;
using RipperWorks.Organizer;

namespace RipperWorks.App.ViewModels;

public sealed class LibraryViewModel :
    ObservableObject,
    IDisposable,
    ILibraryAnalysisLifetime,
    ILibraryUiStatePersistenceLifetime
{
    private const string AllFilter = "All";
    private const string UngroupedFilter = "Ungrouped";
    private const string ProblemsFilter = "Problems";

    private readonly Func<RipperWorksSettings> _getSettings;
    private readonly ILibraryIndexService _libraryIndex;
    private readonly LocalizationService _localization;
    private readonly OrganizerRepository _repository;
    private readonly OrganizerAnalysisService _analysis;
    private readonly AnalysisQueueService _queue;
    private readonly ILibraryWindowService _windowService;
    private readonly InstallPlanService _installPlan;
    private readonly ModInstallationService? _installationService;
    private readonly ModRemovalService? _removalService;
    private readonly PackageRelationService _relations;
    private readonly IBatchInstallCoordinator? _batchInstall;
    private readonly IBatchRemovalCoordinator? _batchRemoval;
    private readonly IModArchiveSwitchService? _archiveSwitch;
    private readonly LibraryArchiveDeletionService? _archiveDeletion;
    private readonly ArchiveFamilyReconciliationService?
        _archiveFamilyReconciliation;
    private readonly Func<GameProfileRecord?> _getGameProfile;
    private readonly Action<string, string?>? _recordSessionEvent;
    private readonly IUserDialogService? _userDialogs;
    private readonly Action<Exception>? _exceptionLogger;
    private readonly INexusRequirementRefreshService? _nexusRequirements;
    private readonly NexusRequirementRefreshRunner? _nexusRefresh;
    private readonly INexusRequirementRelationsService? _nexusRelations;
    private readonly NexusInstallDependencyWarningService? _nexusInstallWarning;
    private readonly IGameMaintenanceService? _gameMaintenance;
    private readonly Dispatcher _uiDispatcher;
    private readonly LibraryUiStatePersistenceOwner _uiStatePersistence;
    private readonly BufferedDispatcherProgress<BatchPackageStateChanged>
        _batchPackageStateChanges;
    private FomodPreparationBackgroundExecutor
        _fomodPreparationBackground = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly BulkObservableCollection<PackageRowViewModel> _packages = [];
    private readonly BulkObservableCollection<LibraryGroupViewModel> _groups = [];
    private string _searchText = string.Empty;
    private string _selectedFilter = AllFilter;
    private string _selectedAnalysisFilter = AllFilter;
    private string _selectedInstalledFilter = AllFilter;
    private string _selectedGroupFilter = AllFilter;
    private string _statusMessage = string.Empty;
    private string _newGroupName = string.Empty;
    private int _packageCount;
    private int _archiveCount;
    private int _skippedDirectoryCount;
    private DateTime? _lastUpdated;
    private PackageRowViewModel? _selectedPackage;
    private LibraryGroupViewModel? _selectedGroup;
    private CancellationTokenSource? _analysisCancellation;
    private Task _activeAnalysisCompletion = Task.CompletedTask;
    private CancellationTokenSource? _nexusRefreshCancellation;
    private Task _activeNexusRefreshCompletion = Task.CompletedTask;
    private bool _isAnalyzing;
    private int _analysisCompleted;
    private int _analysisTotal;
    private bool _loadingCachedState;
    private bool _recoveryRequired;
    private bool _isBatchBusy;
    private bool _isRefreshingNexusRelations;
    private bool _disposed;

    public LibraryViewModel(
        Func<RipperWorksSettings> getSettings,
        ILibraryIndexService libraryIndex,
        LocalizationService localization,
        OrganizerRepository repository,
        OrganizerAnalysisService analysis,
        AnalysisQueueService queue,
        ILibraryWindowService windowService,
        InstallPlanService? installPlan = null,
        Func<GameProfileRecord?>? getGameProfile = null,
        Action<string, string?>? recordSessionEvent = null,
        ModInstallationService? installationService = null,
        IUserDialogService? userDialogs = null,
        Action<Exception>? exceptionLogger = null,
        ModRemovalService? removalService = null,
        PackageRelationService? relations = null,
        IBatchInstallCoordinator? batchInstall = null,
        IBatchRemovalCoordinator? batchRemoval = null,
        IModArchiveSwitchService? archiveSwitch = null,
        LibraryArchiveDeletionService? archiveDeletion = null,
        ArchiveFamilyReconciliationService?
            archiveFamilyReconciliation = null,
        INexusRequirementRefreshService? nexusRequirements = null,
        INexusRequirementRelationsService? nexusRelationReader = null,
        IGameMaintenanceService? gameMaintenance = null)
    {
        _getSettings = getSettings;
        _libraryIndex = libraryIndex;
        _localization = localization;
        _repository = repository;
        _analysis = analysis;
        _queue = queue;
        _windowService = windowService;
        _installPlan = installPlan ??
            new InstallPlanService(new EmptyInstalledPathOwnerProvider());
        _getGameProfile = getGameProfile ?? (() => null);
        _recordSessionEvent = recordSessionEvent;
        _installationService = installationService;
        _removalService = removalService;
        _relations = relations ?? new PackageRelationService(repository);
        _batchInstall = batchInstall;
        _batchRemoval = batchRemoval;
        _archiveSwitch = archiveSwitch;
        _archiveDeletion = archiveDeletion;
        _archiveFamilyReconciliation = archiveFamilyReconciliation;
        _gameMaintenance = gameMaintenance;
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _batchPackageStateChanges = new(
            _uiDispatcher,
            ApplyBatchPackageStateChanged);
        if (_batchInstall is not null)
            _batchInstall.PackageStateChanged += OnBatchPackageStateChanged;
        if (_batchRemoval is not null)
            _batchRemoval.PackageStateChanged += OnBatchPackageStateChanged;
        _userDialogs = userDialogs;
        _exceptionLogger = exceptionLogger;
        _nexusRequirements = nexusRequirements;
        _nexusRefresh = nexusRequirements is null
            ? null
            : new NexusRequirementRefreshRunner(
                nexusRequirements,
                exceptionLogger);
        _nexusRelations = nexusRelationReader;
        _nexusInstallWarning = nexusRelationReader is null
            ? null
            : new NexusInstallDependencyWarningService(
                nexusRelationReader,
                localization);
        _uiStatePersistence = new(exception =>
            StatusMessage = exception.Message);
        PackagesView = new LibraryGroupCollectionView(
            Packages,
            () => Groups,
            ShowEmptyGroupInCurrentFilter);
        if (PackagesView is IEditableCollectionView editableView)
        {
            editableView.NewItemPlaceholderPosition =
                NewItemPlaceholderPosition.None;
        }
        PackagesView.Filter = FilterPackage;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenSelectedModCommand = new RelayCommand(OpenSelectedMod);
        AnalyzeSelectedCommand = new AsyncRelayCommand(
            () => AnalyzeRowsAsync(
                SelectedPackage is null ? [] : [SelectedPackage],
                false),
            () => SelectedPackage is not null &&
                !IsAnalyzing &&
                !IsRefreshingNexusRelations);
        ReanalyzeSelectedCommand = new AsyncRelayCommand(
            () => AnalyzeRowsAsync(
                SelectedPackage is null ? [] : [SelectedPackage],
                true),
            () => SelectedPackage is not null &&
                !IsAnalyzing &&
                !IsRefreshingNexusRelations);
        AnalyzeGroupCommand = new AsyncRelayCommand(
            () => AnalyzeRowsAsync(
                SelectedPackage is null
                    ? []
                    : Packages
                        .Where(row => row.Group == SelectedPackage.Group)
                        .ToArray(),
                false),
            () => SelectedPackage is not null &&
                !IsAnalyzing &&
                !IsRefreshingNexusRelations);
        AnalyzeAllCommand = new AsyncRelayCommand(
            () => AnalyzeRowsAsync(
                Packages.Where(row =>
                    row.AnalysisState is
                        PackageAnalysisState.NotAnalyzed or
                        PackageAnalysisState.Stale).ToArray(),
                false),
            () => Packages.Count > 0 &&
                !IsAnalyzing &&
                !IsRefreshingNexusRelations);
        CancelAnalysisCommand = new RelayCommand(
            CancelActiveOperations,
            () => IsAnalyzing || IsRefreshingNexusRelations);
        CheckAllNexusRelationsCommand = new AsyncRelayCommand(
            () => StartNexusRefresh(Packages),
            () => CanStartNexusRefresh(Packages));
        CreateGroupCommand = new AsyncRelayCommand(CreateGroupAsync);
        RenameGroupCommand = new AsyncRelayCommand(
            RenameGroupAsync,
            () => SelectedGroup?.Id is not null);
        DeleteGroupCommand = new AsyncRelayCommand(
            DeleteGroupAsync,
            () => SelectedGroup?.Id is not null);
        MoveGroupUpCommand = new AsyncRelayCommand(
            () => MoveGroupAsync(-1),
            () => SelectedGroup?.Id is not null);
        MoveGroupDownCommand = new AsyncRelayCommand(
            () => MoveGroupAsync(1),
            () => SelectedGroup?.Id is not null);
        InstallSelectedCommand = new AsyncRelayCommand(
            InstallSelectedAsync,
            () => _batchInstall is not null &&
                  !IsBatchBusy &&
                  !IsRefreshingNexusRelations &&
                  SelectedRows.Any(row =>
                      row.LibraryMod.Archives.Any(
                          IsBatchInstallPrimaryCandidate)));
        RemoveSelectedCommand = new AsyncRelayCommand(
            RemoveSelectedAsync,
            () => _batchRemoval is not null &&
                  !IsBatchBusy &&
                  !IsRefreshingNexusRelations &&
                  SelectedRows.Any(row =>
                      row.LibraryMod.InstalledArchives.Count > 0));
        DeleteSelectedCompletelyCommand = new AsyncRelayCommand(
            DeleteSelectedCompletelyAsync,
            () => _archiveDeletion is not null &&
                  !IsBatchBusy &&
                  !IsRefreshingNexusRelations &&
                  !RecoveryRequired &&
                  HasBatchSelection);
        ClearSelectionCommand = new RelayCommand(
            ClearBatchSelection,
            () => HasBatchSelection);
        RefreshChoices();
        _localization.LanguageChanged += Localization_OnLanguageChanged;
    }

    public ObservableCollection<PackageRowViewModel> Packages => _packages;
    public ObservableCollection<LibraryGroupViewModel> Groups => _groups;
    public ICollectionView PackagesView { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenSelectedModCommand { get; }
    public AsyncRelayCommand AnalyzeSelectedCommand { get; }
    public AsyncRelayCommand ReanalyzeSelectedCommand { get; }
    public AsyncRelayCommand AnalyzeGroupCommand { get; }
    public AsyncRelayCommand AnalyzeAllCommand { get; }
    public RelayCommand CancelAnalysisCommand { get; }
    public AsyncRelayCommand CheckAllNexusRelationsCommand { get; }
    public AsyncRelayCommand CreateGroupCommand { get; }
    public AsyncRelayCommand RenameGroupCommand { get; }
    public AsyncRelayCommand DeleteGroupCommand { get; }
    public AsyncRelayCommand MoveGroupUpCommand { get; }
    public AsyncRelayCommand MoveGroupDownCommand { get; }
    public AsyncRelayCommand InstallSelectedCommand { get; }
    public AsyncRelayCommand RemoveSelectedCommand { get; }
    public AsyncRelayCommand DeleteSelectedCompletelyCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public ILibraryWindowService WindowService => _windowService;
    internal LocalizationService Localization => _localization;
    public Action? RequestNavigateDiagnostics { get; set; }

    private async Task<bool> EnsureGameReadyForTargetOperationsAsync()
    {
        var profile = _getGameProfile();
        if (profile is null || string.IsNullOrWhiteSpace(profile.GameRoot))
            return true;

        if (_gameMaintenance is null)
            return true;

        try
        {
            var scan = await _gameMaintenance.ScanAsync(profile.GameRoot).ConfigureAwait(true);
            if (scan.Status is GameMaintenanceScanStatus.Clean or GameMaintenanceScanStatus.ManagedContentPresent)
                return true;

            if (scan.Status == GameMaintenanceScanStatus.ForeignModificationDetected)
            {
                var title = _localization.Get("GameFolderRequiresMaintenanceTitle");
                var message = _localization.Get("GameFolderRequiresMaintenanceMessage");
                var cancelLabel = _localization.Get("Cancel");
                var diagLabel = _localization.Get("SettingsGoToDiagnostics");

                var shouldNavigate = _userDialogs?.Confirm(message, title, cancelLabel, diagLabel) == true;
                if (shouldNavigate)
                {
                    RequestNavigateDiagnostics?.Invoke();
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _exceptionLogger?.Invoke(ex);
            return false;
        }
    }

    internal FomodPreparationBackgroundExecutor
        FomodPreparationBackground
    {
        get => _fomodPreparationBackground;
        set => _fomodPreparationBackground = value ??
            throw new ArgumentNullException(nameof(value));
    }

    public IReadOnlyList<ChoiceItem> FilterChoices { get; private set; } = [];
    public IReadOnlyList<string> FilterDisplayChoices =>
        FilterChoices.Select(choice => choice.Display).ToArray();
    public IReadOnlyList<ChoiceItem> AnalysisFilterChoices { get; private set; } = [];
    public IReadOnlyList<ChoiceItem> InstalledFilterChoices { get; private set; } = [];
    public IReadOnlyList<ChoiceItem> GroupFilterChoices { get; private set; } = [];
    public Action<string>? RequestNavigateNexus { get; set; }

    public PackageRowViewModel? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (value is not null && !Packages.Contains(value))
                value = null;
            if (!SetProperty(ref _selectedPackage, value))
                return;
            AnalyzeSelectedCommand.NotifyCanExecuteChanged();
            ReanalyzeSelectedCommand.NotifyCanExecuteChanged();
            AnalyzeGroupCommand.NotifyCanExecuteChanged();
        }
    }

    public void ObserveSelectedPackage(
        PackageRowViewModel? row)
    {
        if (!SetProperty(ref _selectedPackage, row,
                nameof(SelectedPackage)))
        {
            return;
        }
        AnalyzeSelectedCommand.NotifyCanExecuteChanged();
        ReanalyzeSelectedCommand.NotifyCanExecuteChanged();
        AnalyzeGroupCommand.NotifyCanExecuteChanged();
    }

    public LibraryGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value))
                return;
            NewGroupName = value?.Name ?? string.Empty;
            NotifyGroupCommands();
        }
    }

    public string NewGroupName
    {
        get => _newGroupName;
        set => SetProperty(ref _newGroupName, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ClearBatchSelection();
                PackagesView.Refresh();
            }
        }
    }

    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (SetProperty(ref _selectedFilter, value))
            {
                ClearBatchSelection();
                PackagesView.Refresh();
            }
        }
    }

    public ChoiceItem? SelectedFilterChoice
    {
        get => FilterChoices.FirstOrDefault(choice =>
            choice.Value == SelectedFilter);
        set
        {
            if (value is not null)
                SelectedFilter = value.Value;
        }
    }

    public string SelectedFilterDisplay
    {
        get => FilterChoices.FirstOrDefault(choice =>
            choice.Value == SelectedFilter)?.Display ?? string.Empty;
        set
        {
            var choice = FilterChoices.FirstOrDefault(item =>
                item.Display == value);
            if (choice is not null)
                SelectedFilter = choice.Value;
        }
    }

    public string SelectedAnalysisFilter
    {
        get => _selectedAnalysisFilter;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (SetProperty(ref _selectedAnalysisFilter, value))
            {
                ClearBatchSelection();
                PackagesView.Refresh();
            }
        }
    }

    public ChoiceItem? SelectedAnalysisFilterChoice
    {
        get => AnalysisFilterChoices.FirstOrDefault(choice =>
            choice.Value == SelectedAnalysisFilter);
        set
        {
            if (value is not null)
                SelectedAnalysisFilter = value.Value;
        }
    }

    public string SelectedInstalledFilter
    {
        get => _selectedInstalledFilter;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (SetProperty(ref _selectedInstalledFilter, value))
            {
                ClearBatchSelection();
                PackagesView.Refresh();
            }
        }
    }

    public ChoiceItem? SelectedInstalledFilterChoice
    {
        get => InstalledFilterChoices.FirstOrDefault(choice =>
            choice.Value == SelectedInstalledFilter);
        set
        {
            if (value is not null)
                SelectedInstalledFilter = value.Value;
        }
    }

    public string SelectedGroupFilter
    {
        get => _selectedGroupFilter;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (SetProperty(ref _selectedGroupFilter, value))
            {
                ClearBatchSelection();
                PackagesView.Refresh();
                if (!_loadingCachedState)
                {
                    _uiStatePersistence.TrySchedule(() =>
                        PersistSelectedGroupFilterAsync(value));
                }
            }
        }
    }

    public ChoiceItem? SelectedGroupFilterChoice
    {
        get => GroupFilterChoices.FirstOrDefault(choice =>
            choice.Value == SelectedGroupFilter);
        set
        {
            if (value is not null)
            {
                SelectedGroupFilter = value.Value;
                SelectedGroup = long.TryParse(
                        value.Value,
                        CultureInfo.InvariantCulture,
                        out var groupId)
                    ? Groups.FirstOrDefault(group => group.Id == groupId)
                    : null;
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (!SetProperty(ref _isAnalyzing, value))
                return;
            OnPropertyChanged(nameof(AnalysisProgressText));
            AnalyzeSelectedCommand.NotifyCanExecuteChanged();
            ReanalyzeSelectedCommand.NotifyCanExecuteChanged();
            AnalyzeGroupCommand.NotifyCanExecuteChanged();
            AnalyzeAllCommand.NotifyCanExecuteChanged();
            CancelAnalysisCommand.NotifyCanExecuteChanged();
            CheckAllNexusRelationsCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsBatchBusy
    {
        get => _isBatchBusy;
        private set
        {
            if (!SetProperty(ref _isBatchBusy, value))
                return;
            InstallSelectedCommand.NotifyCanExecuteChanged();
            RemoveSelectedCommand.NotifyCanExecuteChanged();
            DeleteSelectedCompletelyCommand.NotifyCanExecuteChanged();
            CheckAllNexusRelationsCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsRefreshingNexusRelations
    {
        get => _isRefreshingNexusRelations;
        private set
        {
            if (!SetProperty(ref _isRefreshingNexusRelations, value))
                return;
            AnalyzeSelectedCommand.NotifyCanExecuteChanged();
            ReanalyzeSelectedCommand.NotifyCanExecuteChanged();
            AnalyzeGroupCommand.NotifyCanExecuteChanged();
            AnalyzeAllCommand.NotifyCanExecuteChanged();
            CancelAnalysisCommand.NotifyCanExecuteChanged();
            CheckAllNexusRelationsCommand.NotifyCanExecuteChanged();
            InstallSelectedCommand.NotifyCanExecuteChanged();
            RemoveSelectedCommand.NotifyCanExecuteChanged();
            DeleteSelectedCompletelyCommand.NotifyCanExecuteChanged();
        }
    }

    public string AnalysisProgressText => IsAnalyzing
        ? string.Format(
            _localization.Get("AnalysisProgress"),
            _analysisCompleted,
            _analysisTotal)
        : string.Empty;

    public string Header => _localization.Get("Library");
    public string Subtitle => _localization.Get("LibrarySubtitle");
    public string SearchPlaceholder => _localization.Get("SearchPlaceholder");
    public string PackageCount => string.Format(
        _localization.Get("PackageCount"),
        _packageCount);
    public string FoundPackagesSummary => string.Format(
        _localization.Get("LibraryModsAndArchivesCount"),
        _packageCount,
        _archiveCount);
    public string SkippedFoldersSummary => string.Format(
        _localization.Get("SkippedFolders"),
        _skippedDirectoryCount);
    public string LastUpdatedSummary => string.Format(
        _localization.Get("LastUpdated"),
        _lastUpdated?.ToString(
            "g",
            CultureInfo.GetCultureInfo(_localization.CurrentLanguage))
        ?? _localization.Get("NotUpdatedYet"));
    public string RefreshLabel => _localization.Get("Refresh");
    public string FilterAllDisplay => _localization.Get("FilterAll");
    public string FilterNexusDisplay => _localization.Get("FilterNexus");
    public string FilterLocalDisplay => _localization.Get("FilterLocal");
    public string FilterWithMetadataDisplay =>
        _localization.Get("FilterWithMetadata");
    public string FilterWithoutMetadataDisplay =>
        _localization.Get("FilterWithoutMetadata");
    public string AnalysisAllDisplay => _localization.Get("AnalysisAll");
    public string AnalysisNotAnalyzedDisplay =>
        _localization.Get("AnalysisNotAnalyzed");
    public string AnalysisReadyDisplay =>
        _localization.Get("AnalysisReady");
    public string AnalysisRequiresSelectionDisplay =>
        _localization.Get("AnalysisRequiresSelection");
    public string AnalysisProblemsDisplay =>
        _localization.Get("AnalysisProblems");
    public string ColumnName => _localization.Get("ColumnName");
    public string ColumnSource => _localization.Get("ColumnSource");
    public string ColumnVersion => _localization.Get("ColumnVersion");
    public string ColumnFormat => _localization.Get("ColumnFormat");
    public string ColumnSize => _localization.Get("ColumnSize");
    public string ColumnPath => _localization.Get("ColumnPath");
    public string ColumnInstalled => _localization.Get("ColumnInstalled");
    public string CheckInstallLabel => _localization.Get("CheckInstallation");
    public string PackageCountValue =>
        _packageCount.ToString(CultureInfo.InvariantCulture);
    public string SkippedDirectoryCountValue =>
        _skippedDirectoryCount.ToString(CultureInfo.InvariantCulture);
    public string LastUpdatedValue => _lastUpdated?.ToString(
        "g",
        CultureInfo.GetCultureInfo(_localization.CurrentLanguage)) ?? "—";
    public string AnalyzeSelectedLabel => _localization.Get("AnalyzeSelected");
    public string ReanalyzeLabel => _localization.Get("Reanalyze");
    public string AnalyzeGroupLabel => _localization.Get("AnalyzeGroup");
    public string AnalyzeAllLabel => _localization.Get("AnalyzeAll");
    public string CheckNexusRelationsLabel =>
        _localization.Get("CheckNexusRelations");
    public string CancelLabel => _localization.Get("Cancel");
    public string PackageDetailsLabel => _localization.Get("PackageDetails");
    public string DisplayNameLabel => _localization.Get("DisplayName");
    public string VersionLabel => _localization.Get("ColumnVersion");
    public string GroupLabel => _localization.Get("Group");
    public string NoteLabel => _localization.Get("Note");
    public string AnalysisLabel => _localization.Get("Analysis");
    public string PathLabel => _localization.Get("ColumnPath");
    public string SourceLabel => _localization.Get("ColumnSource");
    public string AuthorLabel => _localization.Get("Author");
    public string CategoryLabel => _localization.Get("Category");
    public string NexusModIdLabel => _localization.Get("NexusModId");
    public string NexusFileIdLabel => _localization.Get("NexusFileId");
    public string NexusUrlLabel => _localization.Get("NexusUrl");
    public string SizeLabel => _localization.Get("ColumnSize");
    public string FormatLabel => _localization.Get("ColumnFormat");
    public string ModifiedLabel => _localization.Get("Modified");
    public string AnalysisResultLabel => _localization.Get("AnalysisResult");
    public string SaveLabel => _localization.Get("Save");
    public string OpenFolderLabel => _localization.Get("OpenFolder");
    public string OpenNexusLabel => _localization.Get("OpenNexus");
    public string CreateGroupLabel => _localization.Get("CreateGroup");
    public string RenameGroupLabel => _localization.Get("RenameGroup");
    public string DeleteGroupLabel => _localization.Get("DeleteGroup");
    public string MoveUpLabel => _localization.Get("MoveUp");
    public string MoveDownLabel => _localization.Get("MoveDown");
    public string NoPackageSelectedLabel => _localization.Get("NoPackageSelected");
    public string CloseLabel => _localization.Get("Close");
    public string RefreshToolTip => _localization.Get("RefreshToolTip");
    public string AddGroupLabel => _localization.Get("AddGroup");
    public string SelectedCountLabel => string.Format(
        _localization.Get("BatchSelectedCount"),
        SelectedCount);
    public string InstallSelectedLabel =>
        _localization.Get("BatchInstallSelected");
    public string RemoveSelectedLabel =>
        _localization.Get("BatchRemoveSelected");
    public string DeleteSelectedCompletelyLabel => string.Format(
        _localization.Get("BatchDeleteSelectedCompletely"),
        SelectedCount);
    public string ClearSelectionLabel =>
        _localization.Get("BatchClearSelection");
    public string InstallSelectedContextLabel => string.Format(
        _localization.Get("BatchInstallSelectedContext"),
        SelectedCount);
    public string RemoveSelectedContextLabel => string.Format(
        _localization.Get("BatchRemoveSelectedContext"),
        SelectedCount);
    public string DeleteSelectedCompletelyContextLabel => string.Format(
        _localization.Get("BatchDeleteSelectedCompletelyContext"),
        SelectedCount);
    public IReadOnlyList<PackageRowViewModel> SelectedRows =>
        Packages.Where(row => row.IsBatchSelected).ToArray();
    public int SelectedCount => SelectedRows.Count;
    public bool HasBatchSelection => SelectedCount > 0;
    public bool? AllVisibleSelected
    {
        get
        {
            var visible = PackagesView
                .Cast<PackageRowViewModel>()
                .ToArray();
            if (visible.Length == 0 ||
                visible.All(row => !row.IsBatchSelected))
            {
                return false;
            }
            return visible.All(row => row.IsBatchSelected)
                ? true
                : null;
        }
    }

    public event EventHandler? BatchSelectionChanged;
    public bool RecoveryRequired
    {
        get => _recoveryRequired;
        private set => SetProperty(ref _recoveryRequired, value);
    }

    public void SetRecoveryRequired(bool value) =>
        RecoveryRequired = value;

    public void ToggleCheckbox(PackageRowViewModel? row)
    {
        if (row is null || !Packages.Contains(row))
            return;
        row.SetBatchSelected(!row.IsBatchSelected);
        NotifyBatchSelectionChanged();
    }

    public void ToggleAllVisibleSelection()
    {
        var visible = PackagesView.Cast<PackageRowViewModel>().ToArray();
        var select = visible.Any(row => !row.IsBatchSelected);
        foreach (var row in visible)
            row.SetBatchSelected(select);
        NotifyBatchSelectionChanged();
    }

    public bool? GetGroupBatchSelectionState(
        LibraryGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var visible = PackagesView
            .Cast<PackageRowViewModel>()
            .Where(row => ReferenceEquals(row.Group, group))
            .ToArray();
        if (visible.Length == 0 ||
            visible.All(row => !row.IsBatchSelected))
        {
            return false;
        }
        return visible.All(row => row.IsBatchSelected)
            ? true
            : null;
    }

    public void ToggleGroupSelection(LibraryGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var visible = PackagesView
            .Cast<PackageRowViewModel>()
            .Where(row => ReferenceEquals(row.Group, group))
            .ToArray();
        if (visible.Length == 0)
            return;
        var select = visible.Any(row => !row.IsBatchSelected);
        foreach (var row in visible)
            row.SetBatchSelected(select);
        NotifyBatchSelectionChanged();
    }

    public void ClearBatchSelection()
    {
        if (!Packages.Any(row => row.IsBatchSelected))
            return;
        SetBatchSelection([]);
        NotifyBatchSelectionChanged();
    }

    private void SetBatchSelection(
        IReadOnlyCollection<PackageRowViewModel> rows)
    {
        var selected = rows.ToHashSet();
        foreach (var row in Packages)
            row.SetBatchSelected(selected.Contains(row));
    }

    private void NotifyBatchSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasBatchSelection));
        OnPropertyChanged(nameof(AllVisibleSelected));
        OnPropertyChanged(nameof(SelectedCountLabel));
        OnPropertyChanged(nameof(InstallSelectedContextLabel));
        OnPropertyChanged(nameof(RemoveSelectedContextLabel));
        OnPropertyChanged(nameof(DeleteSelectedCompletelyLabel));
        OnPropertyChanged(nameof(DeleteSelectedCompletelyContextLabel));
        foreach (var group in Groups)
            group.RefreshBatchSelectionState();
        InstallSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCompletelyCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        BatchSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadCachedAsync()
    {
        _loadingCachedState = true;
        try
        {
            await ReloadFromDatabaseAsync();
            _packageCount = Packages.Count;
            _archiveCount = Packages.Sum(row => row.ArchiveCount);
            _lastUpdated = (await _repository.GetLastIndexedAtUtcAsync())
                ?.ToLocalTime();
            NotifySummaries();
            _recordSessionEvent?.Invoke(
                "EventLibraryLoaded",
                string.Format(
                    _localization.Get("EventModsCount"),
                    _packageCount));
            LibrarySelectionDiagnostics.Record(
                "LibraryLoaded",
                detail: $"database rows={_packageCount}",
                verifyInvariants: true);
        }
        finally
        {
            _loadingCachedState = false;
        }
    }

    public Task RefreshFromHostAsync()
    {
        if (_uiDispatcher.CheckAccess())
        {
            return RefreshAsync();
        }

        return _uiDispatcher.InvokeAsync(RefreshAsync).Task.Unwrap();
    }

    public async Task RefreshAsync()
    {
        LibrarySelectionDiagnostics.Record(
            "LibraryRefreshStarted",
            detail: "manual index refresh",
            verifyInvariants: true);
        var root = _getSettings().LibraryRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusMessage = _localization.Get("LibraryNotSelected");
            LibrarySelectionDiagnostics.Record(
                "LibraryRefreshCompleted",
                detail: "manual index refresh skipped: root is empty",
                verifyInvariants: true);
            return;
        }
        if (!Directory.Exists(root))
        {
            StatusMessage = _localization.Get("LibraryMissing");
            LibrarySelectionDiagnostics.Record(
                "LibraryRefreshCompleted",
                detail: "manual index refresh skipped: root is missing",
                verifyInvariants: true);
            return;
        }

        StatusMessage = _localization.Get("Indexing");
        try
        {
            var result = await _libraryIndex.IndexAsync(root);
            var reconciliation = await _repository.SynchronizePackagesAsync(
                result.Packages);
            if (_archiveFamilyReconciliation is not null)
                await _archiveFamilyReconciliation.ReconcileAsync();
            await ReloadFromDatabaseAsync();
            _packageCount = Packages.Count;
            _archiveCount = Packages.Sum(row => row.ArchiveCount);
            _skippedDirectoryCount = result.SkippedDirectoryCount;
            _lastUpdated = (await _repository.GetLastIndexedAtUtcAsync())
                ?.ToLocalTime();
            NotifySummaries();
            StatusMessage = string.Format(
                _localization.Get("LibraryRefreshSummary"),
                reconciliation.FoundArchiveCount,
                reconciliation.AddedCount,
                reconciliation.UpdatedCount,
                reconciliation.RemovedMissingCount,
                reconciliation.MergedDuplicateCount);
            _recordSessionEvent?.Invoke(
                "EventLibraryUpdated",
                StatusMessage);
            LibrarySelectionDiagnostics.Record(
                "LibraryRefreshCompleted",
                detail:
                    $"manual index refresh; packages={_packageCount}; " +
                    $"archives={_archiveCount}",
                verifyInvariants: true);
        }
        catch (Exception exception)
        {
            StatusMessage = string.Format(
                _localization.Get("IndexFailed"),
                exception.Message);
            LibrarySelectionDiagnostics.Record(
                "LibraryRefreshCompleted",
                detail:
                    "manual index refresh failed: " +
                    exception.GetType().Name,
                verifyInvariants: true);
        }
    }

    private async Task ReloadFromDatabaseAsync(
        PackageId? retainSelection = null,
        bool loadDataInBackground = false)
    {
        LibrarySelectionDiagnostics.Record(
            "LibraryRefreshStarted",
            detail:
                "database reload; retainPackageId=" +
                (retainSelection?.Value ?? "<null>"),
            verifyInvariants: true);
        await _reloadGate.WaitAsync();
        try
        {
            var batchSelection = Packages
                .Where(row => row.IsBatchSelected)
                .Select(row => row.LibraryModId)
                .ToHashSet();
            var reloadData = loadDataInBackground
                ? await _fomodPreparationBackground.RunTaskAsync(
                    LoadLibraryReloadDataAsync)
                : await LoadLibraryReloadDataAsync();
            var groups = reloadData.Groups;
            var newGroups = new List<LibraryGroupViewModel>();
            newGroups.Add(new LibraryGroupViewModel(
                null,
                _localization.Get("Ungrouped"),
                -1,
                true,
                _localization,
                PersistExpanded,
                CreateGroupFromMenuAsync,
                RenameGroupFromMenuAsync,
                AnalyzeGroupFromMenuAsync,
                DeleteGroupFromMenuAsync,
                group => MoveGroupFromMenuAsync(group, -1),
                group => MoveGroupFromMenuAsync(group, 1),
                GetGroupBatchSelectionState,
                ToggleGroupSelection));
            foreach (var group in groups)
            {
                newGroups.Add(new LibraryGroupViewModel(
                    group.Id,
                    group.Name,
                    group.SortOrder,
                    group.IsExpanded,
                    _localization,
                    PersistExpanded,
                    CreateGroupFromMenuAsync,
                    RenameGroupFromMenuAsync,
                    AnalyzeGroupFromMenuAsync,
                    DeleteGroupFromMenuAsync,
                    item => MoveGroupFromMenuAsync(item, -1),
                    item => MoveGroupFromMenuAsync(item, 1),
                    GetGroupBatchSelectionState,
                    ToggleGroupSelection));
            }

            var groupsById = newGroups.ToDictionary(g => g.Id ?? -1L);
            var preparedPackages =
                new List<PreparedPackageRow>(reloadData.Mods.Count);
            foreach (var mod in reloadData.Mods)
            {
                try
                {
                    if (mod.Archives is null ||
                        mod.Archives.Any(archive =>
                            archive is null ||
                            archive.Package is null))
                    {
                        throw new InvalidDataException(
                            "Library mod contains an invalid archive.");
                    }
                    var record = mod.PreferredArchive;
                    if (record is null)
                    {
                        throw new InvalidDataException(
                            "Library mod has no archive.");
                    }
                    var group = groupsById.TryGetValue(mod.GroupId ?? -1L, out var g2) ? g2 : newGroups[0];
                    preparedPackages.Add(new(
                        record,
                        mod,
                        group,
                        reloadData.MissingDependencies.TryGetValue(
                            record.Package.PackageId,
                            out var missing)
                            ? missing
                            : []));
                }
                catch (Exception exception)
                {
                    _exceptionLogger?.Invoke(new InvalidDataException(
                        $"Library row '{mod.LibraryModId.Value}' " +
                        "was skipped because its data is incomplete.",
                        exception));
                }
            }

            var restoredGroupId = groups
                .FirstOrDefault(group => group.IsSelected)?.Id;
            await InvokeOnUiAsync(() =>
            {
                var existingById = Packages.ToDictionary(
                    row => row.LibraryModId);
                var desiredPackages =
                    new List<PackageRowViewModel>(
                        preparedPackages.Count);
                foreach (var prepared in preparedPackages)
                {
                    if (existingById.TryGetValue(
                            prepared.Mod.LibraryModId,
                            out var existing))
                    {
                        existing.RefreshFrom(
                            prepared.Record,
                            prepared.Mod,
                            prepared.Group,
                            newGroups,
                            prepared.MissingDependencies);
                        desiredPackages.Add(existing);
                    }
                    else
                    {
                        desiredPackages.Add(CreatePackageRow(
                            prepared,
                            newGroups));
                    }
                }
                _groups.ReplaceAll(newGroups);
                SynchronizePackages(desiredPackages);
                PackagesView.Refresh();
                foreach (var row in desiredPackages)
                {
                    row.SetBatchSelected(batchSelection.Contains(
                        row.LibraryModId));
                }
                if (retainSelection is { } retainedPackageId)
                {
                    var retainedPackage = desiredPackages.FirstOrDefault(row =>
                        row.Archives.Any(archive =>
                            archive.Package.PackageId == retainedPackageId));
                    ObserveSelectedPackage(retainedPackage);
                }
                else if (_selectedPackage is not null &&
                         !desiredPackages.Contains(_selectedPackage))
                {
                    ObserveSelectedPackage(null);
                }
                if (restoredGroupId is not null)
                {
                    _selectedGroupFilter = restoredGroupId.Value.ToString(
                        CultureInfo.InvariantCulture);
                    OnPropertyChanged(nameof(SelectedGroupFilter));
                    OnPropertyChanged(nameof(SelectedGroupFilterChoice));
                }
                else if (long.TryParse(
                             _selectedGroupFilter,
                             CultureInfo.InvariantCulture,
                             out var filteredGroupId) &&
                         newGroups.All(group => group.Id != filteredGroupId))
                {
                    _selectedGroupFilter = AllFilter;
                    OnPropertyChanged(nameof(SelectedGroupFilter));
                    OnPropertyChanged(nameof(SelectedGroupFilterChoice));
                }
                SelectedGroup = newGroups.FirstOrDefault(group =>
                    group.Id is not null &&
                    group.Id.Value.ToString(CultureInfo.InvariantCulture) ==
                    _selectedGroupFilter);
                RefreshGroupChoices();
                NotifyBatchSelectionChanged();
                AnalyzeAllCommand.NotifyCanExecuteChanged();
                CheckAllNexusRelationsCommand.NotifyCanExecuteChanged();
            });
        }
        finally
        {
            _reloadGate.Release();
            LibrarySelectionDiagnostics.Record(
                "LibraryRefreshCompleted",
                detail:
                    "database reload; retainPackageId=" +
                    (retainSelection?.Value ?? "<null>"),
                verifyInvariants: true);
        }
    }

    private async Task<LibraryReloadData> LoadLibraryReloadDataAsync()
    {
        var groups = await _repository.LoadGroupsAsync()
            .ConfigureAwait(false);
        var sortOrders = groups.ToDictionary(
            group => group.Id,
            group => group.SortOrder);
        var mods = (await _repository.LoadLibraryModsAsync()
                .ConfigureAwait(false))
            .OrderBy(mod => sortOrders.TryGetValue(
                    mod.GroupId ?? -1L,
                    out var order)
                ? order
                : -1)
            .ToArray();
        var missingDependencies = await _relations
            .LoadMissingDependencyNamesAsync()
            .ConfigureAwait(false);
        return new(groups, mods, missingDependencies);
    }

    private PackageRowViewModel CreatePackageRow(
        PreparedPackageRow prepared,
        IReadOnlyList<LibraryGroupViewModel> availableGroups)
    {
        var row = new PackageRowViewModel(
            prepared.Record,
            prepared.Group,
            availableGroups,
            _localization,
            SavePackageAsync,
            item => AnalyzeRowsAsync([item], false),
            item => AnalyzeRowsAsync([item], true),
            SelectRootAsync,
            QueueOpenModDetails,
            AssignGroupAsync,
            BuildInstallPlanAsync,
            InstallModAsync,
            _installationService is not null,
            RemoveModAsync,
            _removalService is not null,
            DeleteMissingLibraryRecordAsync,
            prepared.MissingDependencies,
            prepared.Mod,
            DeleteLibraryModFullyAsync,
            _archiveDeletion is not null,
            CheckNexusRelationsFromRowAsync,
            CanCheckNexusRelationsFromRow,
            _getSettings,
            url => RequestNavigateNexus?.Invoke(url));
        return row;
    }

    private void SynchronizePackages(
        IReadOnlyList<PackageRowViewModel> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var row = desired[index];
            if (index < Packages.Count &&
                ReferenceEquals(Packages[index], row))
            {
                continue;
            }
            var existingIndex = Packages.IndexOf(row);
            if (existingIndex >= 0)
                Packages.Move(existingIndex, index);
            else
                Packages.Insert(index, row);
        }
        while (Packages.Count > desired.Count)
            Packages.RemoveAt(Packages.Count - 1);
    }

    private Task InvokeOnUiAsync(Action action)
    {
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return _uiDispatcher.InvokeAsync(action).Task;
    }

    private async Task DeleteMissingLibraryRecordAsync(
        PackageRowViewModel row)
    {
        if (!row.CanDeleteLibraryRecord || _userDialogs is null)
            return;
        var confirmed = _userDialogs.Confirm(
            string.Format(
                _localization.Get("DeleteLibraryRecordQuestion"),
                row.DisplayName),
            _localization.Get("DeleteLibraryRecordTitle"),
            _localization.Get("Cancel"),
            _localization.Get("DeleteLibraryRecordConfirm"));
        if (!confirmed)
            return;

        var deleted = await _repository.DeleteMissingUninstalledPackageAsync(
            row.Record.PackageId);
        if (!deleted)
            return;
        await ReloadFromDatabaseAsync();
        _packageCount = Packages.Count;
        NotifySummaries();
        StatusMessage = _localization.Get("LibraryRecordDeleted");
    }

    private async Task DeleteLibraryModFullyAsync(
        PackageRowViewModel row)
    {
        if (_archiveDeletion is null || _userDialogs is null)
            return;
        var installed = row.LibraryMod.InstalledArchives.Count > 0;
        var message = string.Format(
            _localization.Get(installed
                ? "LibraryModFullDeleteInstalledQuestion"
                : "LibraryModFullDeleteQuestion"),
            row.DisplayName,
            row.ArchiveCount);
        var confirmed = _userDialogs.Confirm(
            message,
            _localization.Get("LibraryModFullDeleteTitle"),
            _localization.Get("Cancel"),
            _localization.Get(installed
                ? "LibraryModFullDeleteInstalledConfirm"
                : "LibraryModFullDeleteConfirm"));
        if (!confirmed)
            return;

        var libraryModId = row.LibraryModId;
        var gameProfile = _getGameProfile();
        var libraryRoot = _getSettings().LibraryRoot;
        if (installed && _removalService is not null)
        {
            var installedArchives = row.LibraryMod.InstalledArchives;
            var plans = new List<ModRemovalPlan>();
            foreach (var archive in installedArchives)
            {
                plans.Add(await _removalService.BuildPreflightPlanAsync(
                    archive,
                    gameProfile,
                    libraryRoot));
            }
            var blocked = plans.FirstOrDefault(p => !p.CanRemove);
            if (blocked is not null)
            {
                _userDialogs.ShowError(LocalizeRemovalError(
                    blocked.ErrorCode,
                    blocked.ProblemPaths));
                return;
            }
        }

        LibraryModFullDeletionResult? result = null;
        try
        {
            await _windowService.ShowFullDeleteProgressAsync(
                row.DisplayName,
                async () =>
                {
                    result = await RunFullDeleteInBackgroundAsync(
                        libraryModId,
                        gameProfile,
                        libraryRoot);
                },
                _localization);
        }
        catch (Exception exception)
        {
            result = new LibraryModFullDeletionResult
            {
                Status = LibraryModFullDeletionStatus.RemovalFailed,
                ErrorMessage = exception.Message
            };
        }

        if (result is null || !result.Success)
        {
            _userDialogs.ShowError(LocalizeFullDeleteError(result));
            return;
        }
        await ReloadFromDatabaseAsync();
        _packageCount = Packages.Count;
        _archiveCount = Packages.Sum(item => item.ArchiveCount);
        NotifySummaries();
        StatusMessage = _localization.Get(
            "LibraryModFullDeleteCompleted");
    }

    private Task<LibraryModFullDeletionResult> RunFullDeleteInBackgroundAsync(
        LibraryModId libraryModId,
        GameProfileRecord? profile,
        string libraryRoot,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null) =>
        _fomodPreparationBackground.RunTaskAsync(() =>
            _archiveDeletion!.DeleteModAsync(
                libraryModId,
                profile,
                libraryRoot,
                authorizedModifiedFiles: authorizedModifiedFiles));

    private async Task BuildInstallPlanAsync(PackageRowViewModel row)
    {
        try
        {
            var plan = await _installPlan.BuildAsync(
                row.Stored,
                _getGameProfile());
            _recordSessionEvent?.Invoke(
                "EventInstallPlanBuilt",
                row.DisplayName);
            _windowService.ShowInstallPlan(plan, _localization);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    internal async Task<ModDetailsData> LoadModDetailsDataAsync(
        LibraryModId libraryModId)
    {
        var row = Packages.FirstOrDefault(item =>
            item.LibraryModId == libraryModId) ??
            throw new InvalidOperationException(
                "The mod is no longer available in the library.");
        var stored = row.Stored;
        var profile = _getGameProfile();
        var installedArchive = _archiveSwitch is null
            ? null
            : FindSwitchSource(row.LibraryMod, stored);
        var installedPackageIds = row.LibraryMod.InstalledArchives
            .Select(archive => archive.Package.PackageId)
            .ToArray();
        var packageId = row.Record.PackageId;
        var loaded = await LoadModDetailsDataInBackgroundAsync(
            stored,
            profile,
            installedArchive,
            installedPackageIds,
            packageId);
        var nexus = await LoadNexusModDetailsDataAsync(row.LibraryMod);
        return new ModDetailsData(
            row,
            loaded.Plan,
            loaded.SwitchPlan,
            loaded.InstalledFiles,
            loaded.Relations,
            nexus);
    }

    private async Task<NexusModDetailsData> LoadNexusModDetailsDataAsync(
        LibraryModRecord mod)
    {
        if (_nexusRelations is null ||
            !NexusRequirementRefreshEligibility.TryCreate(
                mod,
                out var identity))
        {
            return new(false, null, false);
        }

        try
        {
            return new(
                true,
                await _nexusRelations.LoadRelationsAsync(identity),
                false);
        }
        catch (Exception)
        {
            return new(true, null, true);
        }
    }

    private Task<(InstallPlan Plan, ModArchiveSwitchPlan? SwitchPlan,
        IReadOnlyList<InstalledFileRecord> InstalledFiles,
        IReadOnlyList<PackageRelationView> Relations)>
        LoadModDetailsDataInBackgroundAsync(
            OrganizerPackageRecord stored,
            GameProfileRecord? profile,
            OrganizerPackageRecord? installedArchive,
            IReadOnlyList<PackageId> installedPackageIds,
            PackageId packageId) =>
        _fomodPreparationBackground.RunTaskAsync(async () =>
        {
            var plan = await _installPlan.BuildAsync(stored, profile)
                .ConfigureAwait(false);
            ModArchiveSwitchPlan? switchPlan = null;
            if (_archiveSwitch is not null && installedArchive is not null)
            {
                switchPlan = await _archiveSwitch.BuildPlanAsync(
                        installedArchive,
                        stored)
                    .ConfigureAwait(false);
            }
            var installedFiles = new List<InstalledFileRecord>();
            foreach (var installedPackageId in installedPackageIds)
            {
                installedFiles.AddRange(
                    await _repository.LoadInstalledFilesAsync(
                            installedPackageId)
                        .ConfigureAwait(false));
            }
            var relations = await _relations.LoadForPackageAsync(packageId)
                .ConfigureAwait(false);
            return (
                plan,
                switchPlan,
                (IReadOnlyList<InstalledFileRecord>)installedFiles,
                relations);
        });

    internal async Task SelectArchiveAsync(
        LibraryModId libraryModId,
        PackageId archiveId)
    {
        await _repository.SetPreferredArchiveAsync(
            libraryModId,
            archiveId);
        await ReloadFromDatabaseAsync(archiveId);
    }

    internal async Task ExecuteArchivePrimaryAsync(
        LibraryModId libraryModId,
        PackageId archiveId)
    {
        if (!await EnsureGameReadyForTargetOperationsAsync())
            return;

        var row = Packages.FirstOrDefault(item =>
            item.LibraryModId == libraryModId);
        var target = row?.LibraryMod.Archives.FirstOrDefault(archive =>
            archive.Package.PackageId == archiveId);
        if (row is null || target is null)
        {
            if (row is not null && target is null)
            {
                StatusMessage = _localization.Get("PlanErrorArchiveMissing");
            }
            return;
        }
        await _repository.SetPreferredArchiveAsync(
            libraryModId,
            archiveId);

        var installedInFamily = FindSwitchSource(
            row.LibraryMod,
            target);
        if (installedInFamily is null || _archiveSwitch is null)
        {
            await ReloadFromDatabaseAsync(archiveId);
            var selected = Packages.FirstOrDefault(item =>
                item.LibraryModId == libraryModId);
            if (selected is null)
            {
                StatusMessage = _localization.Get("PlanErrorArchiveMissing");
                return;
            }
            await InstallModInternalAsync(selected, archiveId);
            return;
        }

        var readyTarget = await EnsureFomodReadyAsync(target);
        if (readyTarget is null)
            return;
        target = readyTarget;

        var plan = await _archiveSwitch.BuildPlanAsync(
            installedInFamily,
            target);
        if (!plan.CanSwitch)
        {
            var isSamePackageReinstall =
                installedInFamily.Package.PackageId == target.Package.PackageId;
            if (isSamePackageReinstall && plan.HasEligibleModifiedFiles)
            {
                var confirmedModified = _windowService.ConfirmReinstallWithModifiedFiles(
                    row.DisplayName,
                    plan.EligibleModifiedPaths,
                    _localization);
                if (!confirmedModified)
                    return;

                var authorizedModifiedFiles = ModifiedFilesAuthorization.ForPackage(
                    installedInFamily.Package.PackageId,
                    plan.EligibleModifiedPaths);

                var authorizedPlan = await _archiveSwitch.BuildPlanAsync(
                    installedInFamily,
                    target,
                    authorizedModifiedFiles);
                if (!authorizedPlan.CanSwitch)
                {
                    var authorizedErrorKey = $"PlanError{authorizedPlan.ErrorCode}";
                    var authorizedLocalizedError = _localization.Get(authorizedErrorKey);
                    if (string.Equals(authorizedLocalizedError, authorizedErrorKey, StringComparison.Ordinal))
                    {
                        var removalKey = $"RemovalResult{authorizedPlan.ErrorCode}";
                        authorizedLocalizedError = _localization.Get(removalKey);
                        if (string.Equals(authorizedLocalizedError, removalKey, StringComparison.Ordinal))
                            authorizedLocalizedError = _localization.Get("ArchiveSwitchFailed");
                    }
                    if (authorizedPlan.IneligibleProblemPaths.Count > 0 || authorizedPlan.ProblemPaths.Count > 0)
                    {
                        var problems = authorizedPlan.IneligibleProblemPaths.Count > 0
                            ? authorizedPlan.IneligibleProblemPaths
                            : authorizedPlan.ProblemPaths;
                        authorizedLocalizedError += Environment.NewLine + Environment.NewLine +
                            string.Join(Environment.NewLine, problems);
                    }
                    _userDialogs?.ShowError(authorizedLocalizedError);
                    return;
                }

                var switchResult = await _archiveSwitch.SwitchAsync(
                    installedInFamily,
                    target,
                    _getGameProfile(),
                    _getSettings().LibraryRoot,
                    authorizedModifiedFiles: authorizedModifiedFiles);
                if (!switchResult.Success)
                {
                    if (switchResult.RecoveryRequired)
                        RecoveryRequired = true;
                    _userDialogs?.ShowError(_localization.Get(
                        switchResult.RecoveryRequired
                            ? "ArchiveSwitchRecoveryRequired"
                            : "ArchiveSwitchFailed"));
                }
                await ReloadFromDatabaseAsync(archiveId);
                return;
            }

            var errorKey = $"PlanError{plan.ErrorCode}";
            var localizedError = _localization.Get(errorKey);
            if (string.Equals(localizedError, errorKey, StringComparison.Ordinal))
            {
                var removalKey = $"RemovalResult{plan.ErrorCode}";
                localizedError = _localization.Get(removalKey);
                if (string.Equals(localizedError, removalKey, StringComparison.Ordinal))
                    localizedError = _localization.Get("ArchiveSwitchFailed");
            }
            if (plan.IneligibleProblemPaths.Count > 0 || plan.ProblemPaths.Count > 0)
            {
                var problems = plan.IneligibleProblemPaths.Count > 0
                    ? plan.IneligibleProblemPaths
                    : plan.ProblemPaths;
                localizedError += Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, problems);
            }
            _userDialogs?.ShowError(localizedError);
            return;
        }
        var confirmation = _windowService.ConfirmArchiveSwitch(
            row.DisplayName,
            installedInFamily.EffectiveVersion,
            target.EffectiveVersion,
            plan,
            _localization);
        if (confirmation is null)
            return;
        var result = await _archiveSwitch.SwitchAsync(
            installedInFamily,
            target,
            _getGameProfile(),
            _getSettings().LibraryRoot);
        if (!result.Success)
        {
            if (result.RecoveryRequired)
                RecoveryRequired = true;
            _userDialogs?.ShowError(_localization.Get(
                result.RecoveryRequired
                    ? "ArchiveSwitchRecoveryRequired"
                    : "ArchiveSwitchFailed"));
        }
        else if (confirmation.DeleteOldArchive &&
                 installedInFamily.Package.PackageId != archiveId &&
                 _archiveDeletion is not null)
        {
            var deletion = await _archiveDeletion.DeleteAsync(
                installedInFamily.Package.PackageId,
                removeInstalled: false,
                _getGameProfile(),
                _getSettings().LibraryRoot);
            if (!deletion.Success)
            {
                _userDialogs?.ShowError(
                    _localization.Get("ArchiveDeleteFailed"));
            }
        }
        await ReloadFromDatabaseAsync(archiveId);
    }

    internal async Task AnalyzeArchiveAsync(
        LibraryModId libraryModId,
        PackageId archiveId,
        bool force)
    {
        await SelectArchiveAsync(libraryModId, archiveId);
        var row = Packages.First(item =>
            item.LibraryModId == libraryModId);
        await AnalyzeRowsAsync([row], force);
    }

    internal async Task DeleteArchiveAsync(
        LibraryModId libraryModId,
        PackageId archiveId)
    {
        if (_archiveDeletion is null)
            return;
        var row = Packages.FirstOrDefault(item =>
            item.LibraryModId == libraryModId);
        var archive = row?.LibraryMod.Archives.FirstOrDefault(item =>
            item.Package.PackageId == archiveId);
        if (row is null || archive is null)
            return;
        var installed = archive.Package.InstallationState !=
            PackageInstallationState.NotInstalled;
        var message = string.Format(
            _localization.Get(installed
                ? "ArchiveDeleteInstalledQuestion"
                : "ArchiveDeleteQuestion"),
            archive.EffectiveVersion);
        if (_userDialogs?.Confirm(
                message,
                _localization.Get("ArchiveDeleteTitle"),
                _localization.Get("Cancel"),
                installed
                    ? _localization.Get("RemoveMod")
                    : _localization.Get("Delete")) != true)
        {
            return;
        }
        if (row.ArchiveCount == 1)
        {
            var relations = await _relations.LoadForPackageAsync(
                archiveId);
            if (relations.Count > 0 &&
                _userDialogs?.Confirm(
                    _localization.Get(
                        "ArchiveDeleteRelationsWarning"),
                    _localization.Get("ArchiveDeleteTitle")) != true)
            {
                return;
            }
        }
        ModifiedFilesAuthorization? authorizedModifiedFiles = null;
        if (installed && _removalService is not null)
        {
            var plan = await _removalService.BuildPreflightPlanAsync(
                archive,
                _getGameProfile(),
                _getSettings().LibraryRoot);
            if (!plan.CanRemove)
            {
                if (plan.HasEligibleModifiedFiles)
                {
                    var confirmed = _windowService.ConfirmModifiedFilesRemoval(
                        row.DisplayName,
                        plan.EligibleModifiedPaths,
                        _localization);
                    if (!confirmed)
                        return;
                    authorizedModifiedFiles = ModifiedFilesAuthorization.ForPackage(
                        plan.PackageId,
                        plan.EligibleModifiedIdentities);
                }
                else
                {
                    _userDialogs?.ShowError(LocalizeRemovalError(
                        plan.ErrorCode,
                        plan.ProblemPaths));
                    return;
                }
            }
        }
        var result = await _archiveDeletion.DeleteAsync(
            archiveId,
            removeInstalled: installed,
            _getGameProfile(),
            _getSettings().LibraryRoot,
            authorizedModifiedFiles: authorizedModifiedFiles);
        if (!result.Success)
        {
            _userDialogs?.ShowError(
                result.ErrorMessage ??
                _localization.Get("ArchiveDeleteFailed"));
        }
        await ReloadFromDatabaseAsync();
    }

    internal async Task AddRelationFromDialogAsync(PackageId packageId)
    {
        OrganizerPackageRecord? currentPackage = null;
        string currentModDisplayName = string.Empty;
        foreach (var row in Packages)
        {
            var match = row.Archives.FirstOrDefault(archive =>
                archive.Package.PackageId == packageId);
            if (match is not null)
            {
                currentPackage = match;
                currentModDisplayName = row.DisplayName;
                break;
            }
        }
        if (currentPackage is null)
            return;
        var existing = await _relations.LoadForPackageAsync(packageId);
        var existingTargets = existing
            .Where(relation =>
                relation.Relation.FromPackageId == packageId)
            .Select(relation => relation.Relation.ToPackageId)
            .ToHashSet();
        var candidateList = new List<RelationCandidateViewModel>();
        foreach (var row in Packages)
        {
            foreach (var archive in row.Archives)
            {
                var candidatePackageId = archive.Package.PackageId;
                if (candidatePackageId == packageId ||
                    existingTargets.Contains(candidatePackageId))
                {
                    continue;
                }
                candidateList.Add(RelationCandidateViewModel.Create(
                    archive,
                    row.DisplayName,
                    row.LibraryModId,
                    _localization));
            }
        }
        var candidates = candidateList
            .DistinctBy(candidate => candidate.PackageId)
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Component, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.PackageId.Value, StringComparer.Ordinal)
            .ToArray();
        var selection = _windowService.ShowAddRelation(
            currentModDisplayName,
            candidates,
            _localization);
        if (selection is null)
            return;
        var result = await _relations.AddAsync(
            packageId,
            selection.PackageId,
            selection.RelationType,
            PackageRelationSource.User,
            isConfirmed: true);
        if (!result.Success)
        {
            _userDialogs?.ShowError(_localization.Get(
                $"RelationError{result.ErrorCode}"));
            return;
        }
        var targetCandidate = candidates.FirstOrDefault(candidate =>
            candidate.PackageId == selection.PackageId);
        var targetDisplayName = targetCandidate?.DisplayName ??
            selection.PackageId.Value;
        _recordSessionEvent?.Invoke(
            "EventRelationAdded",
            $"{currentModDisplayName} → {targetDisplayName}");
        await ReloadFromDatabaseAsync(packageId);
    }

    internal async Task RemoveRelationFromDialogAsync(
        PackageRelationView relation)
    {
        if (!_windowService.ConfirmRelationRemoval(
                relation.FromDisplayName,
                relation.ToDisplayName,
                _localization))
        {
            return;
        }
        await _relations.DeleteAsync(relation.Relation.RelationId);
        _recordSessionEvent?.Invoke(
            "EventRelationRemoved",
            $"{relation.FromDisplayName} → {relation.ToDisplayName}");
        await ReloadFromDatabaseAsync(relation.Relation.FromPackageId);
    }

    internal async Task DismissNexusRelationAsync(
        NexusRequirementCanonicalKey key)
    {
        if (_nexusRelations is not null)
        {
            await _nexusRelations.DismissRelationAsync(key);
        }
    }

    internal async Task<NexusRequirementSyncResult?> RefreshNexusRelationsForModAsync(
        LibraryModRecord mod,
        CancellationToken cancellationToken = default)
    {
        if (_nexusRequirements is null ||
            !NexusRequirementRefreshEligibility.TryCreate(mod, out var identity))
        {
            return null;
        }

        return await _nexusRequirements.RefreshAsync(
            identity,
            clearDismissals: true,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> AddToDownloadsAsync(
        NexusModIdentity targetMod,
        string displayName,
        string? safeUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (_nexusRelations is not null)
        {
            return await _nexusRelations.AddToDownloadsAsync(
                targetMod,
                displayName,
                safeUrl,
                cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    internal async Task<OrganizerPackageRecord?> EnsureFomodReadyAsync(
        OrganizerPackageRecord target,
        Func<IProgress<FomodPreparationPhase>, Task>?
            continuePreparation = null,
        bool forceFomodSelection = false)
    {
        var responsiveness = ResponsivenessTraceSession.Current;
        if (target.Analysis?.State == PackageAnalysisState.Ready &&
            !forceFomodSelection)
            return target;

        if (target.Analysis?.ResultCode == ArchiveAnalyzerResultCodes.UnsupportedFomod)
        {
            _userDialogs?.ShowError(_localization.Get("FomodUnsupportedArchive"));
            return null;
        }

        if (target.Analysis?.ResultCode ==
                ArchiveAnalyzerResultCodes.FomodSelectionRequired ||
            forceFomodSelection &&
            IsResolvedFomodAnalysis(target.Analysis))
        {
            try
            {
                var fomodService = new FomodService();
                FomodDefinition? fomod;
                using (responsiveness?.Stage(
                           "FOMOD_SELECTION",
                           target.Package.PackageId))
                {
                    fomod = await fomodService
                        .LoadFomodDefinitionAsync(target);
                }
                if (fomod is not null && fomod.IsSupported)
                {
                    ISet<string>? selectedIds;
                    using (responsiveness?.Stage(
                               "FOMOD_SELECTION",
                               target.Package.PackageId))
                    {
                        selectedIds = _windowService.ShowFomodInstaller(
                            target.Package.DisplayName,
                            fomod,
                            _localization);
                    }
                    if (selectedIds is null) return null;

                    OrganizerPackageRecord? updated = null;
                    await _windowService.ShowFomodPreparationAsync(
                        target.Package.DisplayName,
                        async progress =>
                        {
                            progress.Report(
                                FomodPreparationPhase.ValidatingSelection);
                            await Task.Yield();
                            progress.Report(
                                FomodPreparationPhase.PreparingFiles);
                            updated = responsiveness is null
                                ? await fomodService
                                    .ApplyFomodSelectionAsync(
                                        target,
                                        selectedIds,
                                        _repository)
                                : await responsiveness.RunStageAsync(
                                    "FOMOD_APPLY",
                                    target.Package.PackageId,
                                    () => fomodService
                                        .ApplyFomodSelectionAsync(
                                            target,
                                            selectedIds,
                                            _repository));
                            progress.Report(
                                FomodPreparationPhase.UpdatingModData);
                            if (responsiveness is null)
                            {
                                await ReloadFromDatabaseAsync(
                                    target.Package.PackageId,
                                    loadDataInBackground: true);
                            }
                            else
                            {
                                await responsiveness.RunStageAsync(
                                    "FOMOD_RELOAD",
                                    target.Package.PackageId,
                                    () => ReloadFromDatabaseAsync(
                                        target.Package.PackageId,
                                        loadDataInBackground: true));
                            }
                            if (updated.Analysis?.State ==
                                    PackageAnalysisState.Ready &&
                                continuePreparation is not null)
                            {
                                progress.Report(
                                    FomodPreparationPhase
                                        .PreparingInstallPlan);
                                await continuePreparation(progress);
                            }
                        },
                        _localization);
                    if (updated is null)
                        return null;
                    if (updated.Analysis?.State == PackageAnalysisState.Ready)
                    {
                        return updated;
                    }

                    _userDialogs?.ShowError(_localization.Get("FomodUnsupportedArchive"));
                    return null;
                }
                if (fomod is not null && !fomod.IsSupported)
                {
                    _userDialogs?.ShowError(_localization.Get("FomodUnsupportedArchive"));
                    return null;
                }

                _userDialogs?.ShowError(_localization.Get("FomodReanalysisRequired"));
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                _userDialogs?.ShowError(_localization.Get("FomodReanalysisRequired"));
                return null;
            }
        }
        return target;
    }

    internal static bool IsResolvedFomodAnalysis(
        PackageAnalysisRecord? analysis) =>
        analysis is
        {
            State: PackageAnalysisState.Ready,
            DetectedRoot: "FOMOD",
            SelectedRoot: "FOMOD"
        } &&
        analysis.DetectedRoots.Count == 1 &&
        string.Equals(
            analysis.DetectedRoots[0],
            "FOMOD",
            StringComparison.Ordinal) &&
        analysis.Entries.Any(entry =>
            FomodParser.IsFomodEntry(entry.NormalizedPath));

    internal static bool RequiresInteractiveFomodSelection(
        PackageInstallationState installationState,
        PackageAnalysisRecord? analysis) =>
        string.Equals(
            analysis?.ResultCode,
            ArchiveAnalyzerResultCodes.FomodSelectionRequired,
            StringComparison.Ordinal) ||
        installationState == PackageInstallationState.NotInstalled &&
        IsResolvedFomodAnalysis(analysis);

    private async Task InstallModAsync(PackageRowViewModel row)
    {
        if (row.RequiresComponentChoice)
        {
            QueueOpenModDetails(row);
            return;
        }
        if (row.DirectInstallPackageId is not { } packageId)
            return;
        await InstallModInternalAsync(row, packageId);
    }

    private async Task InstallModInternalAsync(
        PackageRowViewModel row,
        PackageId? targetArchiveId = null)
    {
        if (_installationService is null)
            return;

        if (!await EnsureGameReadyForTargetOperationsAsync())
            return;

        OrganizerPackageRecord targetStored;
        if (targetArchiveId is null)
        {
            targetStored = row.Stored;
        }
        else
        {
            var found = row.LibraryMod.Archives.FirstOrDefault(a => a.Package.PackageId == targetArchiveId.Value);
            if (found is null)
            {
                StatusMessage = _localization.Get("PlanErrorArchiveMissing");
                return;
            }
            targetStored = found;
        }

        await using var responsiveness =
            ResponsivenessTraceSession.Start(_uiDispatcher);
        using var requestStage = responsiveness.Stage(
            "INSTALL_REQUEST",
            targetStored.Package.PackageId);
        var installedInFamily = FindSwitchSource(
            row.LibraryMod,
            targetStored);
        if (installedInFamily is not null &&
            installedInFamily.Package.PackageId !=
            targetStored.Package.PackageId &&
            _archiveSwitch is not null)
        {
            await ExecuteArchivePrimaryAsync(
                row.LibraryModId,
                targetStored.Package.PackageId);
            return;
        }
        if (_nexusInstallWarning is not null)
        {
            var dependencyWarnings = await _nexusInstallWarning
                .GetSingleInstallWarningsAsync(row.LibraryMod, targetStored);
            if (dependencyWarnings.Count > 0)
            {
                var decision = await responsiveness.RunStageAsync(
                    "INSTALL_NEXUS_DEPENDENCY_WARNING",
                    targetStored.Package.PackageId,
                    () => Task.FromResult(_windowService.ConfirmNexusDependencies(
                        dependencyWarnings,
                        isBatch: false,
                        _localization)));
                if (decision.Decision != NexusDependencyWarningDecisionKind.Continue)
                {
                    HandleNexusDependencyAssistance(decision);
                    return;
                }
            }
        }
        try
        {
            InstallPlan? plan = null;
            var packageId = targetStored.Package.PackageId;
            var requiresFreshFomodSelection =
                targetStored.Package.InstallationState ==
                    PackageInstallationState.NotInstalled &&
                IsResolvedFomodAnalysis(targetStored.Analysis);
            var requiresFomodSelection = RequiresInteractiveFomodSelection(
                targetStored.Package.InstallationState,
                targetStored.Analysis);
            var readyStored = await EnsureFomodReadyAsync(
                targetStored,
                requiresFomodSelection
                    ? async _ =>
                    {
                        var refreshedRow = Packages.FirstOrDefault(item =>
                            item.Archives.Any(a => a.Package.PackageId == packageId)) ?? row;
                        var storedForPreflight = refreshedRow.LibraryMod.Archives.FirstOrDefault(a =>
                            a.Package.PackageId == packageId) ?? targetStored;
                        var profileForPreflight = _getGameProfile();
                        var libraryRootForPreflight =
                            _getSettings().LibraryRoot;
                        plan = await BuildInstallPreflightInBackgroundAsync(
                            responsiveness,
                            packageId,
                            storedForPreflight,
                            profileForPreflight,
                            libraryRootForPreflight);
                    }
                    : null,
                forceFomodSelection: requiresFreshFomodSelection);
            if (readyStored is null)
                return;
            targetStored = readyStored;
            row = Packages.FirstOrDefault(p => p.Archives.Any(a => a.Package.PackageId == packageId)) ?? row;

            var storedForPreflight = targetStored;
            var profileForPreflight = _getGameProfile();
            var libraryRootForPreflight = _getSettings().LibraryRoot;
            plan ??= await ShowInstallPreflightPreparationAsync(
                row.DisplayName,
                responsiveness,
                packageId,
                storedForPreflight,
                profileForPreflight,
                libraryRootForPreflight);

            _recordSessionEvent?.Invoke(
                "EventInstallPlanBuilt",
                row.DisplayName);
            using var relationStage = responsiveness.Stage(
                "INSTALL_RELATION_CHECKS",
                packageId);
            var potentialConflictingOwners = plan.Entries
                .Where(entry =>
                    entry.Action == InstallPlanAction.Conflict &&
                    entry.OwnerPackageId is not null)
                .GroupBy(entry => new
                {
                    PackageId = entry.OwnerPackageId!.Value,
                    Name = entry.OwnerDisplayName ?? entry.Reason ?? "—"
                })
                .ToArray();
            var conflictingOwners = new List<
                (PackageId PackageId, string Name, int FileCount)>();
            foreach (var owner in potentialConflictingOwners)
            {
                if (await _repository.AreSameLibraryModAsync(
                        packageId,
                        owner.Key.PackageId))
                {
                    continue;
                }
                conflictingOwners.Add((
                    owner.Key.PackageId,
                    owner.Key.Name,
                    owner.Count()));
            }
            var ownersToLink = new List<(PackageId Id, string Name)>();
            foreach (var owner in conflictingOwners)
            {
                var choice = _windowService.ConfirmDetectedOverlay(
                    row.DisplayName,
                    owner.Name,
                    owner.FileCount,
                    _localization);
                if (choice == DetectedOverlayChoice.Cancel)
                    return;
                if (choice == DetectedOverlayChoice.KeepConflict)
                {
                    responsiveness.RunStage(
                        "INSTALL_PLAN_DIALOG",
                        packageId,
                        () => _windowService.ShowInstallPlan(
                            plan,
                            _localization));
                    return;
                }
                ownersToLink.Add((owner.PackageId, owner.Name));
            }
            var createdRelations = new List<long>();
            foreach (var owner in ownersToLink)
            {
                var relation = await _relations.AddAsync(
                    packageId,
                    owner.Id,
                    PackageRelationType.AddOnOf,
                    PackageRelationSource.DetectedOverlap,
                    isConfirmed: true);
                if (!relation.Success)
                {
                    foreach (var relationId in createdRelations)
                        await _relations.DeleteAsync(relationId);
                    _userDialogs?.ShowError(_localization.Get(
                        $"RelationError{relation.ErrorCode}"));
                    return;
                }
                createdRelations.Add(relation.Relation!.RelationId);
                _recordSessionEvent?.Invoke(
                    "EventRelationAdded",
                    $"{row.DisplayName} → {owner.Name}");
            }
            if (conflictingOwners.Count > 0)
            {
                storedForPreflight = targetStored;
                profileForPreflight = _getGameProfile();
                libraryRootForPreflight = _getSettings().LibraryRoot;
                plan = await ShowInstallPreflightPreparationAsync(
                    row.DisplayName,
                    responsiveness,
                    packageId,
                    storedForPreflight,
                    profileForPreflight,
                    libraryRootForPreflight);
            }
            relationStage.Dispose();
            var installPlanAccepted = responsiveness.RunStage(
                "INSTALL_PLAN_DIALOG",
                packageId,
                () => _windowService.ShowInstallPlan(
                    plan,
                    _localization,
                    allowInstall: true));
            if (!installPlanAccepted)
            {
                return;
            }
            var installationConfirmed = responsiveness.RunStage(
                "INSTALL_CONFIRMATION",
                packageId,
                () => _windowService.ConfirmInstallation(
                    row.DisplayName,
                    plan,
                    _localization));
            if (!installationConfirmed)
            {
                return;
            }

            _recordSessionEvent?.Invoke(
                "EventInstallationStarted",
                row.DisplayName);
            var storedForInstall = targetStored;
            var profileForInstall = _getGameProfile();
            var libraryRootForInstall = _getSettings().LibraryRoot;
            var result = await responsiveness.RunStageAsync(
                "INSTALL_PROGRESS_WINDOW_SHOW",
                packageId,
                () => _windowService.ShowInstallationProgressAsync(
                    row.DisplayName,
                    (progress, cancellationToken) =>
                        RunInstallOperationInBackgroundAsync(
                            responsiveness,
                            packageId,
                            storedForInstall,
                            profileForInstall,
                            libraryRootForInstall,
                            progress,
                            cancellationToken),
                    _localization));
            if (result.Success)
            {
                _recordSessionEvent?.Invoke(
                    "EventStagingPrepared",
                    string.Format(
                        _localization.Get("EventFilesCount"),
                        result.Plan?.Entries.Count ?? 0));
                _recordSessionEvent?.Invoke(
                    "EventInstallationCompleted",
                    row.DisplayName);
                foreach (var owner in plan.Entries
                             .Where(entry =>
                                 entry.Action ==
                                 InstallPlanAction.OverlayMod &&
                                 !string.IsNullOrWhiteSpace(
                                     entry.OwnerDisplayName))
                             .Select(entry => entry.OwnerDisplayName!)
                             .Distinct(StringComparer.CurrentCultureIgnoreCase))
                {
                    _recordSessionEvent?.Invoke(
                        "EventModOverlayCompleted",
                        $"{row.DisplayName} → {owner}");
                }
            }
            else if (result.Status == InstallOperationStatus.RolledBack)
            {
                _recordSessionEvent?.Invoke(
                    "EventInstallationRolledBack",
                    row.DisplayName);
            }
            else if (result.Status ==
                     InstallOperationStatus.RecoveryRequired)
            {
                RecoveryRequired = true;
                _recordSessionEvent?.Invoke(
                    "EventInstallationRecoveryRequired",
                    row.DisplayName);
            }
            else if (!result.Success)
            {
                _recordSessionEvent?.Invoke(
                    "EventInstallationNotCompleted",
                    row.DisplayName);
            }
            var resultMessage = LocalizeInstallationResult(result);
            StatusMessage = result.Success
                ? string.Empty
                : _localization.Get("InstallationNotCompleted");
            if (!result.Success)
                _userDialogs?.ShowError(resultMessage);
            await responsiveness.RunStageAsync(
                "POST_INSTALL_RELOAD",
                packageId,
                () => ReloadFromDatabaseAsync(targetStored.Package.PackageId));
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            _recordSessionEvent?.Invoke(
                "EventInstallationNotCompleted",
                row.DisplayName);
            StatusMessage = _localization.Get(
                "InstallationNotCompleted");
            _userDialogs?.ShowError(
                _localization.Get(
                    "InstallationResultInstallationFailed"));
        }
    }

    private async Task<InstallPlan> ShowInstallPreflightPreparationAsync(
        string displayName,
        ResponsivenessTraceSession responsiveness,
        PackageId packageId,
        OrganizerPackageRecord stored,
        GameProfileRecord? profile,
        string libraryRoot)
    {
        InstallPlan? plan = null;
        await _windowService.ShowFomodPreparationAsync(
            displayName,
            async progress =>
            {
                progress.Report(
                    FomodPreparationPhase.PreparingInstallPlan);
                await Task.Yield();
                plan = await BuildInstallPreflightInBackgroundAsync(
                    responsiveness,
                    packageId,
                    stored,
                    profile,
                    libraryRoot);
            },
            _localization);
        return plan ?? throw new InvalidOperationException(
            "Install preflight did not produce a plan.");
    }

    private Task<InstallPlan> BuildInstallPreflightInBackgroundAsync(
        ResponsivenessTraceSession responsiveness,
        PackageId packageId,
        OrganizerPackageRecord stored,
        GameProfileRecord? profile,
        string libraryRoot) =>
        _fomodPreparationBackground.RunTaskAsync(() =>
            responsiveness.RunStageAsync(
                "INSTALL_PREFLIGHT",
                packageId,
                () => _installationService!.BuildPreflightPlanAsync(
                    stored,
                    profile,
                    libraryRoot)));

    private Task<ModInstallationResult>
        RunInstallOperationInBackgroundAsync(
            ResponsivenessTraceSession responsiveness,
            PackageId packageId,
            OrganizerPackageRecord stored,
            GameProfileRecord? profile,
            string libraryRoot,
            IProgress<ModInstallationProgress> progress,
            CancellationToken cancellationToken) =>
        _fomodPreparationBackground.RunTaskAsync(() =>
            responsiveness.RunStageAsync(
                "INSTALL_OPERATION",
                packageId,
                () => _installationService!.InstallAsync(
                    stored,
                    profile,
                    libraryRoot,
                    progress,
                    cancellationToken)));

    private static OrganizerPackageRecord? FindSwitchSource(
        LibraryModRecord libraryMod,
        OrganizerPackageRecord target) =>
        libraryMod.InstalledArchives.FirstOrDefault(installed =>
        {
            var operation = libraryMod.GetArchiveOperation(
                installed.Package.PackageId,
                target.Package.PackageId);
            return operation != ArchiveVersionOperation.Install;
        });

    private string LocalizeInstallationResult(
        ModInstallationResult result)
    {
        if (result.Success)
            return _localization.Get("InstallationCompleted");
        var key = $"InstallationResult{result.ErrorCode}";
        var localized = _localization.Get(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? _localization.Get("InstallationResultInstallationFailed")
            : localized;
    }

    internal async Task RemoveComponentAsync(
        LibraryModId libraryModId,
        PackageId packageId)
    {
        var row = Packages.FirstOrDefault(item =>
            item.LibraryModId == libraryModId);
        if (row is null)
            return;
        await RemoveModInternalAsync(row, packageId);
    }

    private async Task RemoveModAsync(PackageRowViewModel row)
    {
        if (row.RequiresComponentChoice)
        {
            QueueOpenModDetails(row);
            return;
        }
        if (row.DirectRemovePackageId is not { } packageId)
            return;
        await RemoveModInternalAsync(row, packageId);
    }

    private async Task RemoveModInternalAsync(
        PackageRowViewModel row,
        PackageId? targetPackageId = null)
    {
        if (_removalService is null)
            return;
        await using var responsiveness =
            ResponsivenessTraceSession.Start(_uiDispatcher);
        using var requestStage = responsiveness.Stage(
            "REMOVE_REQUEST",
            targetPackageId ?? row.Record.PackageId);
        try
        {
            OrganizerPackageRecord[] installedArchives;
            PackageId targetId;

            if (targetPackageId is not null)
            {
                var found = row.LibraryMod.Archives.FirstOrDefault(a =>
                    a.Package.PackageId == targetPackageId.Value &&
                    a.Package.InstallationState == PackageInstallationState.Installed);
                if (found is null)
                {
                    StatusMessage = _localization.Get("RemovalNotCompleted");
                    return;
                }
                installedArchives = [found];
                targetId = targetPackageId.Value;
            }
            else
            {
                return;
            }

            var displayName = row.DisplayName;
            var profileForPreflight = _getGameProfile();
            var libraryRootForPreflight = _getSettings().LibraryRoot;
            IReadOnlyList<ModRemovalPlan>? plans = null;
            await _windowService.ShowRemovalPreparationAsync(
                displayName,
                async () =>
                {
                    plans = await BuildRemovalPreflightInBackgroundAsync(
                        responsiveness,
                        targetId,
                        installedArchives,
                        profileForPreflight,
                        libraryRootForPreflight);
                },
                _localization);
            if (plans is null)
            {
                throw new InvalidOperationException(
                    "Removal preflight did not produce plans.");
            }
            ModifiedFilesAuthorization? authorizedModifiedFiles = null;
            var blocked = plans.FirstOrDefault(plan => !plan.CanRemove);
            if (blocked is not null)
            {
                if (blocked.HasEligibleModifiedFiles)
                {
                    var confirmed = responsiveness.RunStage(
                        "REMOVE_MODIFIED_CONFIRMATION",
                        targetId,
                        () => _windowService.ConfirmModifiedFilesRemoval(
                            row.DisplayName,
                            blocked.EligibleModifiedPaths,
                            _localization));
                    if (!confirmed)
                    {
                        StatusMessage = _localization.Get("RemovalNotCompleted");
                        return;
                    }
                    authorizedModifiedFiles = ModifiedFilesAuthorization.ForPackage(
                        blocked.PackageId,
                        blocked.EligibleModifiedIdentities);

                    plans = await BuildRemovalPreflightInBackgroundAsync(
                        responsiveness,
                        targetId,
                        installedArchives,
                        profileForPreflight,
                        libraryRootForPreflight,
                        authorizedModifiedFiles: authorizedModifiedFiles);
                    if (plans is null || plans.Any(p => !p.CanRemove))
                    {
                        StatusMessage = _localization.Get("RemovalNotCompleted");
                        var stillBlocked = plans?.FirstOrDefault(p => !p.CanRemove);
                        if (stillBlocked is not null)
                        {
                            _userDialogs?.ShowError(LocalizeRemovalError(
                                stillBlocked.ErrorCode,
                                stillBlocked.ProblemPaths));
                        }
                        return;
                    }
                }
                else
                {
                    StatusMessage = _localization.Get("RemovalNotCompleted");
                    _userDialogs?.ShowError(LocalizeRemovalError(
                        blocked.ErrorCode,
                        blocked.ProblemPaths));
                    return;
                }
            }
            var plan = new ModRemovalPlan
            {
                PackageId = targetId,
                FilesToDelete = plans.Sum(item => item.FilesToDelete),
                FilesToRestore = plans.Sum(item => item.FilesToRestore),
                LayersToDetach = plans.Sum(item => item.LayersToDetach)
            };

            var dependents = await responsiveness.RunStageAsync(
                "REMOVE_RELATION_CHECKS",
                targetId,
                () => _relations.LoadInstalledDependentsAsync(targetId));
            var dependentChoice = dependents.Count == 0
                ? (DependentRemovalChoice?)null
                : responsiveness.RunStage(
                    "REMOVE_CONFIRMATION",
                    targetId,
                    () => _windowService.ConfirmDependentRemoval(
                        row.DisplayName,
                        dependents,
                        _localization));
            if (dependentChoice == DependentRemovalChoice.Cancel)
            {
                return;
            }
            if (dependentChoice == DependentRemovalChoice.Cascade)
            {
                await RemoveCascadeAsync(row, responsiveness, targetId);
                return;
            }
            if (authorizedModifiedFiles is null &&
                dependentChoice is null &&
                !responsiveness.RunStage(
                    "REMOVE_CONFIRMATION",
                    targetId,
                    () => _windowService.ConfirmRemoval(
                        row.DisplayName,
                        plan,
                        _localization)))
            {
                return;
            }

            ModRemovalResult? result = null;
            await _windowService.ShowRemovalProgressAsync(
                displayName,
                async () =>
                {
                    foreach (var archive in installedArchives)
                    {
                        result = await RemoveOneAsync(
                            row,
                            responsiveness,
                            archive,
                            authorizedModifiedFiles: authorizedModifiedFiles);
                        if (!result.Success)
                            break;
                    }
                },
                _localization);
            if (result?.Success == true)
            {
                _recordSessionEvent?.Invoke(
                    "EventRemovalCompleted",
                    row.DisplayName);
                if (dependentChoice ==
                    DependentRemovalChoice.SelectedOnly)
                {
                    _recordSessionEvent?.Invoke(
                        "EventParentRemovedDependentsKept",
                        row.DisplayName);
                }
            }
            else if (result?.Status ==
                     InstallOperationStatus.RecoveryRequired)
            {
                RecoveryRequired = true;
                _recordSessionEvent?.Invoke(
                    "EventRemovalRecoveryRequired",
                    null);
            }
            else
            {
                _recordSessionEvent?.Invoke(
                    "EventRemovalRolledBack",
                    null);
            }

            StatusMessage = result?.Success == true
                ? string.Empty
                : _localization.Get("RemovalNotCompleted");
            if (result?.Success != true)
            {
                _userDialogs?.ShowError(LocalizeRemovalError(
                    result?.ErrorCode,
                    result?.ProblemPaths ?? []));
            }
            await responsiveness.RunStageAsync(
                "POST_REMOVE_RELOAD",
                targetId,
                () => ReloadFromDatabaseAsync(targetId));
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            StatusMessage = _localization.Get("RemovalNotCompleted");
            _userDialogs?.ShowError(
                _localization.Get("RemovalResultRemovalFailed"));
        }
    }

    private async Task<ModRemovalResult> RemoveOneAsync(
        PackageRowViewModel row,
        ResponsivenessTraceSession responsiveness,
        OrganizerPackageRecord? archive = null,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null)
    {
        _recordSessionEvent?.Invoke(
            "EventRemovalStarted",
            row.DisplayName);
        var storedForRemoval = archive ?? row.Stored;
        var packageId = storedForRemoval.Package.PackageId;
        var profileForRemoval = _getGameProfile();
        var libraryRootForRemoval = _getSettings().LibraryRoot;
        var result = await RunRemovalOperationInBackgroundAsync(
            responsiveness,
            packageId,
            storedForRemoval,
            profileForRemoval,
            libraryRootForRemoval,
            authorizedModifiedFiles: authorizedModifiedFiles);
        if (storedForRemoval.Package.PackageId == row.Record.PackageId)
        {
            row.SetInstallationState(result.InstallationState);
        }
        return result;
    }

    private async Task RemoveCascadeAsync(
        PackageRowViewModel root,
        ResponsivenessTraceSession responsiveness,
        PackageId rootPackageId)
    {
        var order = await _relations.BuildCascadeRemovalOrderAsync(
            rootPackageId);
        var removed = new List<string>();
        foreach (var packageId in order)
        {
            var row = Packages.FirstOrDefault(candidate =>
                candidate.LibraryMod.Archives.Any(archive =>
                    archive.Package.PackageId == packageId));
            if (row is null)
                continue;
            var targetArchive = row.LibraryMod.Archives.FirstOrDefault(archive =>
                archive.Package.PackageId == packageId &&
                archive.Package.InstallationState == PackageInstallationState.Installed);
            if (targetArchive is null)
                continue;
            var installedArchives = new[] { targetArchive };
            var profileForPreflight = _getGameProfile();
            var libraryRootForPreflight = _getSettings().LibraryRoot;
            var plans = await BuildRemovalPreflightInBackgroundAsync(
                responsiveness,
                packageId,
                installedArchives,
                profileForPreflight,
                libraryRootForPreflight);
            if (plans.Any(plan => !plan.CanRemove))
            {
                await ReloadFromDatabaseAsync(rootPackageId);
                _userDialogs?.ShowError(string.Format(
                    _localization.Get("CascadeRemovalStopped"),
                    string.Join(", ", removed),
                    row.DisplayName));
                return;
            }
            var succeeded = true;
            await _windowService.ShowRemovalProgressAsync(
                row.DisplayName,
                async () =>
                {
                    var result = await RemoveOneAsync(
                        row,
                        responsiveness,
                        targetArchive);
                    if (!result.Success)
                        succeeded = false;
                },
                _localization);
            if (!succeeded)
            {
                await ReloadFromDatabaseAsync(rootPackageId);
                _userDialogs?.ShowError(string.Format(
                    _localization.Get("CascadeRemovalStopped"),
                    string.Join(", ", removed),
                    row.DisplayName));
                return;
            }
            removed.Add(targetArchive.EffectiveDisplayName);
            _recordSessionEvent?.Invoke(
                "EventRemovalCompleted",
                row.DisplayName);
        }
        _recordSessionEvent?.Invoke(
            "EventCascadeRemovalCompleted",
            removed.Count.ToString(CultureInfo.InvariantCulture));
        await ReloadFromDatabaseAsync(rootPackageId);
    }

    private Task<IReadOnlyList<ModRemovalPlan>>
        BuildRemovalPreflightInBackgroundAsync(
            ResponsivenessTraceSession responsiveness,
            PackageId packageId,
            IReadOnlyList<OrganizerPackageRecord> installedArchives,
            GameProfileRecord? profile,
            string libraryRoot,
            ModifiedFilesAuthorization? authorizedModifiedFiles = null) =>
        _fomodPreparationBackground.RunTaskAsync(() =>
            responsiveness.RunStageAsync(
                "REMOVE_PREFLIGHT",
                packageId,
                async () =>
                {
                    var plans = new List<ModRemovalPlan>(
                        installedArchives.Count);
                    foreach (var archive in installedArchives)
                    {
                        plans.Add(await _removalService!
                            .BuildPreflightPlanAsync(
                                archive,
                                profile,
                                libraryRoot,
                                authorizedModifiedFiles: authorizedModifiedFiles));
                    }
                    return (IReadOnlyList<ModRemovalPlan>)plans;
                }));

    private Task<ModRemovalResult> RunRemovalOperationInBackgroundAsync(
        ResponsivenessTraceSession responsiveness,
        PackageId packageId,
        OrganizerPackageRecord stored,
        GameProfileRecord? profile,
        string libraryRoot,
        ModifiedFilesAuthorization? authorizedModifiedFiles = null) =>
        _fomodPreparationBackground.RunTaskAsync(() =>
            responsiveness.RunStageAsync(
                "REMOVE_OPERATION",
                packageId,
                () => _removalService!.RemoveAsync(
                    stored,
                    profile,
                    libraryRoot,
                    authorizedModifiedFiles: authorizedModifiedFiles)));

    private async Task InstallSelectedAsync()
    {
        if (_batchInstall is null)
            return;

        if (!await EnsureGameReadyForTargetOperationsAsync())
            return;

        var resolution = ResolveBatchInstallSelection(SelectedRows);
        if (resolution.AmbiguousRow is { } ambiguousRow)
        {
            QueueOpenModDetails(ambiguousRow);
            return;
        }
        if (resolution.PackageIds.Count == 0)
        {
            return;
        }
        var candidates = BuildBatchCandidates(
                resolution.PackageIds);
        var selectedPackageIds = candidates
            .Select(candidate => candidate.Package.Package.PackageId)
            .ToArray();
        await using var responsiveness =
            ResponsivenessTraceSession.Start(_uiDispatcher);
        using var requestStage = responsiveness.Stage(
            "BATCH_INSTALL_REQUEST");
        IsBatchBusy = true;
        try
        {
            if (_nexusInstallWarning is not null)
            {
                var dependencyWarnings = await _nexusInstallWarning
                    .GetBatchInstallWarningsAsync(candidates);
                if (dependencyWarnings.Count > 0)
                {
                    var decision = _windowService.ConfirmNexusDependencies(
                        dependencyWarnings,
                        isBatch: true,
                        _localization);
                    if (decision.Decision != NexusDependencyWarningDecisionKind.Continue)
                    {
                        HandleNexusDependencyAssistance(decision);
                        return;
                    }
                }
            }
            var selected = await ResolveBatchFomodSelectionsAsync(
                selectedPackageIds);
            if (selected is null)
                return;
            var profileForPreflight = _getGameProfile();
            var libraryRootForPreflight = _getSettings().LibraryRoot;
            BatchInstallPlan? plan = null;
            await _windowService.ShowBatchPreparationAsync(
                BatchOperationKind.Install,
                async () =>
                {
                    plan = await PrepareBatchInstallInBackgroundAsync(
                        responsiveness,
                        selected,
                        profileForPreflight,
                        libraryRootForPreflight);
                },
                _localization);
            if (plan is null)
            {
                throw new InvalidOperationException(
                    "Batch install preparation did not produce a plan.");
            }
            if (plan.Decisions.Count > 0)
            {
                var childDecisions = await BuildBatchChildOverlayDecisionsAsync(
                    plan.Decisions,
                    selected);
                if (childDecisions.Count > 0)
                {
                    var confirmed = _windowService.ConfirmBatchChildOverlays(
                        childDecisions,
                        _localization);
                    if (!confirmed)
                    {
                        StatusMessage = _localization.Get("BatchOperationCancelled");
                        return;
                    }

                    var createdRelations = new List<long>();
                    foreach (var decision in childDecisions)
                    {
                        var addResult = await _relations.AddAsync(
                            decision.ChildPackageId,
                            decision.ParentPackageId,
                            PackageRelationType.AddOnOf,
                            PackageRelationSource.DetectedOverlap,
                            isConfirmed: true);
                        if (!addResult.Success)
                        {
                            foreach (var relationId in createdRelations)
                                await _relations.DeleteAsync(relationId);
                            _userDialogs?.ShowError(
                                _localization.Get($"RelationError{addResult.ErrorCode}"));
                            return;
                        }
                        if (addResult.Relation is not null)
                        {
                            createdRelations.Add(addResult.Relation.RelationId);
                        }
                        _recordSessionEvent?.Invoke(
                            "EventRelationAdded",
                            $"{decision.ChildDisplayName} → {decision.ParentDisplayName}");
                    }

                    await _windowService.ShowBatchPreparationAsync(
                        BatchOperationKind.Install,
                        async () =>
                        {
                            plan = await PrepareBatchInstallInBackgroundAsync(
                                responsiveness,
                                selected,
                                profileForPreflight,
                                libraryRootForPreflight);
                        },
                        _localization);
                    if (plan is null)
                    {
                        throw new InvalidOperationException(
                            "Batch install preparation did not produce a plan.");
                    }
                }
            }
            var profileForExecution = _getGameProfile();
            var libraryRootForExecution = _getSettings().LibraryRoot;
            var result = _windowService.ShowBatchInstall(
                plan,
                async (progress, cancellationToken) =>
                {
                    var operationResult = await
                        RunBatchInstallOperationInBackgroundAsync(
                        responsiveness,
                        plan,
                        profileForExecution,
                        libraryRootForExecution,
                        progress,
                        cancellationToken);
                    await _batchPackageStateChanges.WaitForDrainAsync();
                    return operationResult;
                },
                _localization);
            if (result is null)
                return;
            await responsiveness.RunStageAsync(
                "BATCH_INSTALL_POST_RELOAD",
                null,
                () => RefreshInstallationStatesAsync(result));
            StatusMessage = string.Format(
                _localization.Get("BatchOperationSummary"),
                result.SuccessfulCount,
                result.FailedCount,
                result.NotStartedCount,
                result.SkippedCount);
            ClearBatchSelection();
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    private async Task<IReadOnlyList<BatchChildOverlayDecision>>
        BuildBatchChildOverlayDecisionsAsync(
            IReadOnlyList<RelationDecisionRequirement> decisions,
            IReadOnlyList<BatchPackageCandidate> candidates)
    {
        var result = new List<BatchChildOverlayDecision>();
        var seenPairs = new HashSet<(PackageId, PackageId)>();

        if (_nexusRelations is null)
            return result;

        foreach (var decision in decisions)
        {
            if (await _repository.AreSameLibraryModAsync(
                    decision.ChildPackageId,
                    decision.ParentPackageId))
            {
                continue;
            }

            LibraryModRecord? childMod = null;
            LibraryModRecord? parentMod = null;

            foreach (var row in Packages)
            {
                if (row.LibraryMod.Archives.Any(a =>
                        a.Package.PackageId == decision.ChildPackageId))
                {
                    childMod = row.LibraryMod;
                }
                if (row.LibraryMod.Archives.Any(a =>
                        a.Package.PackageId == decision.ParentPackageId))
                {
                    parentMod = row.LibraryMod;
                }
            }

            if (childMod is null || parentMod is null)
                continue;

            NexusModIdentity? childIdentity = null;
            if (NexusRequirementRefreshEligibility.TryCreate(childMod, out var cId))
                childIdentity = cId;
            else if (childMod.Archives.FirstOrDefault()?.Package is { } cPkg && NexusRequirementRefreshEligibility.TryCreate(cPkg, out var cPkgId))
                childIdentity = cPkgId;

            NexusModIdentity? parentIdentity = null;
            if (NexusRequirementRefreshEligibility.TryCreate(parentMod, out var pId))
                parentIdentity = pId;
            else if (parentMod.Archives.FirstOrDefault()?.Package is { } pPkg && NexusRequirementRefreshEligibility.TryCreate(pPkg, out var pPkgId))
                parentIdentity = pPkgId;

            if (childIdentity is null || parentIdentity is null)
                continue;

            bool childRequiresParent = false;
            bool parentRequiresChild = false;

            try
            {
                var childRelations = await _nexusRelations.LoadRelationsAsync(childIdentity.Value);
                if (childRelations?.Forward.Relations.Any(edge =>
                    (edge.LocalState.LibraryModId is not null && edge.LocalState.LibraryModId == parentMod.LibraryModId) ||
                    (NexusGameIdentityBridge.GetEffectiveTargetIdentity(edge.Edge.Target) is { } targetId && targetId == parentIdentity.Value)) == true)
                {
                    childRequiresParent = true;
                }

                var parentRelations = await _nexusRelations.LoadRelationsAsync(parentIdentity.Value);
                if (parentRelations?.Forward.Relations.Any(edge =>
                    (edge.LocalState.LibraryModId is not null && edge.LocalState.LibraryModId == childMod.LibraryModId) ||
                    (NexusGameIdentityBridge.GetEffectiveTargetIdentity(edge.Edge.Target) is { } targetId && targetId == childIdentity.Value)) == true)
                {
                    parentRequiresChild = true;
                }
            }
            catch
            {
                // Fail closed on error
                continue;
            }

            // Exactly one direction must be proven (fail closed if neither or ambiguous both)
            if (childRequiresParent == parentRequiresChild)
                continue;

            PackageId actualChildId;
            LibraryModRecord actualChildMod;
            string actualChildName;
            PackageId actualParentId;
            LibraryModRecord actualParentMod;
            string actualParentName;

            if (childRequiresParent)
            {
                actualChildId = decision.ChildPackageId;
                actualChildMod = childMod;
                actualChildName = decision.ChildDisplayName;
                actualParentId = decision.ParentPackageId;
                actualParentMod = parentMod;
                actualParentName = decision.ParentDisplayName;
            }
            else
            {
                // Reversed raw coordinator decision normalized to child -> parent
                actualChildId = decision.ParentPackageId;
                actualChildMod = parentMod;
                actualChildName = decision.ParentDisplayName;
                actualParentId = decision.ChildPackageId;
                actualParentMod = childMod;
                actualParentName = decision.ChildDisplayName;
            }

            var pairKey = (actualChildId, actualParentId);
            if (!seenPairs.Add(pairKey))
                continue;

            result.Add(new BatchChildOverlayDecision(
                ChildPackageId: actualChildId,
                ChildLibraryModId: actualChildMod.LibraryModId,
                ChildDisplayName: actualChildName,
                ParentPackageId: actualParentId,
                ParentLibraryModId: actualParentMod.LibraryModId,
                ParentDisplayName: actualParentName,
                OverlappingFileCount: decision.OverlappingFileCount,
                HasNexusRequirementEvidence: true));
        }

        return result;
    }

    private async Task<IReadOnlyList<BatchPackageCandidate>?>
        ResolveBatchFomodSelectionsAsync(
            IReadOnlyList<PackageId> selectedPackageIds)
    {
        foreach (var packageId in selectedPackageIds)
        {
            var (_, archive) = FindBatchPackage(packageId);
            if (!RequiresInteractiveFomodSelection(
                    archive.Package.InstallationState,
                    archive.Analysis))
            {
                continue;
            }
            var forceFreshSelection =
                archive.Package.InstallationState ==
                    PackageInstallationState.NotInstalled &&
                IsResolvedFomodAnalysis(archive.Analysis);
            if (await EnsureFomodReadyAsync(
                    archive,
                    forceFomodSelection: forceFreshSelection) is null)
            {
                return null;
            }
        }
        return BuildBatchCandidates(selectedPackageIds);
    }

    private (PackageRowViewModel Row, OrganizerPackageRecord Archive)
        FindBatchPackage(PackageId packageId)
    {
        foreach (var row in Packages)
        {
            var archive = row.LibraryMod.Archives.FirstOrDefault(item =>
                item.Package.PackageId == packageId);
            if (archive is not null)
                return (row, archive);
        }
        throw new InvalidOperationException(
            $"Selected batch package '{packageId.Value}' is no longer " +
            "available.");
    }

    private async Task RemoveSelectedAsync()
    {
        if (_batchRemoval is null)
            return;
        var resolution = ResolveBatchRemovalSelection(SelectedRows);
        if (resolution.AmbiguousRow is { } ambiguousRow)
        {
            QueueOpenModDetails(ambiguousRow);
            return;
        }
        if (resolution.PackageIds.Count == 0)
            return;
        await using var responsiveness =
            ResponsivenessTraceSession.Start(_uiDispatcher);
        using var requestStage = responsiveness.Stage(
            "BATCH_REMOVE_REQUEST");
        var library = BuildBatchCandidates(
            Packages,
            includeAllInstalledArchives: true);
        var selected = BuildBatchCandidates(resolution.PackageIds);
        IsBatchBusy = true;
        try
        {
            var profileForPreflight = _getGameProfile();
            var libraryRootForPreflight = _getSettings().LibraryRoot;
            (IReadOnlyList<BatchPackageCandidate> Candidates,
                BatchRemovalPlan Plan)? prepared = null;
            await _windowService.ShowBatchPreparationAsync(
                BatchOperationKind.Remove,
                async () =>
                {
                    prepared = await PrepareBatchRemovalInBackgroundAsync(
                        responsiveness,
                        selected,
                        library,
                        profileForPreflight,
                        libraryRootForPreflight,
                        expandRelated: false);
                },
                _localization);
            if (prepared is null)
            {
                throw new InvalidOperationException(
                    "Batch removal preparation did not produce a plan.");
            }
            selected = prepared.Value.Candidates;
            var plan = prepared.Value.Plan;
            if (plan.UnselectedRelated.Count > 0)
            {
                var choice = _windowService.ConfirmBatchRelatedRemoval(
                    plan.UnselectedRelated,
                    _localization);
                if (choice == BatchRelatedRemovalChoice.Cancel)
                    return;
                if (choice == BatchRelatedRemovalChoice.IncludeRelated)
                {
                    profileForPreflight = _getGameProfile();
                    libraryRootForPreflight =
                        _getSettings().LibraryRoot;
                    prepared = null;
                    await _windowService.ShowBatchPreparationAsync(
                        BatchOperationKind.Remove,
                        async () =>
                        {
                            prepared = await
                                PrepareBatchRemovalInBackgroundAsync(
                                    responsiveness,
                                    selected,
                                    library,
                                    profileForPreflight,
                                    libraryRootForPreflight,
                                    expandRelated: true);
                        },
                        _localization);
                    if (prepared is null)
                    {
                        throw new InvalidOperationException(
                            "Related batch removal preparation did not " +
                            "produce a plan.");
                    }
                    selected = prepared.Value.Candidates;
                    plan = prepared.Value.Plan;
                }
            }

            var blockedItems = plan.Items.Where(item => !item.CanRemove && item.State != BatchItemState.NotInstalled).ToList();
            var hardBlockers = blockedItems.Where(item => item.Plan is null || !item.Plan.HasEligibleModifiedFiles).ToList();
            var eligibleModifiedItems = blockedItems.Where(item => item.Plan is not null && item.Plan.HasEligibleModifiedFiles).ToList();

            if (hardBlockers.Count == 0 && eligibleModifiedItems.Count > 0)
            {
                var decisions = eligibleModifiedItems
                    .Select(item => new BatchModifiedFilesDecision(
                        item.Candidate.Package.Package.PackageId,
                        item.Candidate.DisplayName,
                        item.Plan!.EligibleModifiedIdentities,
                        item.Plan!.EligibleModifiedPaths))
                    .ToList();

                var confirmed = decisions.Count == 1
                    ? _windowService.ConfirmModifiedFilesRemoval(
                        decisions[0].ModDisplayName,
                        decisions[0].ModifiedPaths,
                        _localization)
                    : _windowService.ConfirmBatchModifiedFilesRemoval(
                        decisions,
                        _localization);

                if (!confirmed)
                {
                    StatusMessage = _localization.Get("RemovalNotCompleted");
                    return;
                }

                var authorizedModifiedFiles = ModifiedFilesAuthorization.Combine(
                    decisions.Select(d => ModifiedFilesAuthorization.ForPackage(d.PackageId, d.Identities)));

                profileForPreflight = _getGameProfile();
                libraryRootForPreflight = _getSettings().LibraryRoot;
                prepared = null;
                await _windowService.ShowBatchPreparationAsync(
                    BatchOperationKind.Remove,
                    async () =>
                    {
                        prepared = await PrepareBatchRemovalInBackgroundAsync(
                            responsiveness,
                            selected,
                            library,
                            profileForPreflight,
                            libraryRootForPreflight,
                            expandRelated: false,
                            authorizedModifiedFiles: authorizedModifiedFiles);
                    },
                    _localization);
                if (prepared is null)
                {
                    throw new InvalidOperationException(
                        "Authorized batch removal preparation did not produce a plan.");
                }
                selected = prepared.Value.Candidates;
                plan = prepared.Value.Plan;
                if (!plan.CanExecute)
                {
                    StatusMessage = _localization.Get("RemovalNotCompleted");
                    return;
                }
            }

            var profileForExecution = _getGameProfile();
            var libraryRootForExecution = _getSettings().LibraryRoot;
            var result = _windowService.ShowBatchRemoval(
                plan,
                async (progress, cancellationToken) =>
                {
                    var operationResult = await
                        RunBatchRemovalOperationInBackgroundAsync(
                        responsiveness,
                        plan,
                        profileForExecution,
                        libraryRootForExecution,
                        progress,
                        cancellationToken);
                    await _batchPackageStateChanges.WaitForDrainAsync();
                    return operationResult;
                },
                _localization);
            if (result is null)
                return;
            await responsiveness.RunStageAsync(
                "BATCH_REMOVE_POST_RELOAD",
                null,
                () => RefreshInstallationStatesAsync(result));
            StatusMessage = string.Format(
                _localization.Get("BatchOperationSummary"),
                result.SuccessfulCount,
                result.FailedCount,
                result.NotStartedCount,
                result.SkippedCount);
            ClearBatchSelection();
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    private async Task DeleteSelectedCompletelyAsync()
    {
        if (_archiveDeletion is null || _userDialogs is null || RecoveryRequired)
            return;

        var selectedMods = SelectedRows
            .Select(row => row.LibraryMod)
            .DistinctBy(mod => mod.LibraryModId)
            .ToList();
        if (selectedMods.Count == 0)
            return;

        var modCount = selectedMods.Count;
        var allArchives = selectedMods
            .SelectMany(mod => mod.Archives)
            .ToList();
        var archiveCount = allArchives.Count;
        var installedArchives = selectedMods
            .SelectMany(mod => mod.InstalledArchives)
            .ToList();
        var installedModCount = selectedMods
            .Count(mod => mod.InstalledArchives.Count > 0);

        var gameProfile = _getGameProfile();
        var libraryRoot = _getSettings().LibraryRoot;

        var libraryCandidates = installedArchives.Count > 0 && _batchRemoval is not null
            ? BuildBatchCandidates(Packages, includeAllInstalledArchives: true)
            : Array.Empty<BatchPackageCandidate>();
        var selectedCandidates = installedArchives.Count > 0 && _batchRemoval is not null
            ? BuildBatchCandidates(installedArchives.Select(a => a.Package.PackageId))
            : Array.Empty<BatchPackageCandidate>();

        ModifiedFilesAuthorization? authorizedModifiedFiles = null;
        var blockers = new List<(string ModName, string Reason)>();
        var eligibleModifiedDecisions = new List<BatchModifiedFilesDecision>();
        var unselectedRelated = new List<string>();
        BatchRemovalPlan? removalPlan = null;

        await using (var responsiveness = ResponsivenessTraceSession.Start(_uiDispatcher))
        {
            using var requestStage = responsiveness.Stage("BATCH_FULL_DELETE_REQUEST");
            await _windowService.ShowBatchPreparationAsync(
                BatchOperationKind.Remove,
                async () =>
                {
                    await _fomodPreparationBackground.RunTaskAsync(async () =>
                    {
                        if (_removalService is not null)
                        {
                            foreach (var mod in selectedMods)
                            {
                                foreach (var installed in mod.InstalledArchives)
                                {
                                    var plan = await _removalService.BuildPreflightPlanAsync(
                                        installed,
                                        gameProfile,
                                        libraryRoot);
                                    if (!plan.CanRemove)
                                    {
                                        if (plan.HasEligibleModifiedFiles)
                                        {
                                            eligibleModifiedDecisions.Add(new BatchModifiedFilesDecision(
                                                installed.Package.PackageId,
                                                mod.EffectiveDisplayName,
                                                plan.EligibleModifiedIdentities,
                                                plan.EligibleModifiedPaths));
                                        }
                                        else
                                        {
                                            blockers.Add((
                                                mod.EffectiveDisplayName,
                                                LocalizeRemovalError(plan.ErrorCode, plan.ProblemPaths)));
                                        }
                                    }
                                }
                            }
                        }

                        if (installedArchives.Count > 0 && _batchRemoval is not null)
                        {
                            removalPlan = await _batchRemoval.PrepareAsync(
                                selectedCandidates,
                                libraryCandidates,
                                gameProfile,
                                libraryRoot);

                            if (removalPlan.UnselectedRelated.Count > 0)
                            {
                                unselectedRelated.AddRange(
                                    removalPlan.UnselectedRelated
                                        .SelectMany(g => g.Dependents.Select(d => $"• {d.DisplayName} (← {g.Owner.DisplayName})"))
                                        .Distinct());
                            }

                            if (!removalPlan.CanExecute)
                            {
                                foreach (var item in removalPlan.Items.Where(i => !i.CanRemove))
                                {
                                    if (item.Plan is null || !item.Plan.HasEligibleModifiedFiles)
                                    {
                                        blockers.Add((
                                            item.Candidate.DisplayName,
                                            LocalizeRemovalError(item.Reason, item.Plan?.ProblemPaths ?? Array.Empty<string>())));
                                    }
                                }
                            }
                        }
                    });
                },
                _localization);
        }

        if (unselectedRelated.Count > 0)
        {
            _userDialogs.ShowError(string.Format(
                _localization.Get("BatchFullDeleteUnselectedRelatedMessage"),
                string.Join(Environment.NewLine, unselectedRelated)));
            return;
        }

        if (blockers.Count > 0)
        {
            var blockerDetails = string.Join(Environment.NewLine + Environment.NewLine,
                blockers.Distinct().Select(b => $"{b.ModName} — {b.Reason}"));
            _userDialogs.ShowError(string.Format(
                _localization.Get("BatchFullDeleteBlockersMessage"),
                blockerDetails));
            return;
        }

        var message = string.Format(
            _localization.Get(installedModCount > 0
                ? "BatchFullDeleteInstalledQuestion"
                : "BatchFullDeleteQuestion"),
            modCount,
            archiveCount,
            installedModCount);

        var confirmed = _userDialogs.Confirm(
            message,
            _localization.Get("BatchFullDeleteTitle"),
            _localization.Get("Cancel"),
            _localization.Get("BatchFullDeleteConfirm"));
        if (!confirmed)
            return;

        if (eligibleModifiedDecisions.Count > 0)
        {
            var distinctDecisions = eligibleModifiedDecisions
                .DistinctBy(d => d.PackageId)
                .ToList();

            var confirmedModified = distinctDecisions.Count == 1
                ? _windowService.ConfirmModifiedFilesRemoval(
                    distinctDecisions[0].ModDisplayName,
                    distinctDecisions[0].ModifiedPaths,
                    _localization)
                : _windowService.ConfirmBatchModifiedFilesRemoval(
                    distinctDecisions,
                    _localization);

            if (!confirmedModified)
                return;

            authorizedModifiedFiles = ModifiedFilesAuthorization.Combine(
                distinctDecisions.Select(d => ModifiedFilesAuthorization.ForPackage(d.PackageId, d.Identities)));

            var postReplanBlockers = new List<(string ModName, string Reason)>();
            await using (var responsiveness = ResponsivenessTraceSession.Start(_uiDispatcher))
            {
                using var requestStage = responsiveness.Stage("BATCH_FULL_DELETE_REPLAN");
                await _windowService.ShowBatchPreparationAsync(
                    BatchOperationKind.Remove,
                    async () =>
                    {
                        await _fomodPreparationBackground.RunTaskAsync(async () =>
                        {
                            if (_removalService is not null)
                            {
                                foreach (var mod in selectedMods)
                                {
                                    foreach (var installed in mod.InstalledArchives)
                                    {
                                        var plan = await _removalService.BuildPreflightPlanAsync(
                                            installed,
                                            gameProfile,
                                            libraryRoot,
                                            authorizedModifiedFiles: authorizedModifiedFiles);
                                        if (!plan.CanRemove)
                                        {
                                            postReplanBlockers.Add((
                                                mod.EffectiveDisplayName,
                                                LocalizeRemovalError(plan.ErrorCode, plan.ProblemPaths)));
                                        }
                                    }
                                }
                            }

                            if (installedArchives.Count > 0 && _batchRemoval is not null)
                            {
                                removalPlan = await _batchRemoval.PrepareAsync(
                                    selectedCandidates,
                                    libraryCandidates,
                                    gameProfile,
                                    libraryRoot,
                                    authorizedModifiedFiles: authorizedModifiedFiles);

                                if (!removalPlan.CanExecute)
                                {
                                    foreach (var item in removalPlan.Items.Where(i => !i.CanRemove))
                                    {
                                        postReplanBlockers.Add((
                                            item.Candidate.DisplayName,
                                            LocalizeRemovalError(item.Reason, item.Plan?.ProblemPaths ?? Array.Empty<string>())));
                                    }
                                }
                            }
                        });
                    },
                    _localization);
            }

            if (postReplanBlockers.Count > 0)
            {
                var blockerDetails = string.Join(Environment.NewLine + Environment.NewLine,
                    postReplanBlockers.Distinct().Select(b => $"{b.ModName} — {b.Reason}"));
                _userDialogs.ShowError(string.Format(
                    _localization.Get("BatchFullDeleteBlockersMessage"),
                    blockerDetails));
                return;
            }
        }

        IsBatchBusy = true;
        try
        {
            if (installedArchives.Count > 0 && removalPlan is not null)
            {
                var profileForExecution = _getGameProfile();
                var libraryRootForExecution = _getSettings().LibraryRoot;

                var removalResult = _windowService.ShowBatchRemoval(
                    removalPlan,
                    async (progress, cancellationToken) =>
                    {
                        var operationResult = await _fomodPreparationBackground.RunTaskAsync(() =>
                            _batchRemoval!.ExecuteAsync(
                                removalPlan,
                                profileForExecution,
                                libraryRootForExecution,
                                progress,
                                cancellationToken));
                        await _batchPackageStateChanges.WaitForDrainAsync();
                        return operationResult;
                    },
                    _localization);

                if (removalResult is null ||
                    removalResult.FailedCount > 0 ||
                    removalResult.NotStartedCount > 0 ||
                    removalResult.SuccessfulCount != removalPlan.ExecutionOrder.Count)
                {
                    await RefreshInstallationStatesAsync(removalResult ?? new BatchOperationResult
                    {
                        Kind = BatchOperationKind.Remove,
                        Items = []
                    });
                    StatusMessage = _localization.Get("BatchOperationFailed");
                    return;
                }

                await RefreshInstallationStatesAsync(removalResult);
            }

            var currentMods = Packages
                .Where(row => selectedMods.Any(sm => sm.LibraryModId == row.LibraryModId))
                .Select(row => row.LibraryMod)
                .DistinctBy(m => m.LibraryModId)
                .ToList();

            if (currentMods.Any(m => m.InstalledArchives.Count > 0))
            {
                StatusMessage = _localization.Get("BatchOperationFailed");
                return;
            }

            var successfulModIds = new List<LibraryModId>();
            var failedMods = new List<(LibraryModId Id, string DisplayName, string Error)>();

            await _windowService.ShowBatchFullDeleteProgressAsync(
                selectedMods.Count,
                selectedMods[0].EffectiveDisplayName,
                async progress =>
                {
                    var gameProfileForDelete = _getGameProfile();
                    var libraryRootForDelete = _getSettings().LibraryRoot;

                    await _fomodPreparationBackground.RunTaskAsync(async () =>
                    {
                        for (var i = 0; i < selectedMods.Count; i++)
                        {
                            var mod = selectedMods[i];
                            progress.Report((i + 1, selectedMods.Count, mod.EffectiveDisplayName));
                            try
                            {
                                var deleteResult = await _archiveDeletion.DeleteModAsync(
                                    mod.LibraryModId,
                                    gameProfileForDelete,
                                    libraryRootForDelete,
                                    authorizedModifiedFiles: authorizedModifiedFiles);
                                if (deleteResult.Success)
                                {
                                    successfulModIds.Add(mod.LibraryModId);
                                }
                                else
                                {
                                    failedMods.Add((
                                        mod.LibraryModId,
                                        mod.EffectiveDisplayName,
                                        LocalizeFullDeleteError(deleteResult)));
                                }
                            }
                            catch (Exception ex)
                            {
                                _exceptionLogger?.Invoke(ex);
                                failedMods.Add((
                                    mod.LibraryModId,
                                    mod.EffectiveDisplayName,
                                    ex.Message));
                            }
                        }
                    });
                },
                _localization);

            await ReloadFromDatabaseAsync();
            _packageCount = Packages.Count;
            _archiveCount = Packages.Sum(item => item.ArchiveCount);
            NotifySummaries();

            if (failedMods.Count > 0)
            {
                var failedIds = failedMods.Select(f => f.Id).ToHashSet();
                foreach (var row in Packages.Where(r => failedIds.Contains(r.LibraryModId)))
                {
                    row.SetBatchSelected(true);
                }
                NotifyBatchSelectionChanged();

                StatusMessage = string.Format(
                    _localization.Get("BatchFullDeletePartialSummary"),
                    successfulModIds.Count,
                    failedMods.Count);

                var failureDetails = string.Join(Environment.NewLine,
                    failedMods.Select(f => $"{f.DisplayName} — {f.Error}"));
                _userDialogs.ShowError(failureDetails);
            }
            else
            {
                ClearBatchSelection();
                StatusMessage = string.Format(
                    _localization.Get("BatchFullDeleteAllSuccessSummary"),
                    successfulModIds.Count);
            }
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    private Task<BatchInstallPlan> PrepareBatchInstallInBackgroundAsync(
        ResponsivenessTraceSession responsiveness,
        IReadOnlyList<BatchPackageCandidate> selected,
        GameProfileRecord? profile,
        string libraryRoot) =>
        _fomodPreparationBackground.RunTaskAsync(() =>
            responsiveness.RunStageAsync(
                "BATCH_INSTALL_PREFLIGHT",
                null,
                () => _batchInstall!.PrepareAsync(
                    selected,
                    profile,
                    libraryRoot)));

    private Task<BatchOperationResult>
        RunBatchInstallOperationInBackgroundAsync(
            ResponsivenessTraceSession responsiveness,
            BatchInstallPlan plan,
            GameProfileRecord? profile,
            string libraryRoot,
            IProgress<BatchOperationProgress> progress,
            CancellationToken cancellationToken) =>
        _fomodPreparationBackground.RunTaskAsync(() =>
            responsiveness.RunStageAsync(
                "BATCH_INSTALL_OPERATION",
                null,
                () => _batchInstall!.ExecuteAsync(
                    plan,
                    profile,
                    libraryRoot,
                    progress,
                    cancellationToken)));

    private Task<(IReadOnlyList<BatchPackageCandidate> Candidates,
        BatchRemovalPlan Plan)> PrepareBatchRemovalInBackgroundAsync(
            ResponsivenessTraceSession responsiveness,
            IReadOnlyList<BatchPackageCandidate> selected,
            IReadOnlyList<BatchPackageCandidate> library,
            GameProfileRecord? profile,
            string libraryRoot,
            bool expandRelated,
            ModifiedFilesAuthorization? authorizedModifiedFiles = null) =>
        _fomodPreparationBackground.RunTaskAsync(async () =>
            await responsiveness.RunStageAsync(
                "BATCH_REMOVE_PREFLIGHT",
                null,
                async () =>
                {
                    IReadOnlyList<BatchPackageCandidate> candidates =
                        selected;
                    if (expandRelated)
                    {
                        candidates = (await _batchRemoval!
                            .ExpandRelatedAsync(selected, library)).ToArray();
                    }
                    var plan = await _batchRemoval!.PrepareAsync(
                        candidates,
                        library,
                        profile,
                        libraryRoot,
                        authorizedModifiedFiles: authorizedModifiedFiles);
                    return (candidates, plan);
                }));

    private Task<BatchOperationResult>
        RunBatchRemovalOperationInBackgroundAsync(
            ResponsivenessTraceSession responsiveness,
            BatchRemovalPlan plan,
            GameProfileRecord? profile,
            string libraryRoot,
            IProgress<BatchOperationProgress> progress,
            CancellationToken cancellationToken) =>
        _fomodPreparationBackground.RunTaskAsync(() =>
            responsiveness.RunStageAsync(
                "BATCH_REMOVE_OPERATION",
                null,
                () => _batchRemoval!.ExecuteAsync(
                    plan,
                    profile,
                    libraryRoot,
                    progress,
                    cancellationToken)));

    private static BatchSelectionResolution
        ResolveBatchInstallSelection(
            IEnumerable<PackageRowViewModel> rows)
    {
        var packageIds = new List<PackageId>();
        foreach (var row in rows)
        {
            var candidates = row.LibraryMod.Archives
                .Where(IsBatchInstallPrimaryCandidate)
                .ToArray();
            if (candidates.Length > 1)
                return new([], row);
            if (candidates.Length == 1)
                packageIds.Add(candidates[0].Package.PackageId);
            else if (row.LibraryMod.Archives.Count == 1 &&
                     row.LibraryMod.Archives[0].Package.InstallationState ==
                         PackageInstallationState.Installed &&
                     IsResolvedFomodAnalysis(
                         row.LibraryMod.Archives[0].Analysis))
            {
                packageIds.Add(
                    row.LibraryMod.Archives[0].Package.PackageId);
            }
        }
        return new(packageIds, null);
    }

    private static bool IsBatchInstallPrimaryCandidate(
        OrganizerPackageRecord archive) =>
        PackageRowViewModel.IsArchiveInstallable(archive) ||
        (archive.IsPresent &&
         RequiresInteractiveFomodSelection(
             archive.Package.InstallationState,
             archive.Analysis));

    private static BatchSelectionResolution
        ResolveBatchRemovalSelection(
            IEnumerable<PackageRowViewModel> rows)
    {
        var packageIds = new List<PackageId>();
        foreach (var row in rows)
        {
            var candidates = row.LibraryMod.InstalledArchives;
            if (candidates.Count > 1)
                return new([], row);
            if (candidates.Count == 1)
                packageIds.Add(candidates[0].Package.PackageId);
        }
        return new(packageIds, null);
    }

    private IReadOnlyList<BatchPackageCandidate> BuildBatchCandidates(
        IEnumerable<PackageId> packageIds)
    {
        _uiDispatcher.VerifyAccess();
        var visible = PackagesView
            .Cast<PackageRowViewModel>()
            .ToArray();
        var ordered = visible
            .Concat(Packages.Where(row => !visible.Contains(row)))
            .ToArray();
        var visualOrder = ordered
            .Select((row, index) => (row, index))
            .ToDictionary(pair => pair.row, pair => pair.index);
        return packageIds
            .Select(FindBatchPackage)
            .Where(item => visualOrder.ContainsKey(item.Row))
            .Select(item => new BatchPackageCandidate(
                item.Archive,
                item.Row.DisplayName,
                visualOrder[item.Row]))
            .OrderBy(item => item.VisualOrder)
            .ToArray();
    }

    private IReadOnlyList<BatchPackageCandidate> BuildBatchCandidates(
        IEnumerable<PackageRowViewModel> rows,
        bool includeAllInstalledArchives = false)
    {
        _uiDispatcher.VerifyAccess();
        var visible = PackagesView
            .Cast<PackageRowViewModel>()
            .ToArray();
        var ordered = visible
            .Concat(Packages.Where(row => !visible.Contains(row)))
            .ToArray();
        var visualOrder = ordered
            .Select((row, index) => (row, index))
            .ToDictionary(pair => pair.row, pair => pair.index);
        return rows
            .Where(visualOrder.ContainsKey)
            .SelectMany(row =>
            {
                var archives = includeAllInstalledArchives
                    ? row.LibraryMod.InstalledArchives
                    : [row.Stored];
                return archives.Select(archive =>
                    new BatchPackageCandidate(
                        archive,
                        row.DisplayName,
                        visualOrder[row]));
            })
            .OrderBy(item => item.VisualOrder)
            .ToArray();
    }

    private sealed record BatchSelectionResolution(
        IReadOnlyList<PackageId> PackageIds,
        PackageRowViewModel? AmbiguousRow);

    private async Task RefreshInstallationStatesAsync(
        BatchOperationResult result)
    {
        var retain = SelectedPackage?.Record.PackageId;
        await ReloadFromDatabaseAsync(retain);
        InstallSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCompletelyCommand.NotifyCanExecuteChanged();
    }

    private void OnBatchPackageStateChanged(
        object? sender,
        BatchPackageStateChanged change) =>
        _batchPackageStateChanges.Report(change);

    private void ApplyBatchPackageStateChanged(
        BatchPackageStateChanged change)
    {
        Packages.FirstOrDefault(row =>
                row.Record.PackageId == change.PackageId)
            ?.SetInstallationState(change.InstallationState);
        InstallSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCompletelyCommand.NotifyCanExecuteChanged();
    }

    private string LocalizeRemovalError(
        string? errorCode,
        IReadOnlyList<string> problemPaths)
    {
        var key = $"RemovalResult{errorCode}";
        var message = _localization.Get(key);
        if (string.Equals(message, key, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(errorCode))
        {
            var planKey = $"PlanError{errorCode}";
            message = _localization.Get(planKey);
            if (string.Equals(message, planKey, StringComparison.Ordinal))
                message = _localization.Get("RemovalResultRemovalFailed");
        }
        if (problemPaths.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, problemPaths);
        }
        return message;
    }

    internal string LocalizeFullDeleteError(LibraryModFullDeletionResult? result)
    {
        if (result is not null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            var raw = result.ErrorMessage.Trim();

            if (string.Equals(raw, "PreflightRequired", StringComparison.OrdinalIgnoreCase))
            {
                return _localization.Get("RemovalResultPreflightRequired");
            }
            if (string.Equals(raw, "RemovalBlocked", StringComparison.OrdinalIgnoreCase))
            {
                return _localization.Get("RemovalResultRemovalBlocked");
            }
            var key = $"RemovalResult{raw}";
            var message = _localization.Get(key);
            if (!string.Equals(message, key, StringComparison.Ordinal))
            {
                return message;
            }
            var planKey = $"PlanError{raw}";
            message = _localization.Get(planKey);
            if (!string.Equals(message, planKey, StringComparison.Ordinal))
            {
                return message;
            }
            if (raw.Contains(' ') && !raw.StartsWith("RemovalResult", StringComparison.Ordinal) && !raw.StartsWith("PlanError", StringComparison.Ordinal))
            {
                return raw;
            }
        }
        return _localization.Get("LibraryModFullDeleteFailed");
    }

    private async Task SavePackageAsync(PackageRowViewModel row)
    {
        try
        {
            await _repository.UpdatePackageDetailsAsync(
                row.Record.PackageId,
                null,
                NullIfWhiteSpace(row.EditVersion),
                row.Note,
                row.Group.Id);
            await _repository.UpdateLibraryModDetailsAsync(
                row.LibraryModId,
                NullIfWhiteSpace(row.EditDisplayName),
                row.Group.Id);
            StatusMessage = _localization.Get("PackageSaved");
            await ReloadFromDatabaseAsync(row.Record.PackageId);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private bool CanCheckNexusRelationsFromRow(PackageRowViewModel row) =>
        CanStartNexusRefresh(
            HasBatchSelection ? SelectedRows : [row]);

    private Task CheckNexusRelationsFromRowAsync(PackageRowViewModel row) =>
        StartNexusRefresh(
            HasBatchSelection ? SelectedRows : [row]);

    private bool CanStartNexusRefresh(
        IEnumerable<PackageRowViewModel> rows) =>
        _nexusRefresh is not null &&
        !IsAnalyzing &&
        !IsBatchBusy &&
        !IsRefreshingNexusRelations &&
        rows.Any(row =>
            NexusRequirementRefreshEligibility.TryCreate(
                row.LibraryMod,
                out _));

    private Task StartNexusRefresh(
        IEnumerable<PackageRowViewModel> sourceRows)
    {
        if (_disposed || _nexusRefresh is null ||
            IsAnalyzing || IsBatchBusy || IsRefreshingNexusRelations)
        {
            return Task.CompletedTask;
        }

        var rows = sourceRows.Distinct().ToArray();
        var identities = rows
            .Select(row =>
                NexusRequirementRefreshEligibility.TryCreate(
                    row.LibraryMod,
                    out var identity)
                    ? identity
                    : (NexusModIdentity?)null)
            .Where(identity => identity.HasValue)
            .Select(identity => identity!.Value)
            .Distinct()
            .ToArray();
        if (identities.Length == 0)
            return Task.CompletedTask;
        var skipped = rows.Count(row =>
            !NexusRequirementRefreshEligibility.TryCreate(
                row.LibraryMod,
                out _));
        _activeNexusRefreshCompletion = RunNexusRefreshAsync(
            identities,
            skipped);
        return _activeNexusRefreshCompletion;
    }

    private async Task RunNexusRefreshAsync(
        IReadOnlyList<NexusModIdentity> identities,
        int skipped)
    {
        _nexusRefreshCancellation = new CancellationTokenSource();
        IsRefreshingNexusRelations = true;
        StatusMessage = string.Format(
            _localization.Get("NexusRelationsProgress"),
            0,
            identities.Count);
        var progress = new Progress<NexusRequirementRefreshProgress>(value =>
            StatusMessage = string.Format(
                _localization.Get("NexusRelationsProgress"),
                value.Completed,
                value.Total));
        try
        {
            var summary = await _nexusRefresh!.RunSequentialAsync(
                identities,
                skipped,
                progress,
                _nexusRefreshCancellation.Token);
            StatusMessage = string.Format(
                _localization.Get("NexusRelationsSummary"),
                summary.Success,
                summary.Partial,
                summary.Failure,
                summary.Skipped);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _localization.Get("NexusRelationsCancelled");
        }
        finally
        {
            _nexusRefreshCancellation.Dispose();
            _nexusRefreshCancellation = null;
            IsRefreshingNexusRelations = false;
        }
    }

    private Task AnalyzeRowsAsync(
        IReadOnlyList<PackageRowViewModel> rows,
        bool force)
    {
        if (_disposed || rows.Count == 0 || IsAnalyzing)
            return Task.CompletedTask;
        _activeAnalysisCompletion = RunAnalysisRowsAsync(rows, force);
        return _activeAnalysisCompletion;
    }

    private async Task RunAnalysisRowsAsync(
        IReadOnlyList<PackageRowViewModel> rows,
        bool force)
    {
        _analysisCancellation = new CancellationTokenSource();
        IsAnalyzing = true;
        _analysisCompleted = 0;
        _analysisTotal = rows.Count;
        OnPropertyChanged(nameof(AnalysisProgressText));
        foreach (var row in rows)
            row.SetTransientState(PackageAnalysisState.Analyzing);

        var progress = new Progress<AnalysisQueueProgress>(value =>
        {
            _analysisCompleted = value.Completed;
            _analysisTotal = value.Total;
            OnPropertyChanged(nameof(AnalysisProgressText));
        });
        var selectedId = SelectedPackage?.Record.PackageId;
        try
        {
            await _queue.AnalyzeAsync(
                rows.Select(row => row.Record).ToArray(),
                force,
                progress,
                _analysisCancellation.Token);
            StatusMessage = _localization.Get("AnalysisComplete");
            _recordSessionEvent?.Invoke(
                "EventModAnalysisCompleted",
                string.Format(
                    _localization.Get("EventModsCount"),
                    rows.Count));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _localization.Get("AnalysisCancelled");
        }
        catch (Exception exception)
        {
            StatusMessage = string.Format(
                _localization.Get("AnalysisFailed"),
                exception.Message);
        }
        finally
        {
            _analysisCancellation.Dispose();
            _analysisCancellation = null;
            IsAnalyzing = false;
            await ReloadFromDatabaseAsync(selectedId);
        }
    }

    private void CancelAnalysis() => _analysisCancellation?.Cancel();

    private void CancelActiveOperations()
    {
        CancelAnalysis();
        _nexusRefreshCancellation?.Cancel();
    }

    Task ILibraryAnalysisLifetime.ActiveAnalysisCompletion =>
        Task.WhenAll(
            _activeAnalysisCompletion,
            _activeNexusRefreshCompletion);

    void ILibraryAnalysisLifetime.RequestAnalysisCancellation() =>
        CancelActiveOperations();

    Task ILibraryUiStatePersistenceLifetime
        .CompleteUiStatePersistenceAsync() =>
        _uiStatePersistence.CompleteAsync();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _uiStatePersistence.StopAccepting();
        CancelActiveOperations();
        if (_batchInstall is not null)
            _batchInstall.PackageStateChanged -= OnBatchPackageStateChanged;
        if (_batchRemoval is not null)
            _batchRemoval.PackageStateChanged -= OnBatchPackageStateChanged;
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
    }

    private void Localization_OnLanguageChanged(object? sender, EventArgs e) =>
        RefreshLocalization();

    private async Task SelectRootAsync(
        PackageRowViewModel row,
        string root)
    {
        try
        {
            await _analysis.SelectRootAsync(row.Record, root);
            await ReloadFromDatabaseAsync(row.Record.PackageId);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private async Task CreateGroupAsync()
    {
        var name = _windowService.PromptForGroupName(
            _localization.Get("CreateGroupTitle"),
            _localization.Get("CreateGroup"),
            null);
        if (name is null)
            return;
        try
        {
            var group = await _repository.CreateGroupAsync(name);
            await ReloadFromDatabaseAsync(SelectedPackage?.Record.PackageId);
            SelectedGroup = Groups.Single(item => item.Id == group.Id);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private async Task RenameGroupAsync()
    {
        if (SelectedGroup?.Id is not long id)
            return;
        var name = _windowService.PromptForGroupName(
            _localization.Get("RenameGroupTitle"),
            _localization.Get("Save"),
            SelectedGroup.Name);
        if (name is null)
            return;
        try
        {
            await _repository.RenameGroupAsync(id, name);
            await ReloadFromDatabaseAsync(SelectedPackage?.Record.PackageId);
            SelectedGroup = Groups.Single(item => item.Id == id);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup?.Id is not long id)
            return;
        try
        {
            StatusMessage = await _repository.DeleteGroupIfEmptyAsync(id)
                ? _localization.Get("GroupDeleted")
                : _localization.Get("GroupNotEmpty");
            await ReloadFromDatabaseAsync(SelectedPackage?.Record.PackageId);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private async Task MoveGroupAsync(int offset)
    {
        if (SelectedGroup?.Id is not long id)
            return;
        var realGroups = Groups.Where(group => group.Id is not null).ToList();
        var index = realGroups.FindIndex(group => group.Id == id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= realGroups.Count)
            return;
        (realGroups[index], realGroups[target]) =
            (realGroups[target], realGroups[index]);
        await _repository.ReorderGroupsAsync(
            realGroups.Select(group => group.Id!.Value).ToArray());
        await ReloadFromDatabaseAsync(SelectedPackage?.Record.PackageId);
        SelectedGroup = Groups.Single(group => group.Id == id);
    }

    private Task CreateGroupFromMenuAsync(
        LibraryGroupViewModel group) =>
        CreateGroupAsync();

    private async Task RenameGroupFromMenuAsync(
        LibraryGroupViewModel group)
    {
        SelectedGroup = group;
        await RenameGroupAsync();
    }

    internal static bool IsExactNexusLibraryMod(LibraryModRecord mod, NexusModIdentity identity)
    {
        if (mod.Source != PackageSource.Nexus)
            return false;
        if (mod.NexusModId != identity.ModId)
            return false;
        if (!NexusGameIdentityBridge.TryGetGameDomain(identity.GameId, out var domain) ||
            string.IsNullOrWhiteSpace(domain))
            return false;
        return string.Equals(mod.GameDomain, domain, StringComparison.OrdinalIgnoreCase);
    }

    private void HandleNexusDependencyAssistance(NexusDependencyWarningResult decision)
    {
        switch (decision.Decision)
        {
            case NexusDependencyWarningDecisionKind.OpenLibraryDependency:
                PackageRowViewModel? targetRow = null;
                if (decision.TargetLibraryModId is { } libId)
                {
                    targetRow = Packages.FirstOrDefault(p => p.LibraryModId == libId);
                    if (targetRow is not null && decision.TargetNexusIdentity is { } targetIdentity &&
                        !IsExactNexusLibraryMod(targetRow.LibraryMod, targetIdentity))
                    {
                        targetRow = null;
                    }
                }
                if (targetRow is null && decision.TargetNexusIdentity is { } identity)
                {
                    var matching = Packages.Where(p => IsExactNexusLibraryMod(p.LibraryMod, identity)).ToArray();

                    if (matching.Length == 1)
                    {
                        targetRow = matching[0];
                    }
                    else if (matching.Length > 1)
                    {
                        SearchText = identity.ModId.ToString(CultureInfo.InvariantCulture);
                    }
                }
                if (targetRow is not null)
                {
                    SelectedPackage = targetRow;
                    QueueOpenModDetails(targetRow);
                }
                else if (decision.TargetNexusIdentity is { } searchIdentity)
                {
                    SearchText = searchIdentity.ModId.ToString(CultureInfo.InvariantCulture);
                }
                break;

            case NexusDependencyWarningDecisionKind.OpenDownloadsDependency:
                if (decision.TargetNexusIdentity is { } downloadIdentity)
                {
                    _windowService.NavigateToDownloads(downloadIdentity);
                }
                break;

            case NexusDependencyWarningDecisionKind.OpenUrl:
                if (decision.TargetUrl is { } url)
                {
                    OpenNexusWebUrl(url);
                }
                break;
        }
    }

    public event Action? RequestCloseModDetails;

    public void OpenNexusWebUrl(Uri url)
    {
        if (url is null)
            return;

        if (!NexusBrowserNavigationPolicy.IsValidOpenNexusWebUrl(url.AbsoluteUri, out var validUri) || validUri is null)
            return;

        if (_getSettings().NexusBrowser == NexusBrowserMode.Internal &&
            RequestNavigateNexus is not null)
        {
            RequestCloseModDetails?.Invoke();
            RequestNavigateNexus(validUri.AbsoluteUri);
            return;
        }

        _windowService.OpenExternalUrl(validUri);
    }

    private void OpenSelectedMod()
    {
        if (SelectedPackage is { } row)
            QueueOpenModDetails(row);
    }

    private void QueueOpenModDetails(PackageRowViewModel row)
    {
        var libraryModId = row.LibraryModId;
        _ = _uiDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
                OpenModDetailsNow(libraryModId)));
    }

    internal void OpenModDetailsNow(LibraryModId libraryModId)
    {
        var row = Packages.FirstOrDefault(candidate =>
            candidate.LibraryModId == libraryModId);
        LibrarySelectionDiagnostics.Record(
            "CardOpening",
            libraryModId.Value,
            "LibraryViewModel.OpenModDetailsNow",
            verifyInvariants: true);
        TraceCardOpening(row);
        if (row is null ||
            !Packages.Contains(row) ||
            string.IsNullOrWhiteSpace(row.LibraryModId.Value) ||
            row.Archives is null ||
            row.Archives.Count == 0 ||
            row.Archives.Any(archive =>
                archive is null ||
                archive.Package is null) ||
            !Packages.Any(candidate =>
                candidate.LibraryModId == row.LibraryModId))
        {
            var displayName = row?.DisplayName;
            var message = string.Format(
                _localization.Get("LibraryCardIncompleteData"),
                string.IsNullOrWhiteSpace(displayName)
                    ? "—"
                    : displayName);
            StatusMessage = message;
            _userDialogs?.ShowError(message);
            _exceptionLogger?.Invoke(new InvalidDataException(message));
            return;
        }
        _windowService.ShowModDetails(this, row);
    }

    [Conditional("DEBUG")]
    private void TraceCardOpening(PackageRowViewModel? row)
    {
        var items = PackagesView.Cast<object?>().ToArray();
        Debug.WriteLine(
            $"{DateTimeOffset.Now:O} LibraryCardOpen " +
            $"thread={Environment.CurrentManagedThreadId}; " +
            $"dispatcher={_uiDispatcher.CheckAccess()}; " +
            $"type={row?.GetType().FullName ?? "<null>"}; " +
            $"libraryModId={row?.LibraryModId.Value ?? "<null>"}; " +
            $"name={row?.DisplayName ?? "<null>"}; " +
            $"archives={row?.Archives?.Count ?? -1}; " +
            $"selectedNull={SelectedPackage is null}; " +
            $"selectedInSource={SelectedPackage is not null && items.Contains(SelectedPackage)}; " +
            $"itemCount={items.Length}; " +
            $"nullItems={items.Count(item => item is null)}");
    }

    private async Task AssignGroupAsync(
        PackageRowViewModel row,
        LibraryGroupViewModel group)
    {
        var selected = SelectedRows;
        var retainSelection = SelectedPackage?.Record.PackageId ??
            row.Record.PackageId;
        try
        {
            if (selected.Count == 0)
            {
                await _repository.UpdateLibraryModDetailsAsync(
                    row.LibraryModId,
                    NullIfWhiteSpace(row.EditDisplayName),
                    group.Id);
            }
            else
            {
                await _repository.AssignLibraryModsToGroupAsync(
                    selected.Select(item => item.LibraryModId).ToArray(),
                    group.Id);
            }
            await ReloadFromDatabaseAsync(retainSelection);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private Task AnalyzeGroupFromMenuAsync(
        LibraryGroupViewModel group) =>
        AnalyzeRowsAsync(
            Packages.Where(row => row.Group.Id == group.Id).ToArray(),
            false);

    private async Task DeleteGroupFromMenuAsync(
        LibraryGroupViewModel group)
    {
        SelectedGroup = group;
        await DeleteGroupAsync();
    }

    private async Task MoveGroupFromMenuAsync(
        LibraryGroupViewModel group,
        int offset)
    {
        SelectedGroup = group;
        await MoveGroupAsync(offset);
    }

    private void PersistExpanded(long? groupId, bool expanded)
    {
        if (groupId is null)
            return;
        _uiStatePersistence.TrySchedule(() =>
            PersistExpandedAsync(groupId.Value, expanded));
    }

    private Task PersistExpandedAsync(long groupId, bool expanded) =>
        _repository.SetGroupExpandedAsync(groupId, expanded);

    private Task PersistSelectedGroupFilterAsync(string value)
    {
        long? groupId = long.TryParse(
            value,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
        return _repository.SetSelectedGroupAsync(groupId);
    }

    private bool FilterPackage(object value)
    {
        if (value is not PackageRowViewModel row)
            return false;
        var search = SearchText.Trim();
        if (search.Length > 0 &&
            !row.DisplayName.Contains(
                search,
                StringComparison.CurrentCultureIgnoreCase) &&
            !row.ArchivePath.Contains(
                search,
                StringComparison.CurrentCultureIgnoreCase) &&
            !(row.LibraryMod.NexusModId?.ToString(CultureInfo.InvariantCulture).Contains(
                search,
                StringComparison.OrdinalIgnoreCase) == true) &&
            !(row.Record.NexusModId?.ToString(CultureInfo.InvariantCulture).Contains(
                search,
                StringComparison.OrdinalIgnoreCase) == true))
            return false;

        var sourceMatches = SelectedFilter switch
        {
            "Nexus" => row.Record.Source == PackageSource.Nexus,
            "Local" => row.Record.Source != PackageSource.Nexus,
            "WithMetadata" => row.Record.HasMetadata,
            "WithoutMetadata" => !row.Record.HasMetadata,
            _ => true
        };
        var analysisMatches = SelectedAnalysisFilter switch
        {
            nameof(PackageAnalysisState.NotAnalyzed) =>
                row.AnalysisState == PackageAnalysisState.NotAnalyzed,
            nameof(PackageAnalysisState.Ready) =>
                row.AnalysisState == PackageAnalysisState.Ready,
            nameof(PackageAnalysisState.RequiresSelection) =>
                row.AnalysisState == PackageAnalysisState.RequiresSelection,
            ProblemsFilter => !row.IsPresent ||
                row.AnalysisState is
                    PackageAnalysisState.Blocked or
                    PackageAnalysisState.Error or
                    PackageAnalysisState.Stale,
            _ => true
        };
        var installedMatches = SelectedInstalledFilter switch
        {
            "Installed" => row.InstallationState == PackageInstallationState.Installed,
            "NotInstalled" => row.InstallationState == PackageInstallationState.NotInstalled,
            _ => true
        };
        var groupMatches = SelectedGroupFilter == AllFilter ||
            SelectedGroupFilter == UngroupedFilter && row.Group.Id is null ||
            long.TryParse(
                SelectedGroupFilter,
                CultureInfo.InvariantCulture,
                out var groupId) &&
            row.Group.Id == groupId;
        return sourceMatches && analysisMatches && installedMatches && groupMatches;
    }

    private bool ShowEmptyGroupInCurrentFilter(LibraryGroupViewModel group)
    {
        if (group.Id is not long groupId)
            return false;
        return SelectedGroupFilter == AllFilter ||
            long.TryParse(
                SelectedGroupFilter,
                CultureInfo.InvariantCulture,
                out var selectedGroupId) &&
            selectedGroupId == groupId;
    }

    private void RefreshLocalization()
    {
        RefreshChoices();
        var ungrouped = Groups.FirstOrDefault(group => group.Id is null);
        ungrouped?.SetName(_localization.Get("Ungrouped"));
        foreach (var package in Packages)
            package.RefreshLocalization();
        foreach (var group in Groups)
            group.RefreshLocalization();
        foreach (var property in new[]
                 {
                     nameof(Header), nameof(Subtitle), nameof(SearchPlaceholder),
                     nameof(PackageCount), nameof(FoundPackagesSummary),
                     nameof(SkippedFoldersSummary), nameof(LastUpdatedSummary),
                     nameof(LastUpdatedValue),
                     nameof(RefreshLabel), nameof(FilterAllDisplay),
                     nameof(FilterNexusDisplay), nameof(FilterLocalDisplay),
                     nameof(FilterWithMetadataDisplay),
                     nameof(FilterWithoutMetadataDisplay),
                     nameof(AnalysisAllDisplay),
                     nameof(AnalysisNotAnalyzedDisplay),
                     nameof(AnalysisReadyDisplay),
                     nameof(AnalysisRequiresSelectionDisplay),
                     nameof(AnalysisProblemsDisplay), nameof(ColumnName),
                     nameof(ColumnSource), nameof(ColumnVersion),
                     nameof(ColumnFormat), nameof(ColumnSize),
                     nameof(ColumnPath), nameof(ColumnInstalled),
                     nameof(CheckInstallLabel), nameof(AnalyzeSelectedLabel),
                     nameof(ReanalyzeLabel), nameof(AnalyzeGroupLabel),
                     nameof(AnalyzeAllLabel), nameof(CheckNexusRelationsLabel),
                     nameof(CancelLabel),
                     nameof(PackageDetailsLabel), nameof(DisplayNameLabel),
                     nameof(VersionLabel), nameof(GroupLabel), nameof(NoteLabel),
                     nameof(AnalysisLabel), nameof(SaveLabel),
                     nameof(PathLabel), nameof(SourceLabel),
                     nameof(AuthorLabel), nameof(CategoryLabel),
                     nameof(NexusModIdLabel), nameof(NexusFileIdLabel),
                     nameof(NexusUrlLabel), nameof(SizeLabel),
                     nameof(FormatLabel), nameof(ModifiedLabel),
                     nameof(AnalysisResultLabel),
                     nameof(OpenFolderLabel), nameof(OpenNexusLabel),
                     nameof(CreateGroupLabel), nameof(RenameGroupLabel),
                     nameof(DeleteGroupLabel), nameof(MoveUpLabel),
                     nameof(MoveDownLabel), nameof(NoPackageSelectedLabel),
                     nameof(AnalysisProgressText), nameof(CloseLabel),
                     nameof(RefreshToolTip), nameof(AddGroupLabel),
                     nameof(SelectedCountLabel),
                     nameof(InstallSelectedLabel),
                     nameof(RemoveSelectedLabel),
                     nameof(DeleteSelectedCompletelyLabel),
                     nameof(ClearSelectionLabel),
                     nameof(InstallSelectedContextLabel),
                     nameof(RemoveSelectedContextLabel),
                     nameof(DeleteSelectedCompletelyContextLabel)
                 })
        {
            OnPropertyChanged(property);
        }
        PackagesView.Refresh();
    }

    private void RefreshChoices()
    {
        FilterChoices =
        [
            new(AllFilter, _localization.Get("FilterAll")),
            new("Nexus", _localization.Get("FilterNexus")),
            new("Local", _localization.Get("FilterLocal")),
            new("WithMetadata", _localization.Get("FilterWithMetadata")),
            new("WithoutMetadata", _localization.Get("FilterWithoutMetadata"))
        ];
        AnalysisFilterChoices =
        [
            new(AllFilter, _localization.Get("AnalysisAll")),
            new(
                nameof(PackageAnalysisState.NotAnalyzed),
                _localization.Get("AnalysisNotAnalyzed")),
            new(
                nameof(PackageAnalysisState.Ready),
                _localization.Get("AnalysisReady")),
            new(
                nameof(PackageAnalysisState.RequiresSelection),
                _localization.Get("AnalysisRequiresSelection")),
            new(ProblemsFilter, _localization.Get("AnalysisProblems"))
        ];
        if (InstalledFilterChoices.Count == 0)
        {
            InstalledFilterChoices =
            [
                new(AllFilter, _localization.Get("InstalledFilterAll")),
                new("Installed", _localization.Get("InstalledFilterInstalled")),
                new("NotInstalled", _localization.Get("InstalledFilterNotInstalled"))
            ];
        }
        else
        {
            foreach (var choice in InstalledFilterChoices)
            {
                choice.Display = choice.Value switch
                {
                    "Installed" => _localization.Get("InstalledFilterInstalled"),
                    "NotInstalled" => _localization.Get("InstalledFilterNotInstalled"),
                    _ => _localization.Get("InstalledFilterAll")
                };
            }
        }
        OnPropertyChanged(nameof(FilterChoices));
        OnPropertyChanged(nameof(FilterDisplayChoices));
        OnPropertyChanged(nameof(AnalysisFilterChoices));
        OnPropertyChanged(nameof(InstalledFilterChoices));
        OnPropertyChanged(nameof(SelectedFilter));
        OnPropertyChanged(nameof(SelectedAnalysisFilter));
        OnPropertyChanged(nameof(SelectedInstalledFilter));
        OnPropertyChanged(nameof(SelectedFilterChoice));
        OnPropertyChanged(nameof(SelectedFilterDisplay));
        OnPropertyChanged(nameof(SelectedAnalysisFilterChoice));
        OnPropertyChanged(nameof(SelectedInstalledFilterChoice));
        RefreshGroupChoices();
    }

    private void RefreshGroupChoices()
    {
        GroupFilterChoices =
        [
            new(AllFilter, _localization.Get("GroupsAll")),
            new(UngroupedFilter, _localization.Get("Ungrouped")),
            .. Groups.Where(group => group.Id is not null).Select(group =>
                new ChoiceItem(
                    group.Id!.Value.ToString(CultureInfo.InvariantCulture),
                    group.Name))
        ];
        OnPropertyChanged(nameof(GroupFilterChoices));
        OnPropertyChanged(nameof(SelectedGroupFilter));
        OnPropertyChanged(nameof(SelectedGroupFilterChoice));
    }

    private void NotifySummaries()
    {
        OnPropertyChanged(nameof(PackageCount));
        OnPropertyChanged(nameof(FoundPackagesSummary));
        OnPropertyChanged(nameof(SkippedFoldersSummary));
        OnPropertyChanged(nameof(LastUpdatedSummary));
        OnPropertyChanged(nameof(PackageCountValue));
        OnPropertyChanged(nameof(SkippedDirectoryCountValue));
        OnPropertyChanged(nameof(LastUpdatedValue));
    }

    private void NotifyGroupCommands()
    {
        RenameGroupCommand.NotifyCanExecuteChanged();
        DeleteGroupCommand.NotifyCanExecuteChanged();
        MoveGroupUpCommand.NotifyCanExecuteChanged();
        MoveGroupDownCommand.NotifyCanExecuteChanged();
    }

    private sealed record PreparedPackageRow(
        OrganizerPackageRecord Record,
        LibraryModRecord Mod,
        LibraryGroupViewModel Group,
        IReadOnlyList<string> MissingDependencies);

    private sealed record LibraryReloadData(
        IReadOnlyList<LibraryGroupRecord> Groups,
        IReadOnlyList<LibraryModRecord> Mods,
        IReadOnlyDictionary<PackageId, IReadOnlyList<string>>
            MissingDependencies);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class LibraryGroupViewModel : ObservableObject
{
    private readonly Action<long?, bool> _expandedChanged;
    private readonly Func<LibraryGroupViewModel, bool?>
        _getBatchSelectionState;
    private readonly Action<LibraryGroupViewModel> _toggleBatchSelection;
    private readonly LocalizationService _localization;
    private string _name;
    private bool _isExpanded;

    public LibraryGroupViewModel(
        long? id,
        string name,
        int sortOrder,
        bool isExpanded,
        LocalizationService localization,
        Action<long?, bool> expandedChanged,
        Func<LibraryGroupViewModel, Task> create,
        Func<LibraryGroupViewModel, Task> rename,
        Func<LibraryGroupViewModel, Task> analyze,
        Func<LibraryGroupViewModel, Task> delete,
        Func<LibraryGroupViewModel, Task> moveUp,
        Func<LibraryGroupViewModel, Task> moveDown,
        Func<LibraryGroupViewModel, bool?> getBatchSelectionState,
        Action<LibraryGroupViewModel> toggleBatchSelection)
    {
        Id = id;
        _name = name;
        SortOrder = sortOrder;
        _isExpanded = isExpanded;
        _expandedChanged = expandedChanged;
        _getBatchSelectionState = getBatchSelectionState;
        _toggleBatchSelection = toggleBatchSelection;
        _localization = localization;
        CreateCommand = new AsyncRelayCommand(() => create(this));
        RenameCommand = new AsyncRelayCommand(
            () => rename(this),
            () => Id is not null);
        AnalyzeCommand = new AsyncRelayCommand(() => analyze(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this), () => Id is not null);
        MoveUpCommand = new AsyncRelayCommand(() => moveUp(this), () => Id is not null);
        MoveDownCommand = new AsyncRelayCommand(() => moveDown(this), () => Id is not null);
        ExpandCommand = new RelayCommand(() => IsExpanded = true);
        CollapseCommand = new RelayCommand(() => IsExpanded = false);
    }

    public long? Id { get; }
    public string Name => _name;
    public int SortOrder { get; }
    public string RenameLabel => _localization.Get("RenameGroup");
    public string AnalyzeLabel => _localization.Get("AnalyzeGroup");
    public string DeleteLabel => _localization.Get("DeleteGroup");
    public string MoveUpLabel => _localization.Get("MoveUp");
    public string MoveDownLabel => _localization.Get("MoveDown");
    public string ExpandLabel => _localization.Get("Expand");
    public string CollapseLabel => _localization.Get("Collapse");
    public string CreateLabel => _localization.Get("CreateGroupTitle");
    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand MoveUpCommand { get; }
    public AsyncRelayCommand MoveDownCommand { get; }
    public RelayCommand ExpandCommand { get; }
    public RelayCommand CollapseCommand { get; }
    public bool? BatchSelectionState => _getBatchSelectionState(this);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                _expandedChanged(Id, value);
        }
    }

    public void SetName(string name)
    {
        _name = name;
        OnPropertyChanged(nameof(Name));
    }

    public void ToggleBatchSelection() =>
        _toggleBatchSelection(this);

    public void RefreshBatchSelectionState() =>
        OnPropertyChanged(nameof(BatchSelectionState));

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(RenameLabel));
        OnPropertyChanged(nameof(AnalyzeLabel));
        OnPropertyChanged(nameof(DeleteLabel));
        OnPropertyChanged(nameof(MoveUpLabel));
        OnPropertyChanged(nameof(MoveDownLabel));
        OnPropertyChanged(nameof(ExpandLabel));
        OnPropertyChanged(nameof(CollapseLabel));
        OnPropertyChanged(nameof(CreateLabel));
    }

    public override string ToString() => Name;
}

public sealed class PackageRowViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly Func<PackageRowViewModel, Task> _save;
    private readonly Func<PackageRowViewModel, Task> _analyze;
    private readonly Func<PackageRowViewModel, Task> _reanalyze;
    private readonly Func<PackageRowViewModel, Task> _checkInstallation;
    private readonly Func<PackageRowViewModel, Task> _install;
    private readonly Func<PackageRowViewModel, Task> _remove;
    private readonly Func<PackageRowViewModel, Task> _deleteLibraryRecord;
    private readonly Func<PackageRowViewModel, Task>
        _deleteLibraryModFully;
    private readonly Func<PackageRowViewModel, Task>
        _checkNexusRelations;
    private readonly Func<PackageRowViewModel, bool>
        _canCheckNexusRelations;
    private readonly Func<PackageRowViewModel, string, Task> _selectRoot;
    private readonly Func<
        PackageRowViewModel,
        LibraryGroupViewModel,
        Task> _assignGroup;
    private readonly Action<PackageRowViewModel> _openCard;
    private readonly bool _installationAvailable;
    private readonly bool _removalAvailable;
    private readonly bool _fullDeletionAvailable;
    private IReadOnlyList<string> _missingDependencies;
    private string _editDisplayName;
    private string _editVersion;
    private string _note;
    private LibraryGroupViewModel _group;
    private PackageAnalysisState? _transientState;
    private bool _isBatchSelected;
    private readonly Func<RipperWorksSettings> _getSettings;
    private readonly Action<string>? _requestNavigateNexus;

    public event Action? RequestClose;

    public PackageRowViewModel(
        OrganizerPackageRecord stored,
        LibraryGroupViewModel group,
        IReadOnlyList<LibraryGroupViewModel> availableGroups,
        LocalizationService localization,
        Func<PackageRowViewModel, Task> save,
        Func<PackageRowViewModel, Task> analyze,
        Func<PackageRowViewModel, Task> reanalyze,
        Func<PackageRowViewModel, string, Task> selectRoot,
        Action<PackageRowViewModel> openCard,
        Func<PackageRowViewModel, LibraryGroupViewModel, Task> assignGroup,
        Func<PackageRowViewModel, Task> checkInstallation,
        Func<PackageRowViewModel, Task>? install = null,
        bool installationAvailable = false,
        Func<PackageRowViewModel, Task>? remove = null,
        bool removalAvailable = false,
        Func<PackageRowViewModel, Task>? deleteLibraryRecord = null,
        IReadOnlyList<string>? missingDependencies = null,
        LibraryModRecord? libraryMod = null,
        Func<PackageRowViewModel, Task>? deleteLibraryModFully = null,
        bool fullDeletionAvailable = false,
        Func<PackageRowViewModel, Task>? checkNexusRelations = null,
        Func<PackageRowViewModel, bool>? canCheckNexusRelations = null,
        Func<RipperWorksSettings>? getSettings = null,
        Action<string>? requestNavigateNexus = null)
    {
        _getSettings = getSettings ?? (() => new RipperWorksSettings());
        _requestNavigateNexus = requestNavigateNexus;
        LibraryMod = libraryMod ?? new LibraryModRecord
        {
            LibraryModId = stored.Package.LibraryModId ??
                new LibraryModId($"archive-{stored.Package.PackageId.Value}"),
            DisplayName = stored.EffectiveDisplayName,
            CustomDisplayName = stored.CustomDisplayName,
            Source = stored.Package.Source,
            GameDomain = stored.Package.GameDomain,
            NexusModId = stored.Package.NexusModId,
            Author = stored.Package.Author,
            Category = stored.Package.Category,
            NexusUrl = stored.Package.NexusUrl,
            GroupId = stored.GroupId,
            PreferredArchiveId = stored.Package.PackageId,
            Archives = [stored]
        };
        Stored = stored;
        Record = stored.Package;
        _group = group;
        AvailableGroups = availableGroups;
        _localization = localization;
        _save = save;
        _analyze = analyze;
        _reanalyze = reanalyze;
        _checkInstallation = checkInstallation;
        _selectRoot = selectRoot;
        _assignGroup = assignGroup;
        _openCard = openCard;
        _install = install ?? (_ => Task.CompletedTask);
        _remove = remove ?? (_ => Task.CompletedTask);
        _deleteLibraryRecord =
            deleteLibraryRecord ?? (_ => Task.CompletedTask);
        _deleteLibraryModFully =
            deleteLibraryModFully ?? (_ => Task.CompletedTask);
        _installationAvailable = installationAvailable;
        _removalAvailable = removalAvailable;
        _fullDeletionAvailable = fullDeletionAvailable;
        _checkNexusRelations =
            checkNexusRelations ?? (_ => Task.CompletedTask);
        _canCheckNexusRelations =
            canCheckNexusRelations ?? (_ => false);
        _missingDependencies = missingDependencies ?? [];
        _editDisplayName = LibraryMod.EffectiveDisplayName;
        _editVersion = stored.EffectiveVersion;
        _note = stored.Note;
        SaveCommand = new AsyncRelayCommand(() => _save(this));
        AnalyzeCommand = new AsyncRelayCommand(
            () => _analyze(this),
            () => IsPresent);
        ReanalyzeCommand = new AsyncRelayCommand(
            () => _reanalyze(this),
            () => IsPresent);
        CheckInstallationCommand = new AsyncRelayCommand(
            () => _checkInstallation(this),
            () => IsPresent);
        InstallCommand = new AsyncRelayCommand(
            ExecuteInstallAsync,
            () => _installationAvailable && CanInstall);
        RemoveModCommand = new AsyncRelayCommand(
            ExecuteRemoveAsync,
            () => _removalAvailable && CanRemove);
        DeleteLibraryRecordCommand = new AsyncRelayCommand(
            () => _deleteLibraryRecord(this),
            () => CanDeleteLibraryRecord);
        DeleteLibraryModFullyCommand = new AsyncRelayCommand(
            () => _deleteLibraryModFully(this),
            () => ShowFullDeleteCommand);
        OpenCardCommand = new RelayCommand(() => _openCard(this));
        CheckNexusRelationsCommand = new AsyncRelayCommand(
            () => _checkNexusRelations(this),
            () => _canCheckNexusRelations(this));
        OpenFolderCommand = new RelayCommand(OpenFolder);
        CopyPathCommand = new RelayCommand(
            () => System.Windows.Clipboard.SetText(ArchivePath));
        OpenNexusCommand = new RelayCommand(
            OpenNexus,
            () => NexusBrowserNavigationPolicy.IsValidOpenNexusWebUrl(
                Record.NexusUrl,
                out _));
        GroupAssignments = BuildGroupAssignments();
        RemoveGroupCommand = new AsyncRelayCommand(
            () => _assignGroup(
                this,
                AvailableGroups.Single(item => item.Id is null)),
            () => Group.Id is not null);
        RootChoices = BuildRootChoices();
    }

    public OrganizerPackageRecord Stored { get; private set; }
    public PackageRecord Record { get; private set; }
    public LibraryModRecord LibraryMod { get; private set; }
    public LibraryModId LibraryModId => LibraryMod.LibraryModId;
    public IReadOnlyList<OrganizerPackageRecord> Archives =>
        LibraryMod.Archives;
    public int ArchiveCount => Archives.Count;
    public int ArchiveFamilyCount => Archives
        .Select(archive => archive.Package.ArchiveFamilyKey ??
            $"archive:{archive.Package.PackageId.Value}")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
    public IReadOnlyList<LibraryGroupViewModel> AvailableGroups
        { get; private set; }
    public IReadOnlyList<GroupAssignmentChoiceViewModel> GroupAssignments
        { get; private set; }
    public IReadOnlyList<RootChoiceViewModel> RootChoices
        { get; private set; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand ReanalyzeCommand { get; }
    public AsyncRelayCommand CheckInstallationCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand RemoveModCommand { get; }
    public AsyncRelayCommand DeleteLibraryRecordCommand { get; }
    public AsyncRelayCommand DeleteLibraryModFullyCommand { get; }
    public RelayCommand OpenCardCommand { get; }
    public AsyncRelayCommand CheckNexusRelationsCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenNexusCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public AsyncRelayCommand RemoveGroupCommand { get; }
    public bool IsBatchSelected => _isBatchSelected;

    internal void SetBatchSelected(bool value) =>
        SetProperty(ref _isBatchSelected, value, nameof(IsBatchSelected));

    private Task ExecuteInstallAsync()
    {
        if (RequiresComponentChoice)
        {
            _openCard(this);
            return Task.CompletedTask;
        }
        return _install(this);
    }

    private Task ExecuteRemoveAsync()
    {
        if (RequiresComponentChoice)
        {
            _openCard(this);
            return Task.CompletedTask;
        }
        return _remove(this);
    }

    internal void RefreshFrom(
        OrganizerPackageRecord stored,
        LibraryModRecord libraryMod,
        LibraryGroupViewModel group,
        IReadOnlyList<LibraryGroupViewModel> availableGroups,
        IReadOnlyList<string> missingDependencies)
    {
        Stored = stored;
        Record = stored.Package;
        LibraryMod = libraryMod;
        _group = group;
        AvailableGroups = availableGroups;
        _missingDependencies = missingDependencies;
        _editDisplayName = libraryMod.EffectiveDisplayName;
        _editVersion = stored.EffectiveVersion;
        _note = stored.Note;
        _transientState = null;
        GroupAssignments = BuildGroupAssignments();
        RootChoices = BuildRootChoices();

        foreach (var property in GetType().GetProperties()
                     .Where(property =>
                         property.GetIndexParameters().Length == 0))
        {
            OnPropertyChanged(property.Name);
        }
        AnalyzeCommand.NotifyCanExecuteChanged();
        ReanalyzeCommand.NotifyCanExecuteChanged();
        CheckInstallationCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        RemoveModCommand.NotifyCanExecuteChanged();
        DeleteLibraryRecordCommand.NotifyCanExecuteChanged();
        DeleteLibraryModFullyCommand.NotifyCanExecuteChanged();
        RemoveGroupCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<GroupAssignmentChoiceViewModel>
        BuildGroupAssignments() =>
        AvailableGroups
            .Where(item => item.Id is not null)
            .Select(item => new GroupAssignmentChoiceViewModel(
                item.Name,
                () => _assignGroup(this, item)))
            .ToArray();

    private IReadOnlyList<RootChoiceViewModel> BuildRootChoices() =>
        Stored.Analysis?.DetectedRoots
            .Select(root => new RootChoiceViewModel(
                root,
                string.Equals(
                    root,
                    Stored.Analysis.SelectedRoot,
                    StringComparison.OrdinalIgnoreCase),
                () => _selectRoot(this, root)))
            .ToArray() ?? [];

    public string EditDisplayName
    {
        get => _editDisplayName;
        set
        {
            if (SetProperty(ref _editDisplayName, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string EditVersion
    {
        get => _editVersion;
        set
        {
            if (SetProperty(ref _editVersion, value))
                OnPropertyChanged(nameof(Version));
        }
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public LibraryGroupViewModel Group
    {
        get => _group;
        set
        {
            if (value is null)
                return;
            SetProperty(ref _group, value);
        }
    }

    public PackageAnalysisState AnalysisState =>
        _transientState ??
        OrganizerAnalysisService.GetEffectiveState(
            Record,
            Stored.Analysis);
    public PackageInstallationState InstallationState =>
        LibraryMod.InstallationState;
    public string InstallationIndicator => InstallationState switch
    {
        PackageInstallationState.Installed => "✓",
        PackageInstallationState.PartiallyInstalled => "!",
        PackageInstallationState.Unknown => "!",
        _ => string.Empty
    };
    public string InstallationToolTip =>
        _localization.Get($"Installation{InstallationState}");
    public bool HasMissingDependencies =>
        InstallationState == PackageInstallationState.Installed &&
        _missingDependencies.Count > 0;
    public string DependencyWarningIndicator =>
        HasMissingDependencies ? "⚠" : string.Empty;
    public string DependencyWarningToolTip => HasMissingDependencies
        ? string.Format(
            _localization.Get("DependencyMissingTooltip"),
            string.Join(", ", _missingDependencies))
        : string.Empty;
    public bool IsPresent => Stored.IsPresent;
    internal static bool IsArchiveInstallable(
        OrganizerPackageRecord archive) =>
        archive.IsPresent &&
        archive.Package.InstallationState ==
            PackageInstallationState.NotInstalled &&
        OrganizerAnalysisService.GetEffectiveState(
            archive.Package,
            archive.Analysis) == PackageAnalysisState.Ready &&
        archive.Analysis is not null &&
        ArchiveAnalyzer.IsCurrent(
            archive.Analysis,
            archive.Package) &&
        (archive.Analysis.DetectedRoots.Count <= 1 ||
         !string.IsNullOrWhiteSpace(
             archive.Analysis.SelectedRoot));
    private IReadOnlyList<OrganizerPackageRecord>
        InstallableArchives => LibraryMod.Archives
            .Where(IsArchiveInstallable)
            .ToArray();
    private IReadOnlyList<OrganizerPackageRecord>
        ComponentActionArchives => LibraryMod.Archives
            .Where(archive =>
                archive.Package.InstallationState ==
                    PackageInstallationState.Installed ||
                IsArchiveInstallable(archive) ||
                archive.IsPresent &&
                LibraryViewModel.RequiresInteractiveFomodSelection(
                    archive.Package.InstallationState,
                    archive.Analysis))
            .ToArray();
    internal bool RequiresComponentChoice =>
        ComponentActionArchives.Count > 1;
    internal PackageId? DirectInstallPackageId =>
        !RequiresComponentChoice &&
        InstallableArchives.Count == 1
            ? InstallableArchives[0].Package.PackageId
            : null;
    internal PackageId? DirectRemovePackageId =>
        !RequiresComponentChoice &&
        LibraryMod.InstalledArchives.Count == 1
            ? LibraryMod.InstalledArchives[0].Package.PackageId
            : null;
    public bool CanInstall =>
        LibraryMod.InstalledArchives.Count == 0 &&
        InstallableArchives.Count > 0;
    public bool CanRemove =>
        LibraryMod.InstalledArchives.Count > 0;
    public bool CanDeleteLibraryRecord =>
        !IsPresent &&
        InstallationState == PackageInstallationState.NotInstalled;
    public bool ShowFullDeleteCommand =>
        _fullDeletionAvailable && ArchiveCount > 0;
    public bool ShowInstallCommand =>
        InstallationState == PackageInstallationState.NotInstalled;
    public bool ShowRemoveCommand =>
        InstallationState == PackageInstallationState.Installed;
    public bool ShowRecoveryReason =>
        InstallationState is
            PackageInstallationState.PartiallyInstalled or
            PackageInstallationState.Unknown;
    public string AnalysisStateDisplay => IsPresent
        ? _localization.Get($"Analysis{AnalysisState}")
        : InstallationState == PackageInstallationState.Installed
            ? _localization.Get("InstalledArchiveMissing")
            : _localization.Get("FileMissing");
    public string AnalysisMessage =>
        Stored.Analysis?.ResultMessage ??
        (Stored.Analysis?.ResultCode is { Length: > 0 } code
            ? _localization.Get($"Result{code}")
            : string.Empty);
    public string DisplayName => string.IsNullOrWhiteSpace(EditDisplayName)
        ? LibraryMod.DisplayName
        : EditDisplayName;
    public string Version
    {
        get
        {
            if (ArchiveCount <= 1)
                return TextOrDash(EditVersion);
            var installed = LibraryMod.InstalledArchives
                .FirstOrDefault();
            if (ArchiveFamilyCount == 1)
            {
                return installed is not null
                    ? string.Format(
                        _localization.Get("LibraryInstalledVersionCount"),
                        TextOrDash(installed.EffectiveVersion),
                        ArchiveCount)
                    : string.Format(
                        _localization.Get("LibrarySelectedVersionCount"),
                        ArchiveCount,
                        TextOrDash(Stored.EffectiveVersion));
            }
            return installed is not null
                ? string.Format(
                    _localization.Get("LibraryInstalledArchiveCount"),
                    TextOrDash(installed.EffectiveVersion),
                    ArchiveCount)
                : string.Format(
                    _localization.Get("LibraryArchiveCount"),
                    ArchiveCount);
        }
    }
    public string VersionToolTip
    {
        get
        {
            var installed = LibraryMod.InstalledArchives
                .Select(archive => TextOrDash(archive.EffectiveVersion))
                .ToArray();
            return string.Format(
                _localization.Get("LibraryVersionTooltip"),
                installed.Length == 0
                    ? "—"
                    : string.Join(", ", installed),
                TextOrDash(Stored.EffectiveVersion),
                ArchiveCount,
                ArchiveFamilyCount);
        }
    }
    public string ArchiveFormat => Record.ArchiveFormat;
    public string Size => FormatSize(Record.FileSize);
    public string ArchivePath => Record.ArchivePath;
    public string Author => TextOrDash(LibraryMod.Author);
    public string Category => TextOrDash(LibraryMod.Category);
    public string NexusModId => LibraryMod.NexusModId?.ToString(
        CultureInfo.InvariantCulture) ?? "—";
    public string NexusFileId => Record.NexusFileId?.ToString(
        CultureInfo.InvariantCulture) ?? "—";
    public string NexusUrl => TextOrDash(LibraryMod.NexusUrl);
    public string Modified => Record.LastWriteUtc.ToLocalTime().ToString(
        "g",
        CultureInfo.GetCultureInfo(_localization.CurrentLanguage));
    public string OpenCardLabel => _localization.Get("OpenCard");
    public string CheckNexusRelationsLabel =>
        _localization.Get("CheckNexusRelations");
    public string AnalyzeLabel => _localization.Get("AnalyzeSelected");
    public string ReanalyzeLabel => _localization.Get("Reanalyze");
    public string CheckInstallationLabel =>
        _localization.Get("CheckInstallation");
    public string InstallLabel => _localization.Get(
        RequiresComponentChoice ? "Components" : "Install");
    public string RemoveModLabel =>
        _localization.Get(
            RequiresComponentChoice
                ? "Components"
                : "RemoveFromGame");
    public string DeleteLibraryModFullyLabel =>
        _localization.Get("LibraryModFullDelete");
    public string DeleteLibraryRecordLabel =>
        _localization.Get("DeleteLibraryRecord");
    public string RecoveryRequiredReason =>
        _localization.Get("InstallationStateRecoveryRequired");
    public string AssignGroupLabel => _localization.Get("AssignGroup");
    public string RemoveGroupLabel => _localization.Get("RemoveFromGroup");
    public string OpenFolderLabel => _localization.Get("OpenFolder");
    public string OpenNexusLabel => _localization.Get("OpenNexus");
    public string CopyPathLabel => _localization.Get("CopyPath");
    public string Source => LibraryMod.Source switch
    {
        PackageSource.Nexus => _localization.Get("SourceNexus"),
        PackageSource.Other => _localization.Get("SourceOther"),
        PackageSource.Manual => _localization.Get("SourceManual"),
        _ => _localization.Get("SourceLocal")
    };

    public void SetTransientState(PackageAnalysisState? state)
    {
        _transientState = state;
        OnPropertyChanged(nameof(AnalysisState));
        OnPropertyChanged(nameof(AnalysisStateDisplay));
    }

    public void SetInstallationState(PackageInstallationState state)
    {
        if (Record.InstallationState == state)
            return;
        Record = Record with { InstallationState = state };
        Stored = Stored with { Package = Record };
        LibraryMod = LibraryMod with
        {
            Archives = LibraryMod.Archives.Select(archive =>
                    archive.Package.PackageId == Record.PackageId
                        ? Stored
                        : archive)
                .ToArray()
        };
        OnPropertyChanged(nameof(InstallationState));
        OnPropertyChanged(nameof(InstallationIndicator));
        OnPropertyChanged(nameof(InstallationToolTip));
        OnPropertyChanged(nameof(HasMissingDependencies));
        OnPropertyChanged(nameof(DependencyWarningIndicator));
        OnPropertyChanged(nameof(DependencyWarningToolTip));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanDeleteLibraryRecord));
        OnPropertyChanged(nameof(ShowFullDeleteCommand));
        OnPropertyChanged(nameof(ShowInstallCommand));
        OnPropertyChanged(nameof(ShowRemoveCommand));
        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(RemoveModLabel));
        OnPropertyChanged(nameof(ShowRecoveryReason));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(VersionToolTip));
        InstallCommand.NotifyCanExecuteChanged();
        RemoveModCommand.NotifyCanExecuteChanged();
        DeleteLibraryRecordCommand.NotifyCanExecuteChanged();
        DeleteLibraryModFullyCommand.NotifyCanExecuteChanged();
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(AnalysisStateDisplay));
        OnPropertyChanged(nameof(AnalysisMessage));
        OnPropertyChanged(nameof(Modified));
        OnPropertyChanged(nameof(OpenCardLabel));
        OnPropertyChanged(nameof(CheckNexusRelationsLabel));
        OnPropertyChanged(nameof(AnalyzeLabel));
        OnPropertyChanged(nameof(ReanalyzeLabel));
        OnPropertyChanged(nameof(CheckInstallationLabel));
        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(RemoveModLabel));
        OnPropertyChanged(nameof(DeleteLibraryModFullyLabel));
        OnPropertyChanged(nameof(DeleteLibraryRecordLabel));
        OnPropertyChanged(nameof(RecoveryRequiredReason));
        OnPropertyChanged(nameof(InstallationToolTip));
        OnPropertyChanged(nameof(DependencyWarningToolTip));
        OnPropertyChanged(nameof(AssignGroupLabel));
        OnPropertyChanged(nameof(RemoveGroupLabel));
        OnPropertyChanged(nameof(OpenFolderLabel));
        OnPropertyChanged(nameof(OpenNexusLabel));
        OnPropertyChanged(nameof(CopyPathLabel));
    }

    private void OpenFolder()
    {
        var directory = Path.GetDirectoryName(Record.ArchivePath);
        if (directory is null)
            return;
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private void OpenNexus()
    {
        if (!NexusBrowserNavigationPolicy.IsValidOpenNexusWebUrl(Record.NexusUrl, out var uri) || uri is null)
            return;

        if (_getSettings().NexusBrowser == NexusBrowserMode.Internal &&
            _requestNavigateNexus is not null)
        {
            RequestClose?.Invoke();
            _requestNavigateNexus(uri.AbsoluteUri);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    private static string TextOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;
}

public sealed class RootChoiceViewModel
{
    public RootChoiceViewModel(
        string root,
        bool isSelected,
        Func<Task> select)
    {
        Root = root;
        IsSelected = isSelected;
        SelectCommand = new AsyncRelayCommand(select);
    }

    public string Root { get; }
    public bool IsSelected { get; }
    public AsyncRelayCommand SelectCommand { get; }
}

public sealed class GroupAssignmentChoiceViewModel
{
    public GroupAssignmentChoiceViewModel(
        string name,
        Func<Task> assign)
    {
        Name = name;
        AssignCommand = new AsyncRelayCommand(assign);
    }

    public string Name { get; }
    public AsyncRelayCommand AssignCommand { get; }
}
