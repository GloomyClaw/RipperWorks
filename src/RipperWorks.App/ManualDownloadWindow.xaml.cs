using System.Windows;
using Microsoft.Win32;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;

namespace RipperWorks.App;

public partial class ManualDownloadWindow : Window
{
    private readonly ManualDownloadViewModel _viewModel;

    public ManualDownloadWindow(
        DownloaderEntry? entry,
        LocalizationService localization)
    {
        InitializeComponent();
        _viewModel = new ManualDownloadViewModel(entry, localization);
        DataContext = _viewModel;
    }

    public ManualDownloadEntry? Result { get; private set; }

    private void BrowseButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Mod archives|*.zip;*.7z;*.rar|All files|*.*"
        };
        if (dialog.ShowDialog() == true)
            _viewModel.ArchivePath = dialog.FileName;
    }

    private void SaveButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.Name))
            return;
        Result = new ManualDownloadEntry(
            _viewModel.Name.Trim(),
            _viewModel.Category.Trim(),
            _viewModel.Source,
            _viewModel.Author.Trim(),
            _viewModel.Url.Trim(),
            _viewModel.Version.Trim(),
            string.IsNullOrWhiteSpace(_viewModel.ArchivePath)
                ? null
                : _viewModel.ArchivePath);
        DialogResult = true;
    }
}

internal sealed class ManualDownloadViewModel : ObservableObject
{
    private string _archivePath;

    public ManualDownloadViewModel(
        DownloaderEntry? entry,
        LocalizationService localization)
    {
        Title = localization.Get(entry is null
            ? "DownloaderAddManual"
            : "Edit");
        Name = entry?.Name ?? string.Empty;
        Category = entry?.Category ?? localization.Get("DownloaderUndefined");
        Source = entry?.Source ?? DownloaderSource.Manual;
        Author = entry?.Author ?? string.Empty;
        Url = entry?.Url ?? string.Empty;
        Version = entry?.Version ?? string.Empty;
        _archivePath = string.Empty;
        Sources = Enum.GetValues<DownloaderSource>()
            .Select(source => new ChoiceItem(
                source.ToString(),
                localization.Get($"DownloaderSource{source}")))
            .ToArray();
        NameLabel = localization.Get("DisplayName");
        CategoryLabel = localization.Get("Category");
        SourceLabel = localization.Get("ColumnSource");
        AuthorLabel = localization.Get("Author");
        VersionLabel = localization.Get("ColumnVersion");
        ArchiveLabel = localization.Get("DownloaderLocalArchive");
        BrowseLabel = localization.Get("Browse");
        CancelLabel = localization.Get("Cancel");
        SaveLabel = localization.Get("Save");
    }

    public string Title { get; }
    public string Name { get; set; }
    public string Category { get; set; }
    public DownloaderSource Source { get; set; }
    public string Author { get; set; }
    public string Url { get; set; }
    public string Version { get; set; }
    public IReadOnlyList<ChoiceItem> Sources { get; }
    public string NameLabel { get; }
    public string CategoryLabel { get; }
    public string SourceLabel { get; }
    public string AuthorLabel { get; }
    public string VersionLabel { get; }
    public string ArchiveLabel { get; }
    public string BrowseLabel { get; }
    public string CancelLabel { get; }
    public string SaveLabel { get; }

    public string ArchivePath
    {
        get => _archivePath;
        set => SetProperty(ref _archivePath, value);
    }
}
