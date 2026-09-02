using System.Windows;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;

namespace RipperWorks.App;

public partial class NexusFileSelectionWindow : Window
{
    private readonly NexusFileSelectionViewModel _viewModel;

    public NexusFileSelectionWindow(
        NexusModMetadata metadata,
        LocalizationService localization)
    {
        InitializeComponent();
        _viewModel = new NexusFileSelectionViewModel(
            metadata,
            localization);
        DataContext = _viewModel;
    }

    public NexusFileInfo? SelectedFile => _viewModel.SelectedFile;

    private void SelectButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedFile is not null)
            DialogResult = true;
    }
}

internal sealed class NexusFileSelectionViewModel : ObservableObject
{
    private NexusFileInfo? _selectedFile;

    public NexusFileSelectionViewModel(
        NexusModMetadata metadata,
        LocalizationService localization)
    {
        Title = localization.Get("DownloaderSelectNexusFile");
        ModName = metadata.Name;
        Hint = localization.Get("DownloaderSelectNexusFileHint");
        Files = metadata.Files;
        CancelLabel = localization.Get("Cancel");
        SelectLabel = localization.Get("Select");
        NameHeader = localization.Get("DisplayName");
        FileHeader = localization.Get("DownloaderFile");
        VersionHeader = localization.Get("ColumnVersion");
        SizeHeader = localization.Get("ColumnSize");
        DateHeader = localization.Get("DownloaderDate");
        _selectedFile = Files.FirstOrDefault();
    }

    public string Title { get; }
    public string ModName { get; }
    public string Hint { get; }
    public IReadOnlyList<NexusFileInfo> Files { get; }
    public string CancelLabel { get; }
    public string SelectLabel { get; }
    public string NameHeader { get; }
    public string FileHeader { get; }
    public string VersionHeader { get; }
    public string SizeHeader { get; }
    public string DateHeader { get; }

    public NexusFileInfo? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value);
    }
}
