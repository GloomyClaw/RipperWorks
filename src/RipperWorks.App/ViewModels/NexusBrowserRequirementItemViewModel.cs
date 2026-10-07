using System.Windows.Input;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Downloader;

namespace RipperWorks.App.ViewModels;

public sealed class NexusBrowserRequirementItemViewModel : ObservableObject
{
    private readonly Action<string> _requestNavigate;
    private readonly LocalizationService _localization;
    private readonly INexusRequirementRelationsService? _relationsService;
    private readonly Action<NexusModIdentity>? _requestNavigateDownloads;
    private bool _isAddedToDownloads;

    public NexusBrowserRequirementItemViewModel(
        NexusRequirementProjectedEdge projectedEdge,
        LocalizationService localization,
        Action<string> requestNavigate,
        INexusRequirementRelationsService? relationsService = null,
        Action<NexusModIdentity>? requestNavigateDownloads = null)
    {
        Edge = projectedEdge.Edge;
        LocalState = projectedEdge.LocalState;
        ContentAccess = projectedEdge.ContentAccess;
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _requestNavigate = requestNavigate ?? throw new ArgumentNullException(nameof(requestNavigate));
        _relationsService = relationsService;
        _requestNavigateDownloads = requestNavigateDownloads;

        if (IsContentRestricted)
        {
            IsExternal = Edge.Target is NexusExternalRequirementTarget;
            DisplayName = RestrictedTitle();
            Notes = RestrictedDescription();
        }
        else if (Edge.ObservedThrough == NexusRequirementTraversal.ForwardRequirements)
        {
            if (Edge.Target is NexusModRequirementTarget modTarget)
            {
                NexusModId = modTarget.Identity.ModId;
                GameId = modTarget.Identity.GameId;
                DisplayName = !string.IsNullOrWhiteSpace(modTarget.DisplayName)
                    ? modTarget.DisplayName
                    : string.Format(_localization.Get("NexusModFallback"), modTarget.Identity.ModId);
                Notes = Edge.Notes;
                IsExternal = false;
            }
            else if (Edge.Target is NexusExternalRequirementTarget extTarget)
            {
                NexusModId = null;
                GameId = null;
                DisplayName = !string.IsNullOrWhiteSpace(extTarget.DisplayName)
                    ? extTarget.DisplayName
                    : _localization.Get("NexusExternalRequirement");
                Notes = Edge.Notes;
                IsExternal = true;
            }
            else
            {
                DisplayName = _localization.Get("NexusExternalRequirement");
                IsExternal = true;
            }
        }
        else
        {
            // Reverse required-by: the source mod is the one requiring this mod
            NexusModId = Edge.Source.ModId;
            GameId = Edge.Source.GameId;
            DisplayName = !string.IsNullOrWhiteSpace(Edge.SourceMetadata?.DisplayName)
                ? Edge.SourceMetadata.DisplayName
                : string.Format(_localization.Get("NexusModFallback"), Edge.Source.ModId);
            Notes = Edge.Notes;
            IsExternal = false;
        }

        OpenInNexusCommand = new RelayCommand(OpenInNexus, () => CanOpenInNexus);
        AddToDownloadsCommand = new AsyncRelayCommand(AddToDownloadsAsync, () => CanAddToDownloads);
        OpenInDownloadsCommand = new RelayCommand(OpenInDownloads, () => CanOpenInDownloads);
    }

    public NexusModRequirementEdge Edge { get; }
    public NexusRequirementLocalState LocalState { get; private set; }
    public NexusAdultContentAccess ContentAccess { get; }
    public long? NexusModId { get; }
    public long? GameId { get; }
    public string DisplayName { get; private set; }
    public string? Notes { get; private set; }
    public bool IsExternal { get; }
    public bool IsContentRestricted => ContentAccess.IsRestricted();
    public bool HasAdultIndicator =>
        ContentAccess == NexusAdultContentAccess.AdultAllowed;
    public string AdultIndicatorText => _localization.Get("NexusAdultBadge");

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    public bool CanOpenInNexus =>
        !IsContentRestricted &&
        NexusModId is > 0 &&
        GameId == NexusGameIdentityBridge.Cyberpunk2077NexusGameId;

    public bool IsInDownloader =>
        _isAddedToDownloads || LocalState.Availability == NexusRequirementLocalAvailability.InDownloads;

    public bool CanAddToDownloads =>
        !IsContentRestricted &&
        !IsExternal &&
        NexusModId.HasValue &&
        GameId.HasValue &&
        !IsInDownloader &&
        LocalState.Availability == NexusRequirementLocalAvailability.Missing;

    public bool CanOpenInDownloads =>
        !IsContentRestricted &&
        !IsExternal &&
        NexusModId.HasValue &&
        GameId.HasValue &&
        IsInDownloader;

    public bool HasDownloaderAction => CanAddToDownloads || CanOpenInDownloads;

    public string DownloaderActionText => CanOpenInDownloads
        ? _localization.Get("NexusBrowserPanelOpenInDownloads")
        : _localization.Get("NexusBrowserPanelAddToDownloads");

