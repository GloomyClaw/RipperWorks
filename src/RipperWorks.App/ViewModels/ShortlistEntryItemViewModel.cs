using System.Windows.Input;
using RipperWorks.App.Services;
using RipperWorks.Core;

namespace RipperWorks.App.ViewModels;

public sealed class ShortlistEntryItemViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private bool _isInDownloader;
    private bool _isDownloaded;
    private bool _isInLibrary;
    private bool _isInstalled;
    private string? _installedVersion;
    private string? _localStatusText;

    public ShortlistEntryItemViewModel(
        ShortlistEntryRecord record,
        LocalizationService localization,
        Action<ShortlistEntryItemViewModel> onOpenInNexus,
        Func<ShortlistEntryItemViewModel, Task> onRemoveAsync)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(onOpenInNexus);
        ArgumentNullException.ThrowIfNull(onRemoveAsync);

        _localization = localization;
        GameDomain = record.GameDomain;
        NexusModId = record.NexusModId;
        AddedAtUtc = record.AddedAtUtc;
        Name = record.Name;
        Author = record.Author;
        LastKnownVersion = record.LastKnownVersion;

        DisplayName = !string.IsNullOrWhiteSpace(record.Name)
            ? record.Name
            : $"Nexus Mod #{record.NexusModId}";

        Subtitle = $"# {record.NexusModId}";

        OpenInNexusCommand = new RelayCommand(() => onOpenInNexus(this));
        RemoveCommand = new AsyncRelayCommand(() => onRemoveAsync(this));
    }

    public string GameDomain { get; }
    public long NexusModId { get; }
    public DateTimeOffset AddedAtUtc { get; }
    public string? Name { get; }
    public string? Author { get; }
    public string? LastKnownVersion { get; }
    public string DisplayName { get; }
    public string Subtitle { get; }

    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);
    public bool HasVersion => !string.IsNullOrWhiteSpace(LastKnownVersion);

    public string OpenOnNexusText => _localization.Get("NexusBrowserPanelOpenOnNexus");
    public string RemoveText => _localization.Get("NexusBrowserPanelRemove");

    public bool IsInDownloader
    {
        get => _isInDownloader;
        private set => SetProperty(ref _isInDownloader, value);
    }

    public bool IsDownloaded
    {
        get => _isDownloaded;
        private set => SetProperty(ref _isDownloaded, value);
    }

    public bool IsInLibrary
    {
        get => _isInLibrary;
        private set => SetProperty(ref _isInLibrary, value);
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        private set => SetProperty(ref _isInstalled, value);
    }

    public string? InstalledVersion
    {
        get => _installedVersion;
        private set => SetProperty(ref _installedVersion, value);
    }

    public string? LocalStatusText
    {
        get => _localStatusText;
        private set => SetProperty(ref _localStatusText, value);
    }

    public ICommand OpenInNexusCommand { get; }
    public ICommand RemoveCommand { get; }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(OpenOnNexusText));
        OnPropertyChanged(nameof(RemoveText));
    }

    public void UpdateLocalState(NexusModLocalState state, LocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(loc);

        IsInDownloader = state.IsInDownloader;
        IsDownloaded = state.IsDownloaded;
        IsInLibrary = state.IsInLibrary;
        IsInstalled = state.IsInstalled;
        InstalledVersion = state.InstalledVersion;

        if (state.IsInstalled)
        {
            LocalStatusText = !string.IsNullOrWhiteSpace(state.InstalledVersion)
                ? string.Format(loc.Get("NexusBrowserPanelInstalledVersion"), state.InstalledVersion)
                : loc.Get("NexusBrowserPanelInstalled");
        }
        else if (state.IsInLibrary)
        {
            LocalStatusText = loc.Get("NexusBrowserPanelInLibrary");
        }
        else if (state.IsDownloaded)
        {
            LocalStatusText = loc.Get("NexusBrowserPanelDownloaded");
        }
        else if (state.IsInDownloader)
        {
            LocalStatusText = loc.Get("NexusBrowserPanelInDownloads");
        }
        else
        {
            LocalStatusText = null;
        }
    }
}
