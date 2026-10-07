using System.Globalization;
using System.IO;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Organizer;

namespace RipperWorks.App.ViewModels;

public sealed record ModDetailsData(
    PackageRowViewModel Mod,
    InstallPlan Plan,
    ModArchiveSwitchPlan? SwitchPlan,
    IReadOnlyList<InstalledFileRecord> InstalledFiles,
    IReadOnlyList<PackageRelationView> Relations,
    NexusModDetailsData Nexus);

public sealed record NexusModDetailsData(
    bool IsEligible,
    NexusRequirementRelations? Relations,
    bool LoadFailed);

public sealed class ModDetailsDialogViewModel : ObservableObject, IDisposable
{
    private LibraryModId _libraryModId => Mod.LibraryModId;
    private readonly LocalizationService _localization;
    private InstallPlan _plan = new()
    {
        PackageId = default
    };
    private ModArchiveSwitchPlan? _switchPlan;
    private IReadOnlyList<InstalledFileRecord> _installedFileRecords = [];
    private IReadOnlyList<PackageRelationView> _relationRecords = [];
    private NexusModDetailsData _nexus = new(false, null, false);
    private bool _isBusy;
    private string _loadError = string.Empty;
    private bool _disposed;
    private int _selectedTabIndex;
    private PackageRowViewModel _mod = null!;

    public ModDetailsDialogViewModel(
        LibraryViewModel library,
        PackageRowViewModel mod)
    {
        Library = library;
        Mod = mod;
        _localization = library.Localization;
        PrimaryCommand = new AsyncRelayCommand(
            ExecutePrimaryAsync,
            CanExecutePrimary);
        SaveCommand = new AsyncRelayCommand(
            () => ExecuteAndRefreshAsync(Mod.SaveCommand),
            () => !IsBusy);
        CheckInstallationCommand = new AsyncRelayCommand(
            () => ExecuteAndRefreshAsync(Mod.CheckInstallationCommand),
            () => !IsBusy && Mod.CheckInstallationCommand.CanExecute(null));
        RemoveFromGameCommand = new AsyncRelayCommand(
            RemoveFromGameAsync,
            () => !IsBusy && ExactRemovePackageId is not null);
        AddRelationCommand = new AsyncRelayCommand(
            AddRelationAsync,
            () => !IsBusy);
        NavigateBackCommand = new AsyncRelayCommand(
            NavigateBackAsync,
            () => !IsBusy && HasNavigationHistory);
        RefreshNexusRelationsCommand = new AsyncRelayCommand(
            RefreshNexusRelationsAsync,
            () => !IsBusy && _nexus.IsEligible);
        _localization.LanguageChanged += Localization_OnLanguageChanged;
        Library.RequestCloseModDetails += OnModRequestClose;
        RebuildReadOnlyRows();
    }

    private readonly Stack<PackageRowViewModel> _navigationHistory = new();

    public LibraryViewModel Library { get; }
    public PackageRowViewModel Mod
    {
        get => _mod;
        private set
        {
            if (ReferenceEquals(_mod, value))
                return;
            if (_mod is not null)
                _mod.RequestClose -= OnModRequestClose;
            _mod = value;
            if (_mod is not null)
                _mod.RequestClose += OnModRequestClose;
            OnPropertyChanged();
        }
    }

    public event Action? RequestClose;