    public string LocalStatusText => IsContentRestricted
        ? _localization.Get("NexusContentRestrictedStatus")
        : IsInDownloader
        ? _localization.Get("NexusBrowserPanelAddedToDownloads")
        : (LocalState.Availability switch
        {
            NexusRequirementLocalAvailability.Installed => _localization.Get("InstallationInstalled"),
            NexusRequirementLocalAvailability.InLibrary => _localization.Get("NexusRelationStateInLibrary"),
            NexusRequirementLocalAvailability.InDownloads => _localization.Get("NexusBrowserPanelAddedToDownloads"),
            NexusRequirementLocalAvailability.Missing => _localization.Get("NexusRelationStateMissing"),
            NexusRequirementLocalAvailability.External => _localization.Get("NexusRelationStateExternal"),
            _ => _localization.Get("NexusRelationStateMissing")
        });

    public string OpenOnNexusText => _localization.Get("NexusBrowserPanelOpenOnNexus");

    public ICommand OpenInNexusCommand { get; }
    public ICommand AddToDownloadsCommand { get; }
    public ICommand OpenInDownloadsCommand { get; }

    public async Task AddToDownloadsAsync()
    {
        if (!CanAddToDownloads || !NexusModId.HasValue || !GameId.HasValue || _relationsService is null)
            return;

        var identity = new NexusModIdentity(GameId.Value, NexusModId.Value);
        var safeUrl = NexusGameIdentityBridge.TryGetGameDomain(GameId.Value, out var domain)
            ? $"https://www.nexusmods.com/{domain}/mods/{NexusModId.Value}"
            : null;

        try
        {
            var added = await _relationsService.AddToDownloadsAsync(identity, DisplayName, safeUrl);
            if (added)
            {
                _isAddedToDownloads = true;
                NotifyDownloaderStateChanged();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fail-safe: preserve truthful local state on error
        }
    }

    public void OpenInDownloads()
    {
        if (!CanOpenInDownloads || !NexusModId.HasValue || !GameId.HasValue)
            return;

        var identity = new NexusModIdentity(GameId.Value, NexusModId.Value);
        _requestNavigateDownloads?.Invoke(identity);
    }

    public void UpdateLocalState(NexusModLocalState state)
    {
        if (IsExternal) return;
        _isAddedToDownloads = false;
        var availability = state.IsInstalled
            ? NexusRequirementLocalAvailability.Installed
            : (state.IsInLibrary || state.IsDownloaded)
                ? NexusRequirementLocalAvailability.InLibrary
                : state.IsInDownloader
                    ? NexusRequirementLocalAvailability.InDownloads
                    : NexusRequirementLocalAvailability.Missing;

        LocalState = LocalState with { Availability = availability };
        NotifyDownloaderStateChanged();
    }

    private void NotifyDownloaderStateChanged()
    {
        OnPropertyChanged(nameof(IsInDownloader));
        OnPropertyChanged(nameof(CanAddToDownloads));
        OnPropertyChanged(nameof(CanOpenInDownloads));
        OnPropertyChanged(nameof(HasDownloaderAction));
        OnPropertyChanged(nameof(DownloaderActionText));
        OnPropertyChanged(nameof(LocalStatusText));
        (AddToDownloadsCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (OpenInDownloadsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (OpenInNexusCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void OpenInNexus()
    {
        if (!CanOpenInNexus || !NexusModId.HasValue)
            return;

        var url = $"https://www.nexusmods.com/{NexusGameIdentityBridge.Cyberpunk2077GameDomain}/mods/{NexusModId.Value}";
        _requestNavigate(url);
    }

    public void RefreshLocalization()
    {
        if (IsContentRestricted)
        {
            DisplayName = RestrictedTitle();
            Notes = RestrictedDescription();
        }
        else if (Edge.ObservedThrough == NexusRequirementTraversal.ForwardRequirements)
        {
            if (Edge.Target is NexusModRequirementTarget modTarget)
            {
                DisplayName = !string.IsNullOrWhiteSpace(modTarget.DisplayName)
                    ? modTarget.DisplayName
                    : string.Format(_localization.Get("NexusModFallback"), modTarget.Identity.ModId);
            }
            else
            {
                DisplayName = _localization.Get("NexusExternalRequirement");
            }
        }
        else
        {
            DisplayName = !string.IsNullOrWhiteSpace(Edge.SourceMetadata?.DisplayName)
                ? Edge.SourceMetadata.DisplayName
                : string.Format(_localization.Get("NexusModFallback"), Edge.Source.ModId);
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(LocalStatusText));
        OnPropertyChanged(nameof(OpenOnNexusText));
        OnPropertyChanged(nameof(DownloaderActionText));
        OnPropertyChanged(nameof(AdultIndicatorText));
    }

    private string RestrictedTitle() => _localization.Get(
        ContentAccess == NexusAdultContentAccess.AdultRestricted
            ? "NexusAdultRestrictedTitle"
            : "NexusContentUnavailableTitle");

    private string RestrictedDescription() => _localization.Get(
        ContentAccess == NexusAdultContentAccess.AdultRestricted
            ? "NexusAdultRestrictedDescription"
            : "NexusContentUnavailableDescription");
}
