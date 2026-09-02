using System.Collections.ObjectModel;
using System.Windows.Input;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Downloader;

namespace RipperWorks.App.ViewModels;

public enum NexusPanelTab { CurrentMod, Shortlist }

public sealed class NexusBrowserPanelViewModel : ObservableObject, IDisposable
{
    private readonly IShortlistStore _shortlistStore = null!;
    private readonly INexusModLocalStateService _localStateService = null!;
    private readonly LocalizationService _localization = null!;
    private readonly Action<string> _requestNavigate = null!;
    private readonly INexusApiClient? _nexusApi;
    private readonly IProtectedCredentialStore? _credentials;
    private readonly INexusRequirementRelationsService? _requirementRelationsService;
    private readonly INexusRequirementRefreshService? _requirementRefreshService;
    private readonly Action<NexusModIdentity>? _requestNavigateDownloads;
    private readonly SemaphoreSlim _shortlistGate = new(1, 1);
    private readonly SemaphoreSlim _requirementsGate = new(1, 1);

    private NexusPanelTab _selectedTab = NexusPanelTab.CurrentMod;
    private NexusPageContext? _currentPageContext;
    private NexusModMetadata? _currentMetadata;
    private bool _isCurrentModShortlisted;
    private string _currentModTitle = string.Empty, _currentModSubtitle = string.Empty;
    private string? _currentModAuthor, _currentModVersion, _currentModCategory, _metadataErrorText, _requirementsErrorText, _shortlistOperationErrorText, _installedVersion, _localVersionsText, _localStatusText;
    private bool _isMetadataLoading, _isRequirementsLoading, _isRequirementsAmbiguous, _isInDownloader, _isDownloaded, _isInLibrary, _isInstalled, _isDisposed;
    private long _currentContextGeneration, _localStateQueryGeneration, _requirementsQueryGeneration, _shortlistLoadGeneration;

