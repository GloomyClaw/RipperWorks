using System.Collections.ObjectModel;
using RipperWorks.App.Services;
using RipperWorks.Core;

namespace RipperWorks.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;
    private NavigationItem _selectedNavigation;

    public MainWindowViewModel(
        LibraryViewModel library,
        DownloadsViewModel downloads,
        SettingsViewModel settings,
        DiagnosticsViewModel diagnostics,
        LocalizationService localization,
        IShortlistStore? shortlistStore = null,
        INexusModLocalStateService? localStateService = null,
        INexusApiClient? nexusApi = null,
        INexusRequirementRelationsService? requirementRelationsService = null,
        INexusRequirementRefreshService? requirementRefreshService = null)
    {
        Library = library;
        Downloads = downloads;
        Settings = settings;
        Diagnostics = diagnostics;
        _localization = localization;
        NexusBrowser = new NexusBrowserViewModel(
            shortlistStore,
            localStateService,
            localization,
            nexusApi,
            requirementRelationsService,
            requirementRefreshService,
            identity =>
            {
                SelectDownloads();
                Downloads.NavigateToNexusMod(identity);
            });
        Navigation =
        [
            new("Nexus", localization.Get("Nexus")),
            new("Downloads", localization.Get("Downloads")),
            new("Library", localization.Get("Library")),
            new("Diagnostics", localization.Get("Diagnostics")),
            new("Settings", localization.Get("Settings"))
        ];
        _selectedNavigation = Navigation.Single(item => item.Key == "Library");
        _localization.LanguageChanged += Localization_OnLanguageChanged;
        if (Library is not null)
        {
            Library.RequestNavigateNexus = url => NavigateToNexusMod(url);
            Library.RequestNavigateDiagnostics = SelectDiagnostics;
        }
        if (Downloads is not null)
        {
            Downloads.RequestNavigateNexus = url => NavigateToNexusMod(url);
        }
        if (Settings is not null)
        {
            Settings.RequestNavigateDiagnostics = SelectDiagnostics;
        }
    }

    public ObservableCollection<NavigationItem> Navigation { get; }
    public LibraryViewModel Library { get; } = null!;
    public DownloadsViewModel Downloads { get; } = null!;
    public NexusBrowserViewModel NexusBrowser { get; } = null!;
    public SettingsViewModel Settings { get; } = null!;
    public DiagnosticsViewModel Diagnostics { get; } = null!;

    public NavigationItem SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            if (!SetProperty(ref _selectedNavigation, value))
                return;
            OnPropertyChanged(nameof(IsLibraryVisible));
            OnPropertyChanged(nameof(IsDownloadsVisible));
            OnPropertyChanged(nameof(IsNexusVisible));
            OnPropertyChanged(nameof(IsDiagnosticsVisible));
            OnPropertyChanged(nameof(IsSettingsVisible));
            OnPropertyChanged(nameof(IsPlaceholderVisible));
            OnPropertyChanged(nameof(SelectedTitle));
            if (value?.Key == "Nexus")
            {
                _ = NexusBrowser.OnActivatedAsync();
            }
        }
    }

    public bool IsLibraryVisible => SelectedNavigation.Key == "Library";
    public bool IsDownloadsVisible => SelectedNavigation.Key == "Downloads";
    public bool IsNexusVisible => SelectedNavigation.Key == "Nexus";
    public bool IsDiagnosticsVisible =>
        SelectedNavigation.Key == "Diagnostics";
    public bool IsSettingsVisible => SelectedNavigation.Key == "Settings";
    public bool IsPlaceholderVisible =>
        !IsLibraryVisible && !IsDownloadsVisible && !IsNexusVisible &&
        !IsDiagnosticsVisible && !IsSettingsVisible;
    public string SelectedTitle => SelectedNavigation.Title;
    public string Placeholder => _localization.Get("Placeholder");

    public void SelectDownloads() =>
        SelectedNavigation =
            Navigation.Single(item => item.Key == "Downloads");

    public void SelectNexus() =>
        SelectedNavigation =
            Navigation.Single(item => item.Key == "Nexus");

    public void SelectDiagnostics() =>
        SelectedNavigation =
            Navigation.Single(item => item.Key == "Diagnostics");

    public void NavigateToNexusMod(string url)
    {
        NexusBrowser.Navigate(url);
        SelectNexus();
    }

    public async Task HandleExternalArgumentAsync(string argument)
    {
        if (!DownloaderLinkParser.TryParseNxm(argument, out _))
            return;
        SelectDownloads();
        await Downloads.HandleNxmAsync(argument);
    }

    private void RefreshLocalization()
    {
        foreach (var item in Navigation)
            item.Title = _localization.Get(item.Key);
        OnPropertyChanged(nameof(Placeholder));
        OnPropertyChanged(nameof(SelectedTitle));
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= Localization_OnLanguageChanged;
        NexusBrowser.Dispose();
    }

    private void Localization_OnLanguageChanged(object? sender, EventArgs e) =>
        RefreshLocalization();
}

public sealed class NavigationItem(string key, string title) : ObservableObject
{
    private string _title = title;
    public string Key { get; } = key;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}