    private void OnModRequestClose() => RequestClose?.Invoke();
    public AsyncRelayCommand PrimaryCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand CheckInstallationCommand { get; }
    public AsyncRelayCommand RemoveFromGameCommand { get; }
    public AsyncRelayCommand AddRelationCommand { get; }
    public AsyncRelayCommand NavigateBackCommand { get; }
    public AsyncRelayCommand RefreshNexusRelationsCommand { get; }
    public bool HasNavigationHistory => _navigationHistory.Count > 0;
    public string NavigationHistoryButtonText => _navigationHistory.Count > 0
        ? string.Format(_localization.Get("RelationNavigateBack"), _navigationHistory.Peek().DisplayName)
        : string.Empty;
    public bool IsNexusEligible => _nexus.IsEligible;
    public string RefreshNexusRelationsLabel => _localization.Get("NexusRelationsRefreshButton");
    public string OpenModDetailsActionText => _localization.Get("RelationActionOpenModDetails");
    public string OpenInDownloadsActionText => _localization.Get("RelationActionOpenInDownloads");
    public string OpenOnNexusActionText => _localization.Get("RelationActionOpenOnNexus");
    public string AddToDownloadsActionText => _localization.Get("RelationActionAddToDownloads");
    public string OpenExternalLinkActionText => _localization.Get("RelationActionOpenExternalLink");
    public string DismissRelationActionText => _localization.Get("RelationActionDismiss");
    public string CopyNameActionText => _localization.Get("RelationCopyName");
    public string CopyModIdActionText => _localization.Get("RelationCopyModId");
    public IReadOnlyList<ArchiveContentRowViewModel> ArchiveEntries
        { get; private set; } = [];
    public IReadOnlyList<LibraryArchiveRowViewModel> Archives
        { get; private set; } = [];
    public IReadOnlyList<ArchiveVersionActionViewModel> ArchiveVersionActions
        { get; private set; } = [];
    public IReadOnlyList<ModInstallPlanRowViewModel> PlanEntries
        { get; private set; } = [];
    public IReadOnlyList<InstalledFileRowViewModel> InstalledFiles
        { get; private set; } = [];
    public IReadOnlyList<ModDetailsRootChoiceViewModel> RootChoices
        { get; private set; } = [];
    public IReadOnlyList<ModRelationRowViewModel> OutgoingRelations
        { get; private set; } = [];
    public IReadOnlyList<ModRelationRowViewModel> IncomingRelations
        { get; private set; } = [];
    public string NexusRelationsStatus => BuildNexusRelationsStatus();
    public bool HasNexusRelationsStatus =>
        !string.IsNullOrWhiteSpace(NexusRelationsStatus);

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            NotifyCommands();
        }
    }

    public string LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
                OnPropertyChanged(nameof(HasProblemText));
        }
    }

    public string WindowTitle => _localization.Get("PackageDetails");
    public string GeneralTabLabel => _localization.Get("ModCardGeneral");
    public string ArchiveContentsTabLabel =>
        _localization.Get("ModCardArchiveContents");
    public string ArchivesTabLabel =>
        _localization.Get("ModCardArchivesAndVersions");
    public string InstallPlanTabLabel =>
        _localization.Get("ModCardInstallPlan");
    public string InstalledFilesTabLabel =>
        _localization.Get("ModCardInstalledFiles");
    public string ArchiveVersionColumnLabel =>
        _localization.Get("ColumnVersion");
    public string ArchiveFileIdColumnLabel =>
        _localization.Get("ModCardNexusFile");
    public string ArchiveComponentColumnLabel =>
        _localization.Get("ModCardComponent");
    public string ArchiveDateColumnLabel =>
        _localization.Get("ModCardDownloadedAt");
    public string ArchiveSizeColumnLabel =>
        _localization.Get("ColumnSize");
    public string ArchiveStateColumnLabel =>
        _localization.Get("ModCardState");
    public string ArchiveActionsColumnLabel =>
        _localization.Get("ModCardActions");
    public string BasicDataLabel => _localization.Get("ModCardBasicData");
    public string ArchiveLabel => _localization.Get("ModCardArchive");
    public string AnalysisLabel => _localization.Get("Analysis");
    public string RelationsLabel =>
        _localization.Get("ModCardRelationsAndDependencies");
    public string ThisModRequiresLabel =>
        _localization.Get("RelationThisModRequires");
    public string ThisModRequiresSectionHeader =>
        string.Format(_localization.Get("RelationThisModRequiresCount"), OutgoingRelations.Count);
    public string DependentsLabel =>
        _localization.Get("RelationDependents");
    public string DependentsSectionHeader =>
        string.Format(_localization.Get("RelationDependentsCount"), IncomingRelations.Count);
    public string DependentsSectionHint =>
        $"({_localization.Get("RelationDependentsHint")})";
    public bool HasOutgoingRelations => OutgoingRelations.Count > 0;
    public bool HasIncomingRelations => IncomingRelations.Count > 0;
    public bool ShowEmptyOutgoingRelations => !HasOutgoingRelations;
    public bool ShowEmptyIncomingRelations => !HasIncomingRelations;
    public string EmptyOutgoingRelationsText =>
        _localization.Get("RelationEmptyThisModRequires");
    public string EmptyIncomingRelationsText =>
        _localization.Get("RelationEmptyDependents");
    public string AddRelationLabel =>
        _localization.Get("RelationAdd");
    public string RemoveRelationLabel =>
        _localization.Get("RelationRemove");
    public string RelationNameColumnLabel =>
        _localization.Get("DisplayName");
    public string RelationTypeColumnLabel =>
        _localization.Get("RelationType");
    public string RelationSourceColumnLabel =>
        _localization.Get("ColumnSource");
    public string RelationStateColumnLabel =>
        _localization.Get("ModCardState");
    public string DisplayNameLabel => _localization.Get("DisplayName");
    public string VersionLabel => _localization.Get("ColumnVersion");
    public string AuthorLabel => _localization.Get("Author");
    public string CategoryLabel => _localization.Get("Category");
    public string GroupLabel => _localization.Get("Group");
    public string SourceLabel => _localization.Get("ColumnSource");
    public string NexusIdLabel => _localization.Get("ModCardNexusId");
    public string ArchiveNameLabel => _localization.Get("ModCardArchiveName");
    public string FullPathLabel => _localization.Get("ModCardFullPath");
    public string SizeLabel => _localization.Get("ColumnSize");
    public string ModifiedLabel => _localization.Get("Modified");
    public string FingerprintLabel => _localization.Get("ModCardFingerprint");
    public string AnalysisStateLabel =>
        _localization.Get("ModCardAnalysisState");
    public string LastAnalysisLabel =>
        _localization.Get("ModCardLastAnalysis");
    public string RootSectionLabel => HasMultipleRoots
        ? _localization.Get("ModCardChooseInstallRoot")
        : _localization.Get("ModCardInstallRoot");
    public string FoundFilesLabel => _localization.Get("ModCardFoundFiles");
    public string BlockedItemsLabel =>
        _localization.Get("ModCardBlockedItems");
    public string ArchivePathColumnLabel =>
        _localization.Get("ModCardArchivePath");
    public string TypeColumnLabel => _localization.Get("ModCardType");
    public string StateColumnLabel => _localization.Get("ModCardState");
    public string ReasonColumnLabel => _localization.Get("PlanReason");
    public string GamePathColumnLabel => _localization.Get("ModCardGamePath");
    public string ActionColumnLabel => _localization.Get("PlanAction");
    public string CurrentOwnerColumnLabel =>
        _localization.Get("ModCardCurrentOwner");
    public string InstalledHashColumnLabel =>
        _localization.Get("ModCardInstalledHash");
    public string PreviousFileColumnLabel =>
        _localization.Get("ModCardPreviousFile");
    public string PreviousHashColumnLabel =>
        _localization.Get("ModCardPreviousHash");
    public string CheckInstallationLabel =>
        _localization.Get("CheckInstallation");
    public string SaveLabel => _localization.Get("Save");
    public string OpenFolderLabel => _localization.Get("OpenFolder");
    public string OpenNexusLabel => _localization.Get("OpenNexus");
    public string CloseLabel => _localization.Get("Close");
    public string CopyPathLabel => _localization.Get("CopyPath");
    public string CopyHashLabel => _localization.Get("ModCardCopyHash");
    public string OpenArchiveFolderLabel =>
        _localization.Get("ModCardOpenArchiveFolder");
    public string SelectArchiveLabel =>
        _localization.Get("ModCardSelectArchive");

    public string HeaderTitle => Mod.DisplayName;
    public string HeaderVersion => string.Format(
        _localization.Get("ModCardVersionValue"),
        Mod.Version);
    public string HeaderGroup => Mod.Group.Name;
    public string HeaderSummary
    {
        get
        {
            var installed = Mod.LibraryMod.InstalledArchives;
            var selected = Mod.Stored;
            var parts = new List<string>();
            if (installed.Count == 0)
            {
                parts.Add(_localization.Get(
                    "ModCardHeaderNotInstalled"));
                parts.Add(string.Format(
                    _localization.Get("ModCardHeaderSelected"),
                    selected.EffectiveVersion));
            }
            else
            {
                var installedVersions = string.Join(
                    ", ",
                    installed.Select(archive =>
                        archive.EffectiveVersion));
                parts.Add(string.Format(
                    _localization.Get("ModCardHeaderInstalled"),
                    installedVersions));
                if (!installed.Any(archive =>
                        archive.Package.PackageId ==
                        selected.Package.PackageId))
                {
                    parts.Add(string.Format(
                        _localization.Get("ModCardHeaderSelected"),
                        selected.EffectiveVersion));
                }
            }
            parts.Add(string.Format(
                _localization.Get("ModCardHeaderArchives"),
                Mod.ArchiveCount));
            parts.Add(Mod.Group.Name);
            return string.Join(" · ", parts);
        }
    }
    public string ArchiveCount =>
        Mod.ArchiveCount.ToString(CultureInfo.InvariantCulture);
    public string ArchiveName => Path.GetFileName(Mod.ArchivePath);
    public string ArchivePath => Mod.ArchivePath;
    public string Fingerprint => Mod.Stored.Analysis?.Fingerprint ?? "—";
    public string ShortFingerprint => ShortHash(Fingerprint);
    public string AnalysisDate => Mod.Stored.Analysis is { } analysis
        ? analysis.AnalyzedAtUtc.ToLocalTime().ToString(
            "g",
            CultureInfo.GetCultureInfo(_localization.CurrentLanguage))
        : "—";
    public bool HasMultipleRoots =>
        Mod.Stored.Analysis?.DetectedRoots.Count > 1;
    public bool ShowResolvedRoot => !HasMultipleRoots;
    public string RootDisplay
    {
        get
        {
            var roots = Mod.Stored.Analysis?.DetectedRoots ?? [];
            return roots.Count == 1
                ? FormatRoot(roots[0])
                : _localization.Get("ModCardInstallRootUndefined");
        }
    }
    public int FoundFileCount =>
        Mod.Stored.Analysis?.InstallableFileCount ?? 0;
    public int BlockedItemCount => Mod.Stored.Analysis?.WarningCount ?? 0;

    public string StatusDisplay
    {
        get
        {
            if (!Mod.IsPresent)
                return _localization.Get("ModCardArchiveMissing");
            if (Mod.InstallationState == PackageInstallationState.Installed &&
                Mod.HasMissingDependencies)
            {
                return _localization.Get(
                    "InstallationInstalledMissingDependency");
            }
            return Mod.InstallationState switch
            {
                PackageInstallationState.Installed =>
                    _localization.Get("InstallationInstalled"),
                PackageInstallationState.PartiallyInstalled =>
                    _localization.Get("ModCardPartiallyInstalled"),
                PackageInstallationState.Unknown =>
                    _localization.Get("ModCardRequiresRecovery"),
                _ when Mod.AnalysisState == PackageAnalysisState.Ready =>
                    _localization.Get("ModCardReadyToInstall"),
                _ => Mod.AnalysisStateDisplay
            };
        }
    }

    public string StatusTone
    {
        get
        {
            if (!Mod.IsPresent)
                return "Error";
            if (Mod.InstallationState == PackageInstallationState.Installed)
                return Mod.HasMissingDependencies ? "Warning" : "Success";
            if (Mod.InstallationState ==
                PackageInstallationState.PartiallyInstalled)
            {
                return "Warning";
            }
            if (Mod.InstallationState == PackageInstallationState.Unknown)
                return "Error";
            return Mod.AnalysisState switch
            {
                PackageAnalysisState.Ready => "Success",
                PackageAnalysisState.Stale => "Warning",
                PackageAnalysisState.Blocked or PackageAnalysisState.Error =>
                    "Error",
                _ => "Neutral"
            };
        }
    }

    public string PrimaryActionLabel
    {
        get
        {
            if (RequiresArchiveSelection)
                return _localization.Get("ModCardChooseVersion");
            var archiveAction = Archives.FirstOrDefault(archive =>
                archive.IsSelected)?.PrimaryAction;
            if (!string.IsNullOrWhiteSpace(archiveAction) &&
                Mod.AnalysisState == PackageAnalysisState.Ready)
            {
                return archiveAction;
            }
            return Mod.AnalysisState switch
            {
                PackageAnalysisState.NotAnalyzed =>
                    _localization.Get("ModCardAnalyze"),
                PackageAnalysisState.Stale =>
                    _localization.Get("ModCardReanalyze"),
                PackageAnalysisState.Ready =>
                    string.Format(
                        _localization.Get("ModCardInstallVersion"),
                        Mod.Stored.EffectiveVersion),
                _ => _localization.Get("ModCardActionUnavailable")
            };
        }
    }
    public string RemoveFromGameLabel =>
        _localization.Get("RemoveFromGame");
    public bool ShowRemoveFromGame =>
        ExactRemovePackageId is not null;
    public bool ShowArchiveActionMenu => ArchiveVersionActions.Count > 1;
    public bool ShowDirectPrimaryAction => !ShowArchiveActionMenu;
    public string ArchiveMenuButtonLabel =>
        Mod.LibraryMod.InstalledArchives.Count > 0
            ? _localization.Get("ModCardVersionAction")
            : _localization.Get("Install");
    public LibraryArchiveRowViewModel? SelectedArchive =>
        Archives.FirstOrDefault(archive => archive.IsSelected);
    internal PackageId? ExactRemovePackageId =>
        SelectedArchive?.CanRemove == true
            ? SelectedArchive.ArchiveId
            : null;
    private bool RequiresArchiveSelection =>
        Mod.ArchiveCount > 1 &&
        (Mod.LibraryMod.PreferredArchiveId is not { } preferred ||
         !Mod.Archives.Any(archive =>
             archive.Package.PackageId == preferred));

    public string ProblemText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LoadError))
                return LoadError;
            if (Mod.InstallationState is
                PackageInstallationState.PartiallyInstalled or
                PackageInstallationState.Unknown)
            {
                return _localization.Get("InstallationStateRecoveryRequired");
            }
            if (!Mod.IsPresent)
                return _localization.Get("ModCardArchiveMissing");
            if (string.Equals(
                    Mod.Stored.Analysis?.ResultCode,
                    ArchiveAnalyzerResultCodes.FomodSelectionRequired,
                    StringComparison.Ordinal))
            {
                return _localization.Get(
                    "ModCardFomodSelectionRequiredPrompt");
            }
            if (!string.IsNullOrWhiteSpace(_switchPlan?.ErrorCode))
            {
                var localized = _localization.Get(
                    $"PlanError{_switchPlan.ErrorCode}");
                return localized.StartsWith(
                    "PlanError",
                    StringComparison.Ordinal)
                    ? _switchPlan.ErrorCode
                    : localized;
            }
            if (!string.IsNullOrWhiteSpace(_plan.ErrorCode))
            {
                var localized = _localization.Get(
                    $"PlanError{_plan.ErrorCode}");
                return localized.StartsWith(
                    "PlanError",
                    StringComparison.Ordinal)
                    ? _plan.ErrorDetail ?? _plan.ErrorCode
                    : localized;
            }
            return Mod.AnalysisMessage;
        }
    }

    public bool HasProblemText => !string.IsNullOrWhiteSpace(ProblemText);
    public int AddCount => _switchPlan?.FilesToAdd ?? _plan.AddCount;
    public int ReplaceCount =>
        _switchPlan?.FilesToReplace ?? _plan.ReplaceExistingCount;
    public int OverlayCount => _switchPlan is null ? _plan.OverlayCount : 0;
    public int ConflictCount =>
        _switchPlan?.ConflictCount ?? _plan.ConflictCount;
    public int BlockedCount =>
        _switchPlan?.BlockedCount ?? _plan.BlockedCount;
    public string AddSummaryLabel => _localization.Get("ModCardWillAdd");
    public string ReplaceSummaryLabel => _localization.Get("ModCardWillReplace");
    public string OverlaySummaryLabel => _localization.Get("ModCardWillOverlay");
    public string ConflictSummaryLabel => _localization.Get("ModCardConflicts");
    public string BlockedSummaryLabel => _localization.Get("ModCardBlocked");
    public bool HasInstalledFiles => InstalledFiles.Count > 0;
    public bool HasNoInstalledFiles => !HasInstalledFiles;
    public string NotInstalledTitle =>
        _localization.Get("ModCardNotInstalled");
    public string NotInstalledMessage =>
        _localization.Get("ModCardInstalledFilesHint");

    public async Task InitializeAsync() => await RefreshAsync();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
        Library.RequestCloseModDetails -= OnModRequestClose;
        if (_mod is not null)
            _mod.RequestClose -= OnModRequestClose;
    }

    private async Task RefreshAsync(bool ownsBusyState = true)
    {
        if (ownsBusyState)
            IsBusy = true;
        try
        {
            var data = await Library.LoadModDetailsDataAsync(_libraryModId);
            Mod = data.Mod;
            _plan = data.Plan;
            _switchPlan = data.SwitchPlan;
            _installedFileRecords = data.InstalledFiles;
            _relationRecords = data.Relations;
            _nexus = data.Nexus;
            LoadError = string.Empty;
            RebuildReadOnlyRows();
            NotifyAll();
        }
        catch (Exception exception)
        {
            LoadError = exception.Message;
            NotifyAll();
        }
        finally
        {
            if (ownsBusyState)
                IsBusy = false;
        }
    }

    private async Task RemoveFromGameAsync()
    {
        if (ExactRemovePackageId is not { } packageId)
            return;
        await RemoveExactPackageFromGameAsync(packageId);
    }

    private async Task RemoveExactPackageFromGameAsync(PackageId packageId)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            if (!Mod.RequiresComponentChoice &&
                Mod.DirectRemovePackageId ==
                packageId)
            {
                await Mod.RemoveModCommand.ExecuteAsync();
            }
            else
            {
                await Library.RemoveComponentAsync(
                    _libraryModId,
                    packageId);
            }
            await RefreshAsync(ownsBusyState: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteAndRefreshAsync(AsyncRelayCommand command)
    {
        if (!command.CanExecute(null))
            return;
        await command.ExecuteAsync();
        await RefreshAsync();
    }

    private async Task ExecutePrimaryAsync()
    {
        if (RequiresArchiveSelection)
        {
            SelectedTabIndex = 1;
            return;
        }
        var command = GetPrimaryCommand();
        if (command is null)
            return;
        await ExecuteAndRefreshAsync(command);
    }

    private async Task AddRelationAsync()
    {
        IsBusy = true;
        try
        {
            await Library.AddRelationFromDialogAsync(Mod.Record.PackageId);
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveRelationAsync(PackageRelationView relation)
    {
        IsBusy = true;
        try
        {
            await Library.RemoveRelationFromDialogAsync(relation);
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DismissNexusRelationAsync(NexusRequirementCanonicalKey key)
    {
        IsBusy = true;
        try
        {
            await Library.DismissNexusRelationAsync(key);
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AsyncRelayCommand? GetPrimaryCommand()
    {
        if (RequiresArchiveSelection)
            return null;
        if (Mod.InstallationState is
            PackageInstallationState.PartiallyInstalled or
            PackageInstallationState.Unknown)
        {
            return null;
        }
        var selectedArchive = Archives.FirstOrDefault(archive =>
            archive.IsSelected);
        return Mod.AnalysisState switch
        {
            PackageAnalysisState.NotAnalyzed => Mod.AnalyzeCommand,
            PackageAnalysisState.Stale => Mod.ReanalyzeCommand,
            PackageAnalysisState.Ready =>
                selectedArchive?.PrimaryCommand ?? Mod.InstallCommand,
            _ => null
        };
    }

    private bool CanExecutePrimary()
    {
        if (IsBusy)
            return false;
        if (RequiresArchiveSelection)
            return true;
        if (!Mod.IsPresent)
            return false;
        var command = GetPrimaryCommand();
        return command?.CanExecute(null) == true;
    }

    private void RebuildReadOnlyRows()
    {
        var hasExplicitSelection = !RequiresArchiveSelection;
        Archives = OrderArchives(Mod.LibraryMod)
            .Select(archive => new LibraryArchiveRowViewModel(
                archive,
                hasExplicitSelection &&
                archive.Package.PackageId == Mod.Record.PackageId,
                Mod.LibraryMod,
                _localization,
                () => SelectArchiveAsync(archive.Package.PackageId),
                () => ExecuteArchivePrimaryAsync(
                    archive.Package.PackageId),
                () => AnalyzeArchiveAsync(
                    archive.Package.PackageId,
                    false),
                () => AnalyzeArchiveAsync(
                    archive.Package.PackageId,
                    true),
                () => DeleteArchiveAsync(
                    archive.Package.PackageId),
                () => RemoveExactPackageFromGameAsync(
                    archive.Package.PackageId)))
            .ToArray();
        ArchiveVersionActions = Archives
            .Select(archive => new ArchiveVersionActionViewModel(
                archive.ArchiveId,
                archive.Version,
                archive.PrimaryAction,
                archive.PrimaryCommand))
            .ToArray();
        var analysisEntries = Mod.Stored.Analysis?.Entries ?? [];
        ArchiveEntries = analysisEntries
            .Select(entry => new ArchiveContentRowViewModel(
                entry,
                _localization))
            .ToArray();

        PlanEntries = _switchPlan is null
            ? _plan.Entries.Select(entry =>
            {
                var archivePath = FindArchivePath(
                    analysisEntries,
                    entry.RelativeGamePath);
                return new ModInstallPlanRowViewModel(
                    archivePath,
                    entry,
                    _localization);
            }).ToArray()
            : _switchPlan.Entries.Select(entry =>
                new ModInstallPlanRowViewModel(
                    FindArchivePath(
                        analysisEntries,
                        entry.RelativeGamePath),
                    entry,
                    _localization)).ToArray();

        InstalledFiles = _installedFileRecords
            .OrderBy(file => file.Sequence)
            .Select(file => new InstalledFileRowViewModel(
                file,
                _localization,
                Mod.Archives.FirstOrDefault(archive =>
                    archive.Package.PackageId == file.PackageId)
                    ?.EffectiveVersion))
            .ToArray();
        var outgoingList = new List<ModRelationRowViewModel>();
        var localOutgoingByTarget = new List<(
            PackageRelationView Local,
            LibraryModId? TargetLibModId,
            NexusModIdentity? TargetNexusId,
            OrganizerPackageRecord? TargetArchive)>();
        foreach (var relation in _relationRecords.Where(r =>
                     r.Relation.FromPackageId == Mod.Record.PackageId))
        {
            var targetPackageId = relation.Relation.ToPackageId;
            var targetArchive = Mod.Archives.FirstOrDefault(a =>
                a.Package.PackageId == targetPackageId);
            var targetRow = Library.Packages.FirstOrDefault(p =>
                p.Record.PackageId == targetPackageId ||
                p.Archives.Any(a => a.Package.PackageId == targetPackageId));
            var targetLibModId = targetRow?.LibraryModId ??
                targetArchive?.Package.LibraryModId;
            NexusModIdentity? targetNexusId = targetRow?.Record.NexusModId is > 0
                ? new NexusModIdentity(
                    NexusGameIdentityBridge.Cyberpunk2077NexusGameId,
                    targetRow.Record.NexusModId.Value)
                : targetArchive?.Package.NexusModId is > 0
                    ? new NexusModIdentity(
                        NexusGameIdentityBridge.Cyberpunk2077NexusGameId,
                        targetArchive.Package.NexusModId.Value)
                    : null;
            localOutgoingByTarget.Add((
                relation,
                targetLibModId,
                targetNexusId,
                targetArchive));
        }

        var matchedOutgoingLocal = new HashSet<int>();
        var nexusOutgoing = _nexus.Relations?.Forward.Relations ?? [];

        foreach (var nexusEdge in nexusOutgoing)
        {
            var nexusTargetLibModId = nexusEdge.LocalState.LibraryModId;
            var nexusTargetIdentity =
                NexusGameIdentityBridge.GetEffectiveTargetIdentity(
                    nexusEdge.Edge.Target);

            int matchIndex = -1;
            for (int i = 0; i < localOutgoingByTarget.Count; i++)
            {
                if (matchedOutgoingLocal.Contains(i))
                    continue;
                var local = localOutgoingByTarget[i];
                var canMergeProvenance =
                    local.Local.Relation.Source == PackageRelationSource.DetectedOverlap &&
                    local.Local.Relation.IsConfirmed;
                var matchesLibMod = nexusTargetLibModId is not null &&
                    local.TargetLibModId is not null &&
                    nexusTargetLibModId == local.TargetLibModId;
                var matchesNexusId = nexusTargetIdentity.HasValue &&
                    local.TargetNexusId.HasValue &&
                    nexusTargetIdentity.Value == local.TargetNexusId.Value;
                if (canMergeProvenance && (matchesLibMod || matchesNexusId))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex >= 0)
            {
                matchedOutgoingLocal.Add(matchIndex);
                var matchedLocal = localOutgoingByTarget[matchIndex];
                outgoingList.Add(ModRelationRowViewModel.CreateCombined(
                    localRelation: matchedLocal.Local,
                    nexusRelation: nexusEdge,
                    outgoing: true,
                    localization: _localization,
                    dialog: this,
                    targetArchive: matchedLocal.TargetArchive,
                    onRemoveLocal: () => RemoveRelationAsync(matchedLocal.Local),
                    onRemoveNexus: nexusEdge.Edge.CanonicalKey is { } key
                        ? () => DismissNexusRelationAsync(key)
                        : null));
            }
            else
            {
                outgoingList.Add(ModRelationRowViewModel.CreateCombined(
                    localRelation: null,
                    nexusRelation: nexusEdge,
                    outgoing: true,
                    localization: _localization,
                    dialog: this,
                    targetArchive: null,
                    onRemoveLocal: null,
                    onRemoveNexus: nexusEdge.Edge.CanonicalKey is { } key
                        ? () => DismissNexusRelationAsync(key)
                        : null));
            }
        }

        for (int i = 0; i < localOutgoingByTarget.Count; i++)
        {
            if (matchedOutgoingLocal.Contains(i))
                continue;
            var local = localOutgoingByTarget[i];
            outgoingList.Add(ModRelationRowViewModel.CreateCombined(
                localRelation: local.Local,
                nexusRelation: null,
                outgoing: true,
                localization: _localization,
                dialog: this,
                targetArchive: local.TargetArchive,
                onRemoveLocal: () => RemoveRelationAsync(local.Local),
                onRemoveNexus: null));
        }

        OutgoingRelations = outgoingList;

        var incomingList = new List<ModRelationRowViewModel>();
        var localIncomingBySource = new List<(
            PackageRelationView Local,
            LibraryModId? SourceLibModId,
            NexusModIdentity? SourceNexusId,
            OrganizerPackageRecord? SourceArchive)>();
        foreach (var relation in _relationRecords.Where(r =>
                     r.Relation.ToPackageId == Mod.Record.PackageId))
        {
            var sourcePackageId = relation.Relation.FromPackageId;
            var sourceArchive = Mod.Archives.FirstOrDefault(a =>
                a.Package.PackageId == sourcePackageId);
            var sourceRow = Library.Packages.FirstOrDefault(p =>
                p.Record.PackageId == sourcePackageId ||
                p.Archives.Any(a => a.Package.PackageId == sourcePackageId));
            var sourceLibModId = sourceRow?.LibraryModId ??
                sourceArchive?.Package.LibraryModId;
            NexusModIdentity? sourceNexusId = sourceRow?.Record.NexusModId is > 0
                ? new NexusModIdentity(
                    NexusGameIdentityBridge.Cyberpunk2077NexusGameId,
                    sourceRow.Record.NexusModId.Value)
                : sourceArchive?.Package.NexusModId is > 0
                    ? new NexusModIdentity(
                        NexusGameIdentityBridge.Cyberpunk2077NexusGameId,
                        sourceArchive.Package.NexusModId.Value)
                    : null;
            localIncomingBySource.Add((
                relation,
                sourceLibModId,
                sourceNexusId,
                sourceArchive));
        }

        var matchedIncomingLocal = new HashSet<int>();
        var nexusIncoming = _nexus.Relations?.Reverse.Relations ?? [];

        foreach (var nexusEdge in nexusIncoming)
        {
            var nexusSourceLibModId = nexusEdge.LocalState.LibraryModId;
            var nexusSourceIdentity = nexusEdge.Edge.Source;

            int matchIndex = -1;
            for (int i = 0; i < localIncomingBySource.Count; i++)
            {
                if (matchedIncomingLocal.Contains(i))
                    continue;
                var local = localIncomingBySource[i];
                var canMergeProvenance =
                    local.Local.Relation.Source == PackageRelationSource.DetectedOverlap &&
                    local.Local.Relation.IsConfirmed;
                var matchesLibMod = nexusSourceLibModId is not null &&
                    local.SourceLibModId is not null &&
                    nexusSourceLibModId == local.SourceLibModId;
                var matchesNexusId = local.SourceNexusId.HasValue &&
                    nexusSourceIdentity == local.SourceNexusId.Value;
                if (canMergeProvenance && (matchesLibMod || matchesNexusId))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex >= 0)
            {
                matchedIncomingLocal.Add(matchIndex);
                var matchedLocal = localIncomingBySource[matchIndex];
                incomingList.Add(ModRelationRowViewModel.CreateCombined(
                    localRelation: matchedLocal.Local,
                    nexusRelation: nexusEdge,
                    outgoing: false,
                    localization: _localization,
                    dialog: this,
                    targetArchive: matchedLocal.SourceArchive,
                    onRemoveLocal: () => RemoveRelationAsync(matchedLocal.Local),
                    onRemoveNexus: nexusEdge.Edge.CanonicalKey is { } key
                        ? () => DismissNexusRelationAsync(key)
                        : null));
            }
            else
            {
                incomingList.Add(ModRelationRowViewModel.CreateCombined(
                    localRelation: null,
                    nexusRelation: nexusEdge,
                    outgoing: false,
                    localization: _localization,
                    dialog: this,
                    targetArchive: null,
                    onRemoveLocal: null,
                    onRemoveNexus: nexusEdge.Edge.CanonicalKey is { } key
                        ? () => DismissNexusRelationAsync(key)
                        : null));
            }
        }

        for (int i = 0; i < localIncomingBySource.Count; i++)
        {
            if (matchedIncomingLocal.Contains(i))
                continue;
            var local = localIncomingBySource[i];
            incomingList.Add(ModRelationRowViewModel.CreateCombined(
                localRelation: local.Local,
                nexusRelation: null,
                outgoing: false,
                localization: _localization,
                dialog: this,
                targetArchive: local.SourceArchive,
                onRemoveLocal: () => RemoveRelationAsync(local.Local),
                onRemoveNexus: null));
        }

        IncomingRelations = incomingList;
        RootChoices = Mod.RootChoices
            .Select(choice => new ModDetailsRootChoiceViewModel(
                FormatRoot(choice.Root),
                choice.IsSelected,
                choice.SelectCommand))
            .ToArray();
    }

    private static IReadOnlyList<OrganizerPackageRecord> OrderArchives(
        LibraryModRecord mod)
    {
        if (mod.ArchiveLinks.Count == 0 || mod.Archives.Count < 2)
            return mod.Archives;

        var original = mod.Archives.ToArray();
        var byId = original.ToDictionary(
            archive => archive.Package.PackageId);
        var outgoing = original.ToDictionary(
            archive => archive.Package.PackageId,
            _ => new List<PackageId>());
        var indegree = original.ToDictionary(
            archive => archive.Package.PackageId,
            _ => 0);
        foreach (var link in mod.ArchiveLinks)
        {
            if (!byId.ContainsKey(link.FromArchiveId) ||
                !byId.ContainsKey(link.ToArchiveId) ||
                outgoing[link.FromArchiveId].Contains(link.ToArchiveId))
            {
                continue;
            }
            outgoing[link.FromArchiveId].Add(link.ToArchiveId);
            indegree[link.ToArchiveId]++;
        }

        var pending = new Queue<PackageId>(original
            .Select(archive => archive.Package.PackageId)
            .Where(id => indegree[id] == 0));
        var ordered = new List<OrganizerPackageRecord>(original.Length);
        var added = new HashSet<PackageId>();
        while (pending.TryDequeue(out var id))
        {
            if (!added.Add(id))
                continue;
            ordered.Add(byId[id]);
            foreach (var next in outgoing[id])
            {
                indegree[next]--;
                if (indegree[next] == 0)
                    pending.Enqueue(next);
            }
        }
        ordered.AddRange(original.Where(archive =>
            added.Add(archive.Package.PackageId)));
        return ordered;
    }

    private static string FindArchivePath(
        IReadOnlyList<ArchiveAnalysisEntry> analysisEntries,
        string relativeGamePath) =>
        analysisEntries.FirstOrDefault(candidate =>
            string.Equals(
                candidate.RelativeInstallPath,
                relativeGamePath,
                StringComparison.OrdinalIgnoreCase))?.OriginalPath ?? "—";

    private async Task SelectArchiveAsync(PackageId archiveId)
    {
        IsBusy = true;
        try
        {
            await Library.SelectArchiveAsync(_libraryModId, archiveId);
            await RefreshAsync();
            LibrarySelectionDiagnostics.Record(
                "SelectedArchiveChanged",
                _libraryModId.Value,
                $"archiveId={archiveId.Value}",
                verifyInvariants: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteArchivePrimaryAsync(PackageId archiveId)
    {
        IsBusy = true;
        try
        {
            await Library.ExecuteArchivePrimaryAsync(
                _libraryModId,
                archiveId);
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AnalyzeArchiveAsync(
        PackageId archiveId,
        bool force)
    {
        await Library.AnalyzeArchiveAsync(
            _libraryModId,
            archiveId,
            force);
        await RefreshAsync();
    }

    private async Task DeleteArchiveAsync(PackageId archiveId)
    {
        await Library.DeleteArchiveAsync(
            _libraryModId,
            archiveId);
        var stillExists = Library.Packages.Any(row =>
            row.LibraryModId == _libraryModId);
        if (stillExists)
            await RefreshAsync();
    }

    private string BuildNexusRelationsStatus()
    {
        if (!_nexus.IsEligible)
            return string.Empty;
        if (_nexus.LoadFailed)
            return _localization.Get("NexusRelationsLoadFailed");
        if (_nexus.Relations is not { } relations)
            return _localization.Get("NexusRelationsNotChecked");

        var forwardReady =
            relations.Forward.State?.HasSuccessfulSnapshot == true;
        var latestFailed =
            relations.Forward.State?.LatestFailure is not null;
        if (!forwardReady)
        {
            return _localization.Get(
                latestFailed
                    ? "NexusRelationsNotCheckedFailed"
                    : "NexusRelationsNotChecked");
        }
        if (latestFailed)
            return _localization.Get("NexusRelationsCachedFailure");
        return _localization.Get("NexusRelationsChecked");
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Mod));
        OnPropertyChanged(nameof(ArchiveEntries));
        OnPropertyChanged(nameof(Archives));
        OnPropertyChanged(nameof(ArchiveVersionActions));
        OnPropertyChanged(nameof(SelectedArchive));
        OnPropertyChanged(nameof(PlanEntries));
        OnPropertyChanged(nameof(InstalledFiles));
        OnPropertyChanged(nameof(OutgoingRelations));
        OnPropertyChanged(nameof(IncomingRelations));
        OnPropertyChanged(nameof(ThisModRequiresSectionHeader));
        OnPropertyChanged(nameof(DependentsSectionHeader));
        OnPropertyChanged(nameof(DependentsSectionHint));
        OnPropertyChanged(nameof(HasOutgoingRelations));
        OnPropertyChanged(nameof(HasIncomingRelations));
        OnPropertyChanged(nameof(ShowEmptyOutgoingRelations));
        OnPropertyChanged(nameof(ShowEmptyIncomingRelations));
        OnPropertyChanged(nameof(EmptyOutgoingRelationsText));
        OnPropertyChanged(nameof(EmptyIncomingRelationsText));
        OnPropertyChanged(nameof(NexusRelationsStatus));
        OnPropertyChanged(nameof(HasNexusRelationsStatus));
        OnPropertyChanged(nameof(IsNexusEligible));
        OnPropertyChanged(nameof(RefreshNexusRelationsLabel));
        OnPropertyChanged(nameof(HasNavigationHistory));
        OnPropertyChanged(nameof(NavigationHistoryButtonText));
        OnPropertyChanged(nameof(RootChoices));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderVersion));
        OnPropertyChanged(nameof(HeaderGroup));
        OnPropertyChanged(nameof(HeaderSummary));
        OnPropertyChanged(nameof(ArchiveCount));
        OnPropertyChanged(nameof(ArchiveName));
        OnPropertyChanged(nameof(ArchivePath));
        OnPropertyChanged(nameof(Fingerprint));
        OnPropertyChanged(nameof(ShortFingerprint));
        OnPropertyChanged(nameof(AnalysisDate));
        OnPropertyChanged(nameof(RootSectionLabel));
        OnPropertyChanged(nameof(HasMultipleRoots));
        OnPropertyChanged(nameof(ShowResolvedRoot));
        OnPropertyChanged(nameof(RootDisplay));
        OnPropertyChanged(nameof(FoundFileCount));
        OnPropertyChanged(nameof(BlockedItemCount));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(RemoveFromGameLabel));
        OnPropertyChanged(nameof(ShowRemoveFromGame));
        OnPropertyChanged(nameof(ShowArchiveActionMenu));
        OnPropertyChanged(nameof(ShowDirectPrimaryAction));
        OnPropertyChanged(nameof(ArchiveMenuButtonLabel));
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(HasProblemText));
        OnPropertyChanged(nameof(AddCount));
        OnPropertyChanged(nameof(ReplaceCount));
        OnPropertyChanged(nameof(OverlayCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(HasInstalledFiles));
        OnPropertyChanged(nameof(HasNoInstalledFiles));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        PrimaryCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        CheckInstallationCommand.NotifyCanExecuteChanged();
        RemoveFromGameCommand.NotifyCanExecuteChanged();
        AddRelationCommand.NotifyCanExecuteChanged();
        NavigateBackCommand.NotifyCanExecuteChanged();
        RefreshNexusRelationsCommand.NotifyCanExecuteChanged();
    }

    public async Task NavigateToModAsync(PackageRowViewModel targetPackage)
    {
        if (targetPackage is null || ReferenceEquals(targetPackage, Mod))
            return;

        _navigationHistory.Push(Mod);
        Mod = targetPackage;
        await RefreshAsync(ownsBusyState: true);
        OnPropertyChanged(nameof(Mod));
        OnPropertyChanged(nameof(HasNavigationHistory));
        OnPropertyChanged(nameof(NavigationHistoryButtonText));
        NavigateBackCommand.NotifyCanExecuteChanged();
    }

    public async Task NavigateBackAsync()
    {
        if (!_navigationHistory.TryPop(out var previous))
            return;

        Mod = previous;
        await RefreshAsync(ownsBusyState: true);
        OnPropertyChanged(nameof(Mod));
        OnPropertyChanged(nameof(HasNavigationHistory));
        OnPropertyChanged(nameof(NavigationHistoryButtonText));
        NavigateBackCommand.NotifyCanExecuteChanged();
    }

    public async Task RefreshNexusRelationsAsync()
    {
        if (IsBusy || !_nexus.IsEligible || Mod.LibraryMod.NexusModId is null)
            return;

        IsBusy = true;
        try
        {
            await Library.RefreshNexusRelationsForModAsync(Mod.LibraryMod);
            var data = await Library.LoadModDetailsDataAsync(Mod.LibraryModId);
            Mod = data.Mod;
            _plan = data.Plan;
            _switchPlan = data.SwitchPlan;
            _installedFileRecords = data.InstalledFiles;
            _relationRecords = data.Relations;
            _nexus = data.Nexus;
            LoadError = string.Empty;
            RebuildReadOnlyRows();
            NotifyAll();
        }
        catch (Exception exception)
        {
            LoadError = exception.Message;
            NotifyAll();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddToDownloadsAsync(ModRelationRowViewModel row)
    {
        if (row.TargetNexusIdentity is null || row.Availability != NexusRequirementLocalAvailability.Missing)
            return;

        IsBusy = true;
        try
        {
            var added = await Library.AddToDownloadsAsync(
                row.TargetNexusIdentity.Value,
                row.Name,
                row.SafeUrl);
            if (!added)
            {
                LoadError = _localization.Get("RelationAddToDownloadsFailed");
                NotifyAll();
                return;
            }

            var data = await Library.LoadModDetailsDataAsync(Mod.LibraryModId);
            Mod = data.Mod;
            _plan = data.Plan;
            _switchPlan = data.SwitchPlan;
            _installedFileRecords = data.InstalledFiles;
            _relationRecords = data.Relations;
            _nexus = data.Nexus;
            LoadError = string.Empty;
            RebuildReadOnlyRows();
            NotifyAll();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LoadError = exception.Message;
            NotifyAll();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenModDetailsForNexusIdentityAsync(
        LibraryModId? libraryModId,
        NexusModIdentity? nexusIdentity)
    {
        PackageRowViewModel? target = null;
        if (libraryModId.HasValue)
        {
            target = Library.Packages.FirstOrDefault(p =>
                p.LibraryModId == libraryModId.Value);
            if (target is not null && nexusIdentity.HasValue &&
                !LibraryViewModel.IsExactNexusLibraryMod(target.LibraryMod, nexusIdentity.Value))
            {
                target = null;
            }
        }

        if (target is null && nexusIdentity.HasValue)
        {
            var matches = Library.Packages.Where(p =>
                LibraryViewModel.IsExactNexusLibraryMod(p.LibraryMod, nexusIdentity.Value))
                .ToList();

            if (matches.Count == 1)
            {
                target = matches[0];
            }
        }

        if (target is not null)
        {
            await NavigateToModAsync(target);
        }
    }

    private void Localization_OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildReadOnlyRows();
        foreach (var property in GetType().GetProperties()
                     .Where(property =>
                         property.PropertyType == typeof(string)))
        {
            OnPropertyChanged(property.Name);
        }
        OnPropertyChanged(nameof(ArchiveEntries));
        OnPropertyChanged(nameof(Archives));
        OnPropertyChanged(nameof(ArchiveVersionActions));
        OnPropertyChanged(nameof(SelectedArchive));
        OnPropertyChanged(nameof(PlanEntries));
        OnPropertyChanged(nameof(InstalledFiles));
        OnPropertyChanged(nameof(OutgoingRelations));
        OnPropertyChanged(nameof(IncomingRelations));
        OnPropertyChanged(nameof(RootChoices));
    }

    internal static string ShortHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—")
            return "—";
        return value.Length <= 18
            ? value
            : $"{value[..10]}…{value[^7..]}";
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }

    private string FormatRoot(string? root) =>
        string.IsNullOrWhiteSpace(root) || root == "."
            ? _localization.Get("ModCardArchiveRoot")
            : root;
}

public sealed record ArchiveVersionActionViewModel(
    PackageId ArchiveId,
    string Version,
    string Label,
    AsyncRelayCommand Command);

public sealed class LibraryArchiveRowViewModel
{
    public LibraryArchiveRowViewModel(
        OrganizerPackageRecord archive,
        bool isSelected,
        LibraryModRecord libraryMod,
        LocalizationService localization,
        Func<Task> select,
        Func<Task> primary,
        Func<Task> verify,
        Func<Task> reanalyze,
        Func<Task> delete,
        Func<Task>? removeFromGame = null)
    {
        Archive = archive;
        IsSelected = isSelected;
        Component = BuildComponentDescriptor(
            archive.Package,
            localization);
        Version = string.IsNullOrWhiteSpace(archive.EffectiveVersion)
            ? "—"
            : archive.EffectiveVersion;
        FileId = archive.Package.NexusFileId?.ToString(
            CultureInfo.InvariantCulture) ?? "—";
        Date = (archive.Package.DownloadedAtUtc ??
                archive.LastIndexedAtUtc ??
                archive.Package.LastWriteUtc)
            .ToLocalTime()
            .ToString(
                "g",
                CultureInfo.GetCultureInfo(localization.CurrentLanguage));
        Size = ModDetailsDialogViewModel.FormatSize(
            archive.Package.FileSize);
        var isInstalled = archive.Package.InstallationState ==
            PackageInstallationState.Installed;
        CanInstall =
            PackageRowViewModel.IsArchiveInstallable(archive) ||
            archive.IsPresent &&
            LibraryViewModel.RequiresInteractiveFomodSelection(
                archive.Package.InstallationState,
                archive.Analysis);
        CanRemove = isInstalled;
        ShowRemoveFromGame = isInstalled && removeFromGame is not null;
        State = string.Join(
            " · ",
            new[]
            {
                isInstalled
                    ? localization.Get("ModCardArchiveInstalled")
                    : null,
                isSelected
                    ? localization.Get("ModCardArchiveSelected")
                    : null,
                !archive.IsPresent
                    ? localization.Get("ModCardArchiveMissing")
                    : !isInstalled
                        ? localization.Get("ModCardArchiveDownloaded")
                        : null,
                archive.Analysis?.State == PackageAnalysisState.Error
                    ? localization.Get("ModCardArchiveAnalysisError")
                    : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(State))
            State = localization.Get("ModCardArchiveDownloaded");
        var installedInFamily =
            libraryMod.InstalledArchives.FirstOrDefault(item =>
            string.Equals(
                item.Package.ArchiveFamilyKey,
                archive.Package.ArchiveFamilyKey,
                StringComparison.OrdinalIgnoreCase));
        var operation = installedInFamily is null
            ? ArchiveVersionOperation.Install
            : libraryMod.GetArchiveOperation(
                installedInFamily.Package.PackageId,
                archive.Package.PackageId);
        if (!isInstalled && !isSelected && installedInFamily is not null)
        {
            State += " · " + localization.Get(
                operation == ArchiveVersionOperation.Update
                    ? "ModCardArchiveUpdateAvailable"
                    : operation == ArchiveVersionOperation.Rollback
                        ? "ModCardArchivePreviousVersion"
                        : "ModCardArchiveDownloaded");
        }
        if (isInstalled)
        {
            PrimaryAction = Version == "—"
                ? localization.Get("ModCardReinstall")
                : string.Format(
                    localization.Get("ModCardReinstallVersion"),
                    Version);
        }
        else if (installedInFamily is null)
        {
            PrimaryAction = Version == "—"
                ? localization.Get("Install")
                : string.Format(
                    localization.Get("ModCardInstallVersion"),
                    Version);
        }
        else
        {
            PrimaryAction = operation == ArchiveVersionOperation.Update
                ? string.Format(
                    localization.Get("ModCardUpdateTo"),
                    Version)
                : operation == ArchiveVersionOperation.Rollback
                    ? string.Format(
                    localization.Get("ModCardRollbackTo"),
                    Version)
                    : Version == "—"
                        ? localization.Get("Install")
                        : string.Format(
                            localization.Get("ModCardInstallVersion"),
                            Version);
        }
        SelectCommand = new AsyncRelayCommand(select);
        PrimaryCommand = new AsyncRelayCommand(
            primary,
            () => isInstalled || CanInstall);
        VerifyCommand = new AsyncRelayCommand(verify);
        ReanalyzeCommand = new AsyncRelayCommand(reanalyze);
        DeleteCommand = new AsyncRelayCommand(delete);
        RemoveFromGameCommand = new AsyncRelayCommand(
            removeFromGame ?? (() => Task.CompletedTask),
            () => ShowRemoveFromGame);
        OpenFolderCommand = new RelayCommand(() =>
        {
            var folder = Path.GetDirectoryName(archive.Package.ArchivePath);
            if (folder is null)
                return;
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
        });
        CopyShaCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(archive.Package.Sha256))
            {
                System.Windows.Clipboard.SetText(
                    archive.Package.Sha256);
            }
        });
        SelectArchiveLabel = localization.Get("ModCardSelectArchive");
        OpenArchiveFolderLabel =
            localization.Get("ModCardOpenArchiveFolder");
        CopyShaLabel = localization.Get("ModCardCopyHash");
        VerifyLabel = localization.Get("VerifyArchive");
        ReanalyzeLabel = localization.Get("Reanalyze");
        DeleteArchiveLabel = localization.Get("DeleteArchive");
        RemoveFromGameLabel = localization.Get("RemoveFromGame");
    }

    public OrganizerPackageRecord Archive { get; }
    public PackageId ArchiveId => Archive.Package.PackageId;
    public string Component { get; }
    public string Version { get; }
    public string FileId { get; }
    public string Date { get; }
    public string Size { get; }
    public string State { get; private set; }
    public string PrimaryAction { get; }
    public bool CanInstall { get; }
    public bool CanRemove { get; }
    public bool ShowRemoveFromGame { get; }
    public string SelectArchiveLabel { get; }
    public string OpenArchiveFolderLabel { get; }
    public string CopyShaLabel { get; }
    public string VerifyLabel { get; }
    public string ReanalyzeLabel { get; }
    public string DeleteArchiveLabel { get; }
    public string RemoveFromGameLabel { get; }
    public bool IsSelected { get; }
    public AsyncRelayCommand SelectCommand { get; }
    public AsyncRelayCommand PrimaryCommand { get; }
    public AsyncRelayCommand VerifyCommand { get; }
    public AsyncRelayCommand ReanalyzeCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RemoveFromGameCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyShaCommand { get; }

    internal static string BuildComponentDescriptor(
        PackageRecord package,
        LocalizationService localization)
    {
        var name = new[]
        {
            package.ArchiveFileName,
            package.DisplayName,
            Path.GetFileName(package.ArchivePath)
        }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
            localization.Get("ModCardLocalArchive");
        return package.NexusFileId is { } fileId
            ? string.Format(
                localization.Get("ModCardNexusComponentDescriptor"),
                name,
                fileId)
            : name;
    }
}

public sealed record ModDetailsRootChoiceViewModel(
    string Display,
    bool IsSelected,
    AsyncRelayCommand SelectCommand);

public sealed class ModRelationRowViewModel
{
    private readonly ModDetailsDialogViewModel? _dialog;
    private readonly LocalizationService _localization;

    public ModRelationRowViewModel(
        PackageRelationView relation,
        bool outgoing,
        LocalizationService localization,
        Func<Task> remove,
        ModDetailsDialogViewModel? dialog = null,
        OrganizerPackageRecord? targetArchive = null)
    {
        _dialog = dialog;
        _localization = localization;
        ContentAccess = NexusAdultContentAccess.Normal;
        AdultContentBadge = null;
        Relation = relation;
        if (targetArchive is not null)
        {
            Name = LibraryArchiveRowViewModel.BuildComponentDescriptor(
                targetArchive.Package,
                localization);
        }
        else
        {
            Name = outgoing
                ? relation.ToDisplayName
                : relation.FromDisplayName;
        }
        Type = localization.Get(
            relation.Relation.RelationType ==
            PackageRelationType.AddOnOf
                ? "RelationTypeAddOnOf"
                : "RelationTypeRequires");
        Source = localization.Get(
            $"RelationSource{relation.Relation.Source}");
        var state = outgoing
            ? relation.ToInstallationState
            : relation.FromInstallationState;
        var archivePresent = outgoing
            ? relation.ToArchivePresent
            : relation.FromArchivePresent;
        State = state == PackageInstallationState.Installed
            ? localization.Get("InstallationInstalled")
            : archivePresent
                ? localization.Get("InstallationNotInstalled")
                : localization.Get("ModCardArchiveMissing");
        Notes = null;
        Availability = null;
        SafeUrl = null;
        TargetLibraryModId = null;
        TargetNexusIdentity = null;

        var targetPackageId = outgoing
            ? relation.Relation.ToPackageId
            : relation.Relation.FromPackageId;

        CanOpenModDetails = _dialog?.Library.Packages.Any(p => p.Record.PackageId == targetPackageId) == true;
        OpenModDetailsCommand = new AsyncRelayCommand(async () =>
        {
            if (_dialog is not null)
            {
                var target = _dialog.Library.Packages.FirstOrDefault(p => p.Record.PackageId == targetPackageId);
                if (target is not null)
                    await _dialog.NavigateToModAsync(target);
            }
        }, () => CanOpenModDetails);

        CanOpenInDownloads = false;
        OpenInDownloadsCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        CanOpenOnNexus = false;
        OpenOnNexusCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        CanAddToDownloads = false;
        AddToDownloadsCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        CanOpenExternalLink = false;
        OpenExternalLinkCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);

        RemoveCommand = new AsyncRelayCommand(remove);
        CanRemove = true;

        CopyNameCommand = new RelayCommand(() =>
        {
            try { System.Windows.Clipboard.SetText(Name); } catch { }
        });
        CopyModIdCommand = new RelayCommand(() => { }, () => false);
    }

    private ModRelationRowViewModel(
        string name,
        string state,
        string? notes,
        NexusRequirementLocalAvailability? availability,
        string? safeUrl,
        LibraryModId? targetLibraryModId,
        NexusModIdentity? targetNexusIdentity,
        NexusAdultContentAccess contentAccess,
        bool isOutgoing,
        LocalizationService localization,
        ModDetailsDialogViewModel? dialog,
        Func<Task>? onRemove)
    {
        _dialog = dialog;
        _localization = localization;
        Relation = null;
        Name = name;
        Type = localization.Get("RelationTypeRequires");
        Source = localization.Get("RelationSourceNexus");
        State = state;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Availability = availability;
        SafeUrl = safeUrl;
        TargetLibraryModId = targetLibraryModId;
        TargetNexusIdentity = targetNexusIdentity;
        ContentAccess = contentAccess;
        AdultContentBadge =
            contentAccess == NexusAdultContentAccess.AdultAllowed
                ? localization.Get("NexusAdultBadge")
                : null;
        var contentRestricted = contentAccess.IsRestricted();

        CanOpenModDetails = !contentRestricted &&
            availability is (
                NexusRequirementLocalAvailability.Installed or
                NexusRequirementLocalAvailability.InLibrary);
        OpenModDetailsCommand = new AsyncRelayCommand(async () =>
        {
            if (_dialog is not null)
                await _dialog.OpenModDetailsForNexusIdentityAsync(TargetLibraryModId, TargetNexusIdentity);
        }, () => CanOpenModDetails);

        CanOpenInDownloads = !contentRestricted &&
            availability == NexusRequirementLocalAvailability.InDownloads &&
            TargetNexusIdentity.HasValue;
        OpenInDownloadsCommand = new AsyncRelayCommand(() =>
        {
            if (_dialog is not null && TargetNexusIdentity.HasValue)
                _dialog.Library.WindowService.NavigateToDownloads(TargetNexusIdentity.Value);
            return Task.CompletedTask;
        }, () => CanOpenInDownloads);

        CanOpenOnNexus = !contentRestricted &&
            availability == NexusRequirementLocalAvailability.Missing &&
            NexusBrowserNavigationPolicy.IsValidOpenNexusWebUrl(SafeUrl, out _);
        OpenOnNexusCommand = new AsyncRelayCommand(() =>
        {
            if (_dialog is not null && !string.IsNullOrWhiteSpace(SafeUrl) && Uri.TryCreate(SafeUrl, UriKind.Absolute, out var uri))
                _dialog.Library.OpenNexusWebUrl(uri);
            return Task.CompletedTask;
        }, () => CanOpenOnNexus);

        CanAddToDownloads = !contentRestricted && isOutgoing &&
            availability == NexusRequirementLocalAvailability.Missing &&
            TargetNexusIdentity.HasValue;
        AddToDownloadsCommand = new AsyncRelayCommand(async () =>
        {
            if (_dialog is not null)
                await _dialog.AddToDownloadsAsync(this);
        }, () => CanAddToDownloads);

        CanOpenExternalLink = !contentRestricted &&
            availability == NexusRequirementLocalAvailability.External &&
            !string.IsNullOrWhiteSpace(SafeUrl) &&
            (SafeUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || SafeUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        OpenExternalLinkCommand = new AsyncRelayCommand(() =>
        {
            if (_dialog is not null && !string.IsNullOrWhiteSpace(SafeUrl) && Uri.TryCreate(SafeUrl, UriKind.Absolute, out var uri))
                _dialog.Library.WindowService.OpenExternalUrl(uri);
            return Task.CompletedTask;
        }, () => CanOpenExternalLink);

        if (onRemove is not null)
        {
            RemoveCommand = new AsyncRelayCommand(onRemove);
            CanRemove = true;
        }
        else
        {
            RemoveCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
            CanRemove = false;
        }

        CopyNameCommand = new RelayCommand(() =>
        {
            try { System.Windows.Clipboard.SetText(Name); } catch { }
        });
        CopyModIdCommand = new RelayCommand(() =>
        {
            if (TargetNexusIdentity.HasValue)
            {
                try { System.Windows.Clipboard.SetText(TargetNexusIdentity.Value.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            }
        }, () => !contentRestricted && TargetNexusIdentity.HasValue);
    }

    public static ModRelationRowViewModel FromNexus(
        NexusRequirementProjectedEdge relation,
        bool outgoing,
        LocalizationService localization,
        ModDetailsDialogViewModel? dialog = null,
        Func<Task>? onRemove = null)
    {
        var restricted = relation.ContentAccess.IsRestricted();
        var name = restricted
            ? RestrictedTitle(relation.ContentAccess, localization)
            : outgoing
                ? BuildForwardName(relation.Edge.Target, localization)
                : FirstNonEmpty(
                    relation.Edge.SourceMetadata?.DisplayName,
                    relation.LocalState.DisplayName,
                    string.Format(
                        localization.Get("NexusModFallback"),
                        relation.Edge.Source.ModId));
        var state = restricted
            ? localization.Get("NexusContentRestrictedStatus")
            : localization.Get(
            relation.LocalState.Availability switch
            {
                NexusRequirementLocalAvailability.Installed =>
                    "InstallationInstalled",
                NexusRequirementLocalAvailability.InLibrary =>
                    "NexusRelationStateInLibrary",
                NexusRequirementLocalAvailability.InDownloads =>
                    "NexusRelationStateInDownloads",
                NexusRequirementLocalAvailability.Missing =>
                    "NexusRelationStateMissing",
                NexusRequirementLocalAvailability.External =>
                    "NexusRelationStateExternal",
                _ => throw new InvalidOperationException(
                    "Unknown Nexus relation availability.")
            });

        string? safeUrl = restricted
            ? null
            : outgoing
            ? (relation.Edge.Target is NexusModRequirementTarget nm
                ? (nm.ClickableUrl?.ToString() ?? nm.ProviderUrl)
                : (relation.Edge.Target is NexusExternalRequirementTarget ex
                    ? (ex.ClickableUrl?.ToString() ?? ex.ProviderUrl)
                    : null))
            : (relation.Edge.SourceMetadata?.ClickableUrl?.ToString() ?? relation.Edge.SourceMetadata?.ProviderUrl);

        NexusModIdentity? targetNexusIdentity = restricted
            ? null
            : outgoing
                ? NexusGameIdentityBridge.GetEffectiveTargetIdentity(
                    relation.Edge.Target)
                : relation.Edge.Source;

        return new(
            name,
            state,
            restricted
                ? RestrictedDescription(relation.ContentAccess, localization)
                : relation.Edge.Notes,
            relation.LocalState.Availability,
            safeUrl,
            restricted ? null : relation.LocalState.LibraryModId,
            targetNexusIdentity,
            relation.ContentAccess,
            outgoing,
            localization,
            dialog,
            onRemove);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();

    private static string BuildForwardName(
        NexusRequirementTarget target,
        LocalizationService localization) => target switch
    {
        NexusModRequirementTarget nexus => FirstNonEmpty(
            nexus.DisplayName,
            string.Format(
                localization.Get("NexusModFallback"),
                nexus.Identity.ModId)),
        NexusExternalRequirementTarget external => FirstNonEmpty(
            external.DisplayName,
            external.ProviderUrl,
            localization.Get("NexusExternalRequirement")),
        _ => localization.Get("NexusExternalRequirement")
    };

    public static ModRelationRowViewModel CreateCombined(
        PackageRelationView? localRelation,
        NexusRequirementProjectedEdge? nexusRelation,
        bool outgoing,
        LocalizationService localization,
        ModDetailsDialogViewModel? dialog = null,
        OrganizerPackageRecord? targetArchive = null,
        Func<Task>? onRemoveLocal = null,
        Func<Task>? onRemoveNexus = null)
    {
        if (localRelation is null && nexusRelation is not null)
        {
            return FromNexus(nexusRelation, outgoing, localization, dialog, onRemoveNexus);
        }
        if (localRelation is not null && nexusRelation is null)
        {
            return new ModRelationRowViewModel(
                localRelation,
                outgoing,
                localization,
                onRemoveLocal ?? (() => Task.CompletedTask),
                dialog,
                targetArchive);
        }
        if (localRelation is null && nexusRelation is null)
        {
            throw new ArgumentException("At least one relation must be provided.");
        }

        var restricted = nexusRelation!.ContentAccess.IsRestricted();
        var name = restricted
            ? RestrictedTitle(nexusRelation.ContentAccess, localization)
            : targetArchive is not null
                ? LibraryArchiveRowViewModel.BuildComponentDescriptor(
                    targetArchive.Package,
                    localization)
                : (outgoing
                    ? localRelation!.ToDisplayName
                    : localRelation!.FromDisplayName);

        var type = localization.Get(
            localRelation!.Relation.RelationType == PackageRelationType.AddOnOf
                ? "RelationTypeAddOnOf"
                : "RelationTypeRequires");

        var source = FormatRelationSource(
            hasNexus: true,
            localRelation.Relation,
            localization);

        var state = outgoing
            ? localRelation.ToInstallationState
            : localRelation.FromInstallationState;
        var archivePresent = outgoing
            ? localRelation.ToArchivePresent
            : localRelation.FromArchivePresent;
        var stateText = restricted
            ? localization.Get("NexusContentRestrictedStatus")
            : state == PackageInstallationState.Installed
                ? localization.Get("InstallationInstalled")
                : archivePresent
                    ? localization.Get("InstallationNotInstalled")
                    : localization.Get("ModCardArchiveMissing");

        string? safeUrl = restricted
            ? null
            : outgoing
            ? (nexusRelation!.Edge.Target is NexusModRequirementTarget nm
                ? (nm.ClickableUrl?.ToString() ?? nm.ProviderUrl)
                : (nexusRelation!.Edge.Target is NexusExternalRequirementTarget ex
                    ? (ex.ClickableUrl?.ToString() ?? ex.ProviderUrl)
                    : null))
            : (nexusRelation!.Edge.SourceMetadata?.ClickableUrl?.ToString() ??
               nexusRelation!.Edge.SourceMetadata?.ProviderUrl);

        NexusModIdentity? targetNexusIdentity = restricted
            ? null
            : outgoing
                ? NexusGameIdentityBridge.GetEffectiveTargetIdentity(
                    nexusRelation.Edge.Target)
                : nexusRelation.Edge.Source;

        var targetLibraryModId = restricted
            ? null
            : nexusRelation.LocalState.LibraryModId ??
                targetArchive?.Package.LibraryModId;

        var removeAction = onRemoveLocal ?? onRemoveNexus;

        return new ModRelationRowViewModel(
            name,
            stateText,
            restricted
                ? RestrictedDescription(
                    nexusRelation.ContentAccess,
                    localization)
                : nexusRelation.Edge.Notes,
            nexusRelation.LocalState.Availability,
            safeUrl,
            targetLibraryModId,
            targetNexusIdentity,
            nexusRelation.ContentAccess,
            outgoing,
            localization,
            dialog,
            removeAction)
        {
            Relation = localRelation,
            Type = type,
            Source = source
        };
    }

    private static string FormatRelationSource(
        bool hasNexus,
        PackageRelationRecord? localRelation,
        LocalizationService localization)
    {
        var parts = new List<string>();
        if (hasNexus)
        {
            parts.Add(localization.Get("RelationSourceNexus"));
        }
        if (localRelation is not null)
        {
            if (localRelation.Source == PackageRelationSource.DetectedOverlap)
            {
                parts.Add(localization.Get(localRelation.IsConfirmed
                    ? "RelationSourceConfirmedOverlap"
                    : "RelationSourceDetectedOverlap"));
            }
            else
            {
                parts.Add(localization.Get($"RelationSource{localRelation.Source}"));
            }
        }
        return string.Join(", ", parts);
    }

    private static string RestrictedTitle(
        NexusAdultContentAccess access,
        LocalizationService localization) => localization.Get(
            access == NexusAdultContentAccess.AdultRestricted
                ? "NexusAdultRestrictedTitle"
                : "NexusContentUnavailableTitle");

    private static string RestrictedDescription(
        NexusAdultContentAccess access,
        LocalizationService localization) => localization.Get(
            access == NexusAdultContentAccess.AdultRestricted
                ? "NexusAdultRestrictedDescription"
                : "NexusContentUnavailableDescription");

    public PackageRelationView? Relation { get; internal set; }
    public string Name { get; }
    public string Type { get; internal set; }
    public string Source { get; internal set; }
    public string State { get; }
    public string? Notes { get; }
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public NexusRequirementLocalAvailability? Availability { get; }
    public string? SafeUrl { get; }
    public LibraryModId? TargetLibraryModId { get; }
    public NexusModIdentity? TargetNexusIdentity { get; }
    public NexusAdultContentAccess ContentAccess { get; }
    public bool IsContentRestricted => ContentAccess.IsRestricted();
    public string? AdultContentBadge { get; }
    public bool HasAdultContentBadge =>
        !string.IsNullOrWhiteSpace(AdultContentBadge);
    public bool HasTargetNexusIdentity =>
        !IsContentRestricted && TargetNexusIdentity.HasValue;

    public bool CanRemove { get; }
    public AsyncRelayCommand RemoveCommand { get; }

    public bool CanOpenModDetails { get; }
    public AsyncRelayCommand OpenModDetailsCommand { get; }

    public bool CanOpenInDownloads { get; }
    public AsyncRelayCommand OpenInDownloadsCommand { get; }

    public bool CanOpenOnNexus { get; }
    public AsyncRelayCommand OpenOnNexusCommand { get; }

    public bool CanAddToDownloads { get; }
    public AsyncRelayCommand AddToDownloadsCommand { get; }

    public bool CanOpenExternalLink { get; }
    public AsyncRelayCommand OpenExternalLinkCommand { get; }

    public RelayCommand CopyNameCommand { get; }
    public RelayCommand CopyModIdCommand { get; }

    public string OpenModDetailsActionText => _localization.Get("RelationActionOpenModDetails");
    public string OpenInDownloadsActionText => _localization.Get("RelationActionOpenInDownloads");
    public string OpenOnNexusActionText => _localization.Get("RelationActionOpenOnNexus");
    public string AddToDownloadsActionText => _localization.Get("RelationActionAddToDownloads");
    public string OpenExternalLinkActionText => _localization.Get("RelationActionOpenExternalLink");
    public string DismissRelationActionText => _localization.Get("RelationActionDismiss");
    public string CopyNameActionText => _localization.Get("RelationCopyName");
    public string CopyModIdActionText => _localization.Get("RelationCopyModId");

    public bool HasContextActions => CanOpenModDetails || CanOpenInDownloads || CanOpenOnNexus || CanAddToDownloads || CanOpenExternalLink || CanRemove;
}

public sealed class ArchiveContentRowViewModel
{
    public ArchiveContentRowViewModel(
        ArchiveAnalysisEntry entry,
        LocalizationService localization)
    {
        Path = entry.OriginalPath;
        Size = entry.IsDirectory
            ? "—"
            : ModDetailsDialogViewModel.FormatSize(entry.EntrySize);
        var classification = Classify(entry);
        Type = localization.Get(
            entry.IsDirectory ? "ModCardDirectory" : "ModCardFile");
        State = localization.Get($"ModCardEntry{classification}");
        Tone = classification switch
        {
            "Installable" => "Success",
            "Dangerous" => "Error",
            "Blocked" => "Error",
            "Excluded" => "Warning",
            "Unsupported" => "Warning",
            _ => "Neutral"
        };
        Reason = LocalizeReason(entry.WarningCode, localization);
    }

    public string Path { get; }
    public string Size { get; }
    public string Type { get; }
    public string State { get; }
    public string Reason { get; }
    public string Tone { get; }

    private static string Classify(ArchiveAnalysisEntry entry)
    {
        if (string.Equals(
                entry.WarningCode,
                ArchiveAnalyzerResultCodes.FomodSelectionRequired,
                StringComparison.Ordinal))
        {
            return "WaitingForSelection";
        }
        if (entry.IsDirectory)
            return "Directory";
        if (entry.IsInstallable)
            return "Installable";
        if (entry.WarningCode is
            "AbsolutePath" or "PathTraversal" or "LinkEntry" or "EmptyPath")
        {
            return "Dangerous";
        }
        if (entry.WarningCode is
            "ExecutableBlocked" or "DuplicateInstallPath")
        {
            return "Blocked";
        }
        if (entry.WarningCode?.Contains(
                "Unsupported",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Unsupported";
        }
        return "Excluded";
    }

    private static string LocalizeReason(
        string? reason,
        LocalizationService localization)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "—";
        var localized = localization.Get($"ModCardReason{reason}");
        return localized.StartsWith(
            "ModCardReason",
            StringComparison.Ordinal)
            ? reason
            : localized;
    }
}

public sealed class ModInstallPlanRowViewModel
{
    public ModInstallPlanRowViewModel(
        string archivePath,
        InstallPlanEntry entry,
        LocalizationService localization)
    {
        ArchivePath = archivePath;
        GamePath = entry.RelativeGamePath;
        Action = localization.Get($"PlanAction{entry.Action}");
        CurrentOwner = entry.Action is
            InstallPlanAction.Conflict or
            InstallPlanAction.AlreadyInstalled or
            InstallPlanAction.OverlayMod
                ? entry.OwnerDisplayName ?? entry.Reason ?? "—"
                : "—";
        Tone = entry.Action switch
        {
            InstallPlanAction.Add => "Success",
            InstallPlanAction.ReplaceExisting => "Warning",
            InstallPlanAction.Conflict => "Conflict",
            InstallPlanAction.Blocked => "Error",
            InstallPlanAction.AlreadyInstalled => "Success",
            InstallPlanAction.OverlayMod => "Warning",
            _ => "Neutral"
        };
    }

    public ModInstallPlanRowViewModel(
        string archivePath,
        ArchiveSwitchPlanEntry entry,
        LocalizationService localization)
    {
        ArchivePath = archivePath;
        GamePath = entry.RelativeGamePath;
        Action = localization.Get($"ArchiveSwitchAction{entry.Action}");
        CurrentOwner = entry.Action == ArchiveSwitchPlanAction.Conflict
            ? entry.OwnerDisplayName ?? "—"
            : "—";
        Tone = entry.Action switch
        {
            ArchiveSwitchPlanAction.Add => "Success",
            ArchiveSwitchPlanAction.ReplaceVersion => "Warning",
            ArchiveSwitchPlanAction.RemoveObsolete => "Warning",
            ArchiveSwitchPlanAction.RestoreLowerLayer => "Success",
            ArchiveSwitchPlanAction.Conflict => "Conflict",
            ArchiveSwitchPlanAction.Blocked => "Error",
            ArchiveSwitchPlanAction.Reinstall => "Warning",
            _ => "Neutral"
        };
    }

    public string ArchivePath { get; }
    public string GamePath { get; }
    public string Action { get; }
    public string CurrentOwner { get; }
    public string Tone { get; }
}

public sealed class InstalledFileRowViewModel
{
    public InstalledFileRowViewModel(
        InstalledFileRecord record,
        LocalizationService localization,
        string? archiveVersion = null)
    {
        Archive = string.IsNullOrWhiteSpace(archiveVersion)
            ? record.PackageId.Value
            : archiveVersion;
        GamePath = record.RelativeGamePath;
        State = localization.Get(record.PreviousFileExisted
            ? "ModCardReplacedExisting"
            : "ModCardAddedByMod");
        InstalledHash = record.InstalledContentHash;
        ShortInstalledHash =
            ModDetailsDialogViewModel.ShortHash(InstalledHash);
        PreviousFile = localization.Get(record.PreviousFileExisted
            ? "ModCardOriginalSaved"
            : "ModCardNoOriginal");
        PreviousHash = record.PreviousContentHash ?? "—";
        ShortPreviousHash =
            ModDetailsDialogViewModel.ShortHash(PreviousHash);
    }

    public string GamePath { get; }
    public string Archive { get; }
    public string State { get; }
    public string InstalledHash { get; }
    public string ShortInstalledHash { get; }
    public string PreviousFile { get; }
    public string PreviousHash { get; }
    public string ShortPreviousHash { get; }
}