    public NexusBrowserPanelViewModel(
        IShortlistStore shortlistStore, INexusModLocalStateService localStateService, LocalizationService localization, Action<string> requestNavigate,
        INexusApiClient? nexusApi = null, IProtectedCredentialStore? credentials = null, INexusRequirementRelationsService? requirementRelationsService = null, INexusRequirementRefreshService? requirementRefreshService = null,
        Action<NexusModIdentity>? requestNavigateDownloads = null)
    {
        _shortlistStore = shortlistStore ?? throw new ArgumentNullException(nameof(shortlistStore));
        _localStateService = localStateService ?? throw new ArgumentNullException(nameof(localStateService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _requestNavigate = requestNavigate ?? throw new ArgumentNullException(nameof(requestNavigate));
        _nexusApi = nexusApi; _credentials = credentials; _requirementRelationsService = requirementRelationsService; _requirementRefreshService = requirementRefreshService;
        _requestNavigateDownloads = requestNavigateDownloads;

        SelectCurrentModTabCommand = new RelayCommand(() => SelectedTab = NexusPanelTab.CurrentMod);
        SelectShortlistTabCommand = new RelayCommand(() => SelectedTab = NexusPanelTab.Shortlist);
        ToggleShortlistCommand = new AsyncRelayCommand(ToggleShortlistAsync);
        RefreshRequirementsCommand = new AsyncRelayCommand(RefreshRequirementsAsync);
        AddCurrentModToDownloadsCommand = new AsyncRelayCommand(AddCurrentModToDownloadsAsync, () => CanAddToCurrentModDownloads);
        OpenCurrentModInDownloadsCommand = new RelayCommand(OpenCurrentModInDownloads, () => CanOpenCurrentModInDownloads);

        _localization.LanguageChanged += Localization_OnLanguageChanged;
        _ = LoadShortlistAsync();
    }

    public ObservableCollection<ShortlistEntryItemViewModel> ShortlistItems { get; } = [];
    public ObservableCollection<NexusBrowserRequirementItemViewModel> ForwardRequirements { get; } = [];
    public ObservableCollection<NexusBrowserRequirementItemViewModel> ReverseRequirements { get; } = [];

    public NexusPanelTab SelectedTab { get => _selectedTab; set { if (SetProperty(ref _selectedTab, value)) { OnPropertyChanged(nameof(IsCurrentModTabSelected)); OnPropertyChanged(nameof(IsShortlistTabSelected)); } } }
    public bool IsCurrentModTabSelected => SelectedTab == NexusPanelTab.CurrentMod;
    public bool IsShortlistTabSelected => SelectedTab == NexusPanelTab.Shortlist;
    public string CurrentModTabHeader => _localization.Get("NexusBrowserPanelCurrentMod");
    public string ShortlistTabHeader => string.Format(_localization.Get("NexusBrowserPanelShortlistCount"), ShortlistItems.Count);
    public string LocalStatusHeader => _localization.Get("NexusBrowserPanelLocalStatus");
    public bool HasActiveModContext => _currentPageContext is not null;
    public string NoModMessage => _localization.Get("NexusBrowserPanelNoModSelected");
    public string EmptyShortlistMessage => _localization.Get("NexusBrowserPanelEmptyShortlist");
    public bool IsShortlistEmpty => ShortlistItems.Count == 0;

    public string CurrentModTitle { get => _currentModTitle; private set => SetProperty(ref _currentModTitle, value); }
    public string CurrentModSubtitle { get => _currentModSubtitle; private set => SetProperty(ref _currentModSubtitle, value); }
    public string? CurrentModAuthor { get => _currentModAuthor; private set { if (SetProperty(ref _currentModAuthor, value)) OnPropertyChanged(nameof(HasAnyMetadata)); } }
    public string? CurrentModVersion { get => _currentModVersion; private set { if (SetProperty(ref _currentModVersion, value)) OnPropertyChanged(nameof(HasAnyMetadata)); } }
    public string? CurrentModCategory { get => _currentModCategory; private set { if (SetProperty(ref _currentModCategory, value)) OnPropertyChanged(nameof(HasAnyMetadata)); } }
    public bool HasAnyMetadata => !string.IsNullOrEmpty(CurrentModAuthor) || !string.IsNullOrEmpty(CurrentModVersion) || !string.IsNullOrEmpty(CurrentModCategory);

    public bool IsMetadataLoading { get => _isMetadataLoading; private set => SetProperty(ref _isMetadataLoading, value); }
    public string? MetadataErrorText { get => _metadataErrorText; private set { if (SetProperty(ref _metadataErrorText, value)) OnPropertyChanged(nameof(HasMetadataError)); } }
    public bool HasMetadataError => !string.IsNullOrEmpty(MetadataErrorText);

    public string ForwardRequirementsHeader => string.Format(_localization.Get("NexusBrowserPanelRequirements"), ForwardRequirements.Count);
    public string ReverseRequirementsHeader => string.Format(_localization.Get("NexusBrowserPanelRequiredBy"), ReverseRequirements.Count);
    public bool HasForwardRequirements => ForwardRequirements.Count > 0;
    public bool HasReverseRequirements => ReverseRequirements.Count > 0;
    public bool HasAnyRequirements => HasForwardRequirements;
    public bool IsRequirementsAmbiguous => _isRequirementsAmbiguous;
    public string AmbiguousRequirementsMessage => _localization.Get("NexusBrowserPanelRequirementsAmbiguous");
    public string NoRequirementsMessage => _localization.Get("NexusBrowserPanelNoRequirements");

    public bool IsRequirementsLoading { get => _isRequirementsLoading; private set => SetProperty(ref _isRequirementsLoading, value); }
    public string? RequirementsErrorText { get => _requirementsErrorText; private set { if (SetProperty(ref _requirementsErrorText, value)) OnPropertyChanged(nameof(HasRequirementsError)); } }
    public bool HasRequirementsError => !string.IsNullOrEmpty(RequirementsErrorText);

    public string? ShortlistOperationErrorText { get => _shortlistOperationErrorText; private set { if (SetProperty(ref _shortlistOperationErrorText, value)) OnPropertyChanged(nameof(HasShortlistOperationError)); } }
    public bool HasShortlistOperationError => !string.IsNullOrEmpty(ShortlistOperationErrorText);

    public bool IsCurrentModShortlisted { get => _isCurrentModShortlisted; private set { if (SetProperty(ref _isCurrentModShortlisted, value)) OnPropertyChanged(nameof(ShortlistButtonText)); } }
    public string ShortlistButtonText => IsCurrentModShortlisted ? _localization.Get("NexusBrowserPanelRemoveFromShortlist") : _localization.Get("NexusBrowserPanelAddToShortlist");

    public bool IsInDownloader { get => _isInDownloader; private set => SetProperty(ref _isInDownloader, value); }
    public bool IsDownloaded { get => _isDownloaded; private set => SetProperty(ref _isDownloaded, value); }
    public bool IsInLibrary { get => _isInLibrary; private set => SetProperty(ref _isInLibrary, value); }
    public bool IsInstalled { get => _isInstalled; private set => SetProperty(ref _isInstalled, value); }
    public string? InstalledVersion { get => _installedVersion; private set => SetProperty(ref _installedVersion, value); }
    public string? LocalVersionsText { get => _localVersionsText; private set => SetProperty(ref _localVersionsText, value); }
    public string? LocalStatusText { get => _localStatusText; private set => SetProperty(ref _localStatusText, value); }

    public bool CanAddToCurrentModDownloads => HasActiveModContext && !IsInDownloader && !IsDownloaded && !IsInLibrary && !IsInstalled && !_isDisposed;
    public bool CanOpenCurrentModInDownloads => HasActiveModContext && IsInDownloader && !_isDisposed;
    public bool HasCurrentModDownloaderAction => CanAddToCurrentModDownloads || CanOpenCurrentModInDownloads;
    public string CurrentModDownloaderActionText => CanOpenCurrentModInDownloads ? _localization.Get("NexusBrowserPanelOpenInDownloads") : _localization.Get("NexusBrowserPanelAddToDownloads");

    public ICommand SelectCurrentModTabCommand { get; }
    public ICommand SelectShortlistTabCommand { get; }
    public ICommand ToggleShortlistCommand { get; }
    public ICommand RefreshRequirementsCommand { get; }
    public ICommand AddCurrentModToDownloadsCommand { get; }
    public ICommand OpenCurrentModInDownloadsCommand { get; }

    public void SetCurrentPageContext(NexusPageContext? context)
    {
        if (_isDisposed) return;
        var contextGen = Interlocked.Increment(ref _currentContextGeneration);
        var localStateGen = Interlocked.Increment(ref _localStateQueryGeneration);
        var reqGen = Interlocked.Increment(ref _requirementsQueryGeneration);
        _currentPageContext = context;
        OnPropertyChanged(nameof(HasActiveModContext));

        if (context is null) { ClearCurrentModState(); return; }

        ClearLocalState();
        ClearMetadataAndRequirements();

        var existing = ShortlistItems.FirstOrDefault(i =>
            i.NexusModId == context.NexusModId &&
            string.Equals(i.GameDomain, context.GameDomain, StringComparison.OrdinalIgnoreCase));

        IsCurrentModShortlisted = existing is not null;
        UpdateCurrentModMetadataPresentation();

        _ = RefreshCurrentModLocalStateAsync(context, localStateGen);
        _ = FetchModMetadataAsync(context, contextGen);
        _ = FetchModRequirementsAsync(context, contextGen, reqGen);
    }

    private void UpdateCurrentModMetadataPresentation()
    {
        if (_isDisposed) return;
        if (_currentPageContext is null)
        {
            CurrentModTitle = string.Empty; CurrentModSubtitle = string.Empty;
            CurrentModAuthor = null; CurrentModVersion = null; CurrentModCategory = null;
            return;
        }

        CurrentModSubtitle = $"# {_currentPageContext.NexusModId}";
        if (_currentMetadata is not null)
        {
            CurrentModTitle = !string.IsNullOrWhiteSpace(_currentMetadata.Name) ? _currentMetadata.Name : $"Nexus Mod #{_currentPageContext.NexusModId}";
            CurrentModAuthor = !string.IsNullOrWhiteSpace(_currentMetadata.Author) ? string.Format(_localization.Get("NexusBrowserPanelAuthor"), _currentMetadata.Author) : null;
            CurrentModVersion = !string.IsNullOrWhiteSpace(_currentMetadata.Version) ? string.Format(_localization.Get("NexusBrowserPanelVersion"), _currentMetadata.Version) : null;
            CurrentModCategory = !string.IsNullOrWhiteSpace(_currentMetadata.Category) ? string.Format(_localization.Get("NexusBrowserPanelCategory"), _currentMetadata.Category) : null;
            return;
        }

        var matching = ShortlistItems.FirstOrDefault(i => i.NexusModId == _currentPageContext.NexusModId && string.Equals(i.GameDomain, _currentPageContext.GameDomain, StringComparison.OrdinalIgnoreCase));
        if (matching is not null)
        {
            CurrentModTitle = !string.IsNullOrWhiteSpace(matching.Name) ? matching.Name : $"Nexus Mod #{_currentPageContext.NexusModId}";
            CurrentModAuthor = !string.IsNullOrWhiteSpace(matching.Author) ? string.Format(_localization.Get("NexusBrowserPanelAuthor"), matching.Author) : null;
            CurrentModVersion = !string.IsNullOrWhiteSpace(matching.LastKnownVersion) ? string.Format(_localization.Get("NexusBrowserPanelVersion"), matching.LastKnownVersion) : null;
            CurrentModCategory = null;
            return;
        }

        CurrentModTitle = $"Nexus Mod #{_currentPageContext.NexusModId}";
        CurrentModAuthor = null; CurrentModVersion = null; CurrentModCategory = null;
    }

    private async Task FetchModMetadataAsync(NexusPageContext context, long contextGen)
    {
        if (_nexusApi is null || _credentials is null || _isDisposed) return;
        try
        {
            IsMetadataLoading = true; MetadataErrorText = null;
            var status = await _credentials.GetStatusAsync(CredentialIdentity.NexusDefault);
            if (_isDisposed || status.Status != CredentialPresenceStatus.Present) return;

            var metadata = await _credentials.UseAsync(CredentialIdentity.NexusDefault, (apiKey, token) => _nexusApi.GetModMetadataOnlyAsync(context.GameDomain, context.NexusModId, apiKey, token));
            if (contextGen != Volatile.Read(ref _currentContextGeneration) || _isDisposed) return;

            _currentMetadata = metadata;
            UpdateCurrentModMetadataPresentation();

            await _shortlistGate.WaitAsync();
            try
            {
                if (contextGen != Volatile.Read(ref _currentContextGeneration) || _isDisposed) return;
                var isStillShortlisted = await _shortlistStore.ContainsAsync(context.GameDomain, context.NexusModId);
                if (_isDisposed || contextGen != Volatile.Read(ref _currentContextGeneration)) return;
                if (isStillShortlisted)
                {
                    await _shortlistStore.AddAsync(context.GameDomain, context.NexusModId, metadata.Name, metadata.Author, metadata.Version);
                    if (!_isDisposed && contextGen == Volatile.Read(ref _currentContextGeneration)) await LoadShortlistAsync();
                }
            }
            finally { _shortlistGate.Release(); }
        }
        catch { if (contextGen == Volatile.Read(ref _currentContextGeneration) && !_isDisposed) MetadataErrorText = _localization.Get("NexusBrowserPanelMetadataError"); }
        finally { if (contextGen == Volatile.Read(ref _currentContextGeneration) && !_isDisposed) IsMetadataLoading = false; }
    }

    private async Task FetchModRequirementsAsync(NexusPageContext context, long contextGen, long reqGen)
    {
        if (_requirementRelationsService is null || _isDisposed) return;
        try
        {
            IsRequirementsLoading = true; RequirementsErrorText = null;
            if (!NexusGameIdentityBridge.TryGetNexusGameId(context.GameDomain, out var gameId)) return;

            var identity = new NexusModIdentity(gameId, context.NexusModId);
            var cachedRelations = await _requirementRelationsService.LoadRelationsAsync(identity);
            if (contextGen != Volatile.Read(ref _currentContextGeneration) || reqGen != Volatile.Read(ref _requirementsQueryGeneration) || _isDisposed) return;
            ApplyRequirements(cachedRelations);

            if (_requirementRefreshService is not null)
            {
                await _requirementsGate.WaitAsync();
                try
                {
                    if (contextGen != Volatile.Read(ref _currentContextGeneration) || reqGen != Volatile.Read(ref _requirementsQueryGeneration) || _isDisposed) return;

                    var syncResult = await _requirementRefreshService.RefreshAsync(identity);
                    if (contextGen != Volatile.Read(ref _currentContextGeneration) || reqGen != Volatile.Read(ref _requirementsQueryGeneration) || _isDisposed) return;

                    if (syncResult.Outcome == NexusRequirementSyncOutcome.Failure || syncResult.Forward.Outcome == NexusRequirementTraversalOutcome.Failed)
                    {
                        RequirementsErrorText = _localization.Get("NexusBrowserPanelRequirementsError");
                    }

                    var freshRelations = await _requirementRelationsService.LoadRelationsAsync(identity);
                    if (contextGen != Volatile.Read(ref _currentContextGeneration) || reqGen != Volatile.Read(ref _requirementsQueryGeneration) || _isDisposed) return;
                    ApplyRequirements(freshRelations);
                }
                finally { _requirementsGate.Release(); }
            }
        }
        catch
        {
            if (contextGen == Volatile.Read(ref _currentContextGeneration) && reqGen == Volatile.Read(ref _requirementsQueryGeneration) && !_isDisposed)
                RequirementsErrorText = _localization.Get("NexusBrowserPanelRequirementsError");
        }
        finally
        {
            if (contextGen == Volatile.Read(ref _currentContextGeneration) && reqGen == Volatile.Read(ref _requirementsQueryGeneration) && !_isDisposed)
                IsRequirementsLoading = false;
        }
    }

    public async Task RefreshRequirementsAsync()
    {
        if (_currentPageContext is null || _requirementRefreshService is null || _requirementRelationsService is null || _isDisposed) return;
        var context = _currentPageContext;
        var contextGen = Volatile.Read(ref _currentContextGeneration);
        var reqGen = Interlocked.Increment(ref _requirementsQueryGeneration);

        await _requirementsGate.WaitAsync();
        try
        {
            if (contextGen != Volatile.Read(ref _currentContextGeneration) || reqGen != Volatile.Read(ref _requirementsQueryGeneration) || _isDisposed) return;

            IsRequirementsLoading = true; RequirementsErrorText = null;
            if (!NexusGameIdentityBridge.TryGetNexusGameId(context.GameDomain, out var gameId)) return;

            var identity = new NexusModIdentity(gameId, context.NexusModId);
            var syncResult = await _requirementRefreshService.RefreshAsync(identity);

            if (contextGen != Volatile.Read(ref _currentContextGeneration) || reqGen != Volatile.Read(ref _requirementsQueryGeneration) || _isDisposed) return;

            if (syncResult.Outcome == NexusRequirementSyncOutcome.Failure || syncResult.Forward.Outcome == NexusRequirementTraversalOutcome.Failed)
            {
                RequirementsErrorText = _localization.Get("NexusBrowserPanelRequirementsError");
            }

            var relations = await _requirementRelationsService.LoadRelationsAsync(identity);
            if (contextGen != Volatile.Read(ref _currentContextGeneration) || reqGen != Volatile.Read(ref _requirementsQueryGeneration) || _isDisposed) return;
            ApplyRequirements(relations);
        }
        catch
        {
            if (contextGen == Volatile.Read(ref _currentContextGeneration) && reqGen == Volatile.Read(ref _requirementsQueryGeneration) && !_isDisposed)
                RequirementsErrorText = _localization.Get("NexusBrowserPanelRequirementsError");
        }
        finally
        {
            if (contextGen == Volatile.Read(ref _currentContextGeneration) && reqGen == Volatile.Read(ref _requirementsQueryGeneration) && !_isDisposed)
                IsRequirementsLoading = false;
            _requirementsGate.Release();
        }
    }

    private void ApplyRequirements(NexusRequirementRelations relations)
    {
        if (_isDisposed) return;

        ForwardRequirements.Clear();
        foreach (var edge in relations.Forward.Relations)
            ForwardRequirements.Add(new NexusBrowserRequirementItemViewModel(edge, _localization, _requestNavigate, _requirementRelationsService, _requestNavigateDownloads));

        ReverseRequirements.Clear();

        _isRequirementsAmbiguous = relations.IsForwardSourceAmbiguous;
        OnPropertyChanged(nameof(ForwardRequirementsHeader)); OnPropertyChanged(nameof(ReverseRequirementsHeader));
        OnPropertyChanged(nameof(HasForwardRequirements)); OnPropertyChanged(nameof(HasReverseRequirements)); OnPropertyChanged(nameof(HasAnyRequirements));
        OnPropertyChanged(nameof(IsRequirementsAmbiguous)); OnPropertyChanged(nameof(AmbiguousRequirementsMessage)); OnPropertyChanged(nameof(NoRequirementsMessage));
    }

    private async Task RefreshCurrentModLocalStateAsync(NexusPageContext context, long localStateGen)
    {
        try
        {
            var state = await _localStateService.QueryAsync(context.GameDomain, context.NexusModId);
            if (localStateGen != Volatile.Read(ref _localStateQueryGeneration) || _isDisposed) return;
            ApplyCurrentModLocalState(state);
        }
        catch { if (localStateGen == Volatile.Read(ref _localStateQueryGeneration) && !_isDisposed) ClearLocalState(); }
    }

    private void ApplyCurrentModLocalState(NexusModLocalState state)
    {
        if (_isDisposed) return;
        IsCurrentModShortlisted = state.IsShortlisted; IsInDownloader = state.IsInDownloader;
        IsDownloaded = state.IsDownloaded; IsInLibrary = state.IsInLibrary;
        IsInstalled = state.IsInstalled; InstalledVersion = state.InstalledVersion;

        LocalVersionsText = state.LocalVersions.Count > 0 ? string.Format(_localization.Get("NexusBrowserPanelLocalVersions"), string.Join(", ", state.LocalVersions)) : null;

        if (state.IsInstalled)
            LocalStatusText = !string.IsNullOrWhiteSpace(state.InstalledVersion) ? string.Format(_localization.Get("NexusBrowserPanelInstalledVersion"), state.InstalledVersion) : _localization.Get("NexusBrowserPanelInstalled");
        else if (state.IsDownloaded || state.IsInLibrary) LocalStatusText = _localization.Get("NexusBrowserPanelDownloaded");
        else if (state.IsInDownloader) LocalStatusText = _localization.Get("NexusBrowserPanelAddedToDownloads");
        else LocalStatusText = _localization.Get("NexusBrowserPanelAbsent");

        NotifyCurrentModDownloaderStateChanged();
    }

    private void ClearLocalState()
    {
        if (_isDisposed) return;
        IsInDownloader = false; IsDownloaded = false; IsInLibrary = false;
        IsInstalled = false; InstalledVersion = null; LocalVersionsText = null; LocalStatusText = null;
        NotifyCurrentModDownloaderStateChanged();
    }

    private void ClearMetadataAndRequirements()
    {
        if (_isDisposed) return;
        _currentMetadata = null; CurrentModAuthor = null; CurrentModVersion = null; CurrentModCategory = null;
        IsMetadataLoading = false; MetadataErrorText = null; IsRequirementsLoading = false; RequirementsErrorText = null; _isRequirementsAmbiguous = false;
        ForwardRequirements.Clear(); ReverseRequirements.Clear();
        OnPropertyChanged(nameof(ForwardRequirementsHeader)); OnPropertyChanged(nameof(ReverseRequirementsHeader));
        OnPropertyChanged(nameof(HasForwardRequirements)); OnPropertyChanged(nameof(HasReverseRequirements)); OnPropertyChanged(nameof(HasAnyRequirements));
        OnPropertyChanged(nameof(IsRequirementsAmbiguous)); OnPropertyChanged(nameof(AmbiguousRequirementsMessage)); OnPropertyChanged(nameof(NoRequirementsMessage));
    }

    private void ClearCurrentModState()
    {
        if (_isDisposed) return;
        CurrentModTitle = string.Empty; CurrentModSubtitle = string.Empty; IsCurrentModShortlisted = false;
        ClearLocalState(); ClearMetadataAndRequirements();
    }

    public async Task ToggleShortlistAsync()
    {
        if (_currentPageContext is null || _isDisposed) return;
        ShortlistOperationErrorText = null;
        var domain = _currentPageContext.GameDomain;
        var modId = _currentPageContext.NexusModId;
        var wasShortlisted = IsCurrentModShortlisted;

        await _shortlistGate.WaitAsync();
        try
        {
            if (_isDisposed) return;
            if (wasShortlisted)
            {
                await _shortlistStore.RemoveAsync(domain, modId);
                if (_isDisposed) return;
                IsCurrentModShortlisted = false;
            }
            else
            {
                await _shortlistStore.AddAsync(domain, modId, name: _currentMetadata?.Name, author: _currentMetadata?.Author, lastKnownVersion: _currentMetadata?.Version);
            }
            if (_isDisposed) return;
            await LoadShortlistAsync();
            if (_isDisposed) return;
            UpdateCurrentModMetadataPresentation();
            var localStateGen = Interlocked.Increment(ref _localStateQueryGeneration);
            await RefreshCurrentModLocalStateAsync(_currentPageContext, localStateGen);
        }
        catch { if (!_isDisposed) ShortlistOperationErrorText = _localization.Get("NexusBrowserPanelOperationError"); }
        finally { _shortlistGate.Release(); }
    }

    public async Task LoadShortlistAsync()
    {
        if (_isDisposed) return;
        var generation = Interlocked.Increment(ref _shortlistLoadGeneration);
        try
        {
            var entries = await _shortlistStore.LoadEntriesAsync();
            if (_isDisposed || generation != Volatile.Read(ref _shortlistLoadGeneration)) return;

            var ordered = entries.OrderByDescending(e => e.AddedAtUtc).ThenBy(e => e.NexusModId).ToList();
            ShortlistItems.Clear();
            foreach (var record in ordered) ShortlistItems.Add(new ShortlistEntryItemViewModel(record, _localization, OnOpenItemInNexus, RemoveItemAsync));

            OnPropertyChanged(nameof(ShortlistTabHeader));
            OnPropertyChanged(nameof(IsShortlistEmpty));

            if (_currentPageContext is not null)
            {
                var currentMatching = ShortlistItems.FirstOrDefault(i =>
                    i.NexusModId == _currentPageContext.NexusModId &&
                    string.Equals(i.GameDomain, _currentPageContext.GameDomain, StringComparison.OrdinalIgnoreCase));

                IsCurrentModShortlisted = currentMatching is not null;
                UpdateCurrentModMetadataPresentation();
            }

            if (_isDisposed || generation != Volatile.Read(ref _shortlistLoadGeneration)) return;
            foreach (var item in ShortlistItems)
            {
                _ = QueryShortlistItemLocalStateAsync(item);
            }
        }
        catch { }
    }

    private async Task QueryShortlistItemLocalStateAsync(ShortlistEntryItemViewModel item)
    {
        if (_isDisposed || item is null) return;
        try
        {
            var state = await _localStateService.QueryAsync(item.GameDomain, item.NexusModId);
            if (!_isDisposed) item.UpdateLocalState(state, _localization);
        }
        catch { }
    }

    private void OnOpenItemInNexus(ShortlistEntryItemViewModel item)
    {
        if (_isDisposed || item is null) return;
        _requestNavigate($"https://www.nexusmods.com/{item.GameDomain}/mods/{item.NexusModId}");
    }

    public async Task RemoveItemAsync(ShortlistEntryItemViewModel item)
    {
        if (_isDisposed || item is null) return;
        ShortlistOperationErrorText = null;
        await _shortlistGate.WaitAsync();
        try
        {
            if (_isDisposed) return;
            await _shortlistStore.RemoveAsync(item.GameDomain, item.NexusModId);
            if (_isDisposed) return;
            ShortlistItems.Remove(item);
            OnPropertyChanged(nameof(ShortlistTabHeader)); OnPropertyChanged(nameof(IsShortlistEmpty));

            if (_currentPageContext is not null && _currentPageContext.NexusModId == item.NexusModId && string.Equals(_currentPageContext.GameDomain, item.GameDomain, StringComparison.OrdinalIgnoreCase))
            {
                IsCurrentModShortlisted = false;
                UpdateCurrentModMetadataPresentation();
                var localStateGen = Interlocked.Increment(ref _localStateQueryGeneration);
                await RefreshCurrentModLocalStateAsync(_currentPageContext, localStateGen);
            }
        }
        catch { if (!_isDisposed) ShortlistOperationErrorText = _localization.Get("NexusBrowserPanelOperationError"); }
        finally { _shortlistGate.Release(); }
    }

    private void Localization_OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        OnPropertyChanged(nameof(CurrentModTabHeader)); OnPropertyChanged(nameof(ShortlistTabHeader));
        OnPropertyChanged(nameof(LocalStatusHeader)); OnPropertyChanged(nameof(NoModMessage));
        OnPropertyChanged(nameof(EmptyShortlistMessage)); OnPropertyChanged(nameof(ShortlistButtonText));
        OnPropertyChanged(nameof(ForwardRequirementsHeader)); OnPropertyChanged(nameof(ReverseRequirementsHeader));
        OnPropertyChanged(nameof(NoRequirementsMessage));

        if (!string.IsNullOrEmpty(ShortlistOperationErrorText)) ShortlistOperationErrorText = _localization.Get("NexusBrowserPanelOperationError");
        if (!string.IsNullOrEmpty(MetadataErrorText)) MetadataErrorText = _localization.Get("NexusBrowserPanelMetadataError");
        if (!string.IsNullOrEmpty(RequirementsErrorText)) RequirementsErrorText = _localization.Get("NexusBrowserPanelRequirementsError");

        UpdateCurrentModMetadataPresentation();
        NotifyCurrentModDownloaderStateChanged();

        if (_currentPageContext != null)
        {
            var localStateGen = Interlocked.Increment(ref _localStateQueryGeneration);
            _ = RefreshCurrentModLocalStateAsync(_currentPageContext, localStateGen);
        }
        foreach (var item in ShortlistItems)
        {
            item.RefreshLocalization();
            _ = QueryShortlistItemLocalStateAsync(item);
        }
        foreach (var item in ForwardRequirements) item.RefreshLocalization();
        foreach (var item in ReverseRequirements) item.RefreshLocalization();
    }

    public async Task AddCurrentModToDownloadsAsync()
    {
        if (_currentPageContext is null || !CanAddToCurrentModDownloads || _requirementRelationsService is null || _isDisposed) return;
        var context = _currentPageContext;
        if (!NexusGameIdentityBridge.TryGetNexusGameId(context.GameDomain, out var gameId)) return;

        var identity = new NexusModIdentity(gameId, context.NexusModId);
        var name = !string.IsNullOrWhiteSpace(_currentMetadata?.Name) ? _currentMetadata.Name : CurrentModTitle;
        var safeUrl = $"https://www.nexusmods.com/{context.GameDomain}/mods/{context.NexusModId}";

        try
        {
            var added = await _requirementRelationsService.AddToDownloadsAsync(identity, name, safeUrl);
            if (added && !_isDisposed)
            {
                var localStateGen = Interlocked.Increment(ref _localStateQueryGeneration);
                await RefreshCurrentModLocalStateAsync(context, localStateGen);
                NotifyCurrentModDownloaderStateChanged();
            }
        }
        catch { }
    }

    public void OpenCurrentModInDownloads()
    {
        if (_currentPageContext is null || !CanOpenCurrentModInDownloads || _isDisposed) return;
        var context = _currentPageContext;
        if (!NexusGameIdentityBridge.TryGetNexusGameId(context.GameDomain, out var gameId)) return;
        var identity = new NexusModIdentity(gameId, context.NexusModId);
        _requestNavigateDownloads?.Invoke(identity);
    }

    public async Task OnActivatedAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) return;
        if (_currentPageContext is not null)
        {
            var localStateGen = Interlocked.Increment(ref _localStateQueryGeneration);
            await RefreshCurrentModLocalStateAsync(_currentPageContext, localStateGen);
            NotifyCurrentModDownloaderStateChanged();
        }
        if (ForwardRequirements.Count > 0)
        {
            var items = ForwardRequirements.ToArray();
            foreach (var item in items)
            {
                if (_isDisposed || cancellationToken.IsCancellationRequested) break;
                if (item.NexusModId.HasValue && item.GameId.HasValue && NexusGameIdentityBridge.TryGetGameDomain(item.GameId.Value, out var domain))
                {
                    try
                    {
                        var state = await _localStateService.QueryAsync(domain, item.NexusModId.Value, cancellationToken);
                        if (!_isDisposed) item.UpdateLocalState(state);
                    }
                    catch { }
                }
            }
        }
        foreach (var item in ShortlistItems)
        {
            if (_isDisposed || cancellationToken.IsCancellationRequested) break;
            _ = QueryShortlistItemLocalStateAsync(item);
        }
    }

    private void NotifyCurrentModDownloaderStateChanged()
    {
        OnPropertyChanged(nameof(CanAddToCurrentModDownloads));
        OnPropertyChanged(nameof(CanOpenCurrentModInDownloads));
        OnPropertyChanged(nameof(HasCurrentModDownloaderAction));
        OnPropertyChanged(nameof(CurrentModDownloaderActionText));
        (AddCurrentModToDownloadsCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (OpenCurrentModInDownloadsCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
    }
}