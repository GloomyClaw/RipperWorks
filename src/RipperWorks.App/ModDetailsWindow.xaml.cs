using System.Windows;
using System.ComponentModel;
using RipperWorks.App.ViewModels;

namespace RipperWorks.App;

public partial class ModDetailsWindow : Window
{
    private bool _initialized;
    private ModDetailsDialogViewModel? _headerViewModel;

    public ModDetailsWindow()
    {
        InitializeComponent();
    }

    private async void Window_OnLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialized ||
            DataContext is not ModDetailsDialogViewModel viewModel)
        {
            return;
        }
        _headerViewModel = viewModel;
        _headerViewModel.PropertyChanged +=
            HeaderViewModel_OnPropertyChanged;
        _headerViewModel.RequestClose += HeaderViewModel_OnRequestClose;
        ApplyLocalizedColumnHeaders(viewModel);
        _initialized = true;
        await viewModel.InitializeAsync();
    }

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        if (_headerViewModel is not null)
        {
            _headerViewModel.PropertyChanged -=
                HeaderViewModel_OnPropertyChanged;
            _headerViewModel.RequestClose -= HeaderViewModel_OnRequestClose;
            _headerViewModel = null;
        }
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }

    private void HeaderViewModel_OnRequestClose()
    {
        Close();
    }

    private void HeaderViewModel_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (sender is ModDetailsDialogViewModel viewModel)
            ApplyLocalizedColumnHeaders(viewModel);
    }

    private void ApplyLocalizedColumnHeaders(
        ModDetailsDialogViewModel viewModel)
    {
        var localization = viewModel.Library.Localization;
        SetHeaders(
            ArchivesAndVersionsGrid,
            viewModel.ArchiveComponentColumnLabel,
            viewModel.ArchiveFileIdColumnLabel,
            viewModel.ArchiveVersionColumnLabel,
            viewModel.ArchiveDateColumnLabel,
            viewModel.ArchiveSizeColumnLabel,
            viewModel.ArchiveStateColumnLabel,
            viewModel.ArchiveActionsColumnLabel,
            string.Empty);
        SetHeaders(
            ArchiveContentsGrid,
            viewModel.ArchivePathColumnLabel,
            viewModel.SizeLabel,
            viewModel.TypeColumnLabel,
            localization.Get("ModCardArchiveInstallationColumn"),
            localization.Get("ModCardArchiveWarningColumn"));
        SetHeaders(
            InstallationPlanGrid,
            viewModel.ArchivePathColumnLabel,
            viewModel.GamePathColumnLabel,
            viewModel.ActionColumnLabel,
            localization.Get("ModCardOverlapOwnerColumn"));
        SetHeaders(
            InstalledFilesGrid,
            viewModel.GamePathColumnLabel,
            viewModel.StateColumnLabel,
            "SHA-256",
            localization.Get("ModCardOriginalColumn"),
            localization.Get("ModCardVerificationColumn"));
    }

    private static void SetHeaders(
        System.Windows.Controls.DataGrid grid,
        params string[] headers)
    {
        for (var index = 0;
             index < headers.Length && index < grid.Columns.Count;
             index++)
        {
            grid.Columns[index].Header = headers[index];
        }
    }

    private void CloseButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        Close();

    private void CopyFingerprint_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is ModDetailsDialogViewModel viewModel)
            CopyText(viewModel.Fingerprint);
    }

    private void CopyArchiveEntryPath_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ArchiveContentsGrid.SelectedItem is
            ArchiveContentRowViewModel row)
        {
            CopyText(row.Path);
        }
    }

    private void CopyPlanGamePath_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (InstallationPlanGrid.SelectedItem is
            ModInstallPlanRowViewModel row)
        {
            CopyText(row.GamePath);
        }
    }

    private void CopyInstalledPath_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (InstalledFilesGrid.SelectedItem is
            InstalledFileRowViewModel row)
        {
            CopyText(row.GamePath);
        }
    }

    private void CopyInstalledHash_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (InstalledFilesGrid.SelectedItem is
            InstalledFileRowViewModel row)
        {
            CopyText(row.InstalledHash);
        }
    }

    private void OpenArchiveFolder_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is ModDetailsDialogViewModel viewModel &&
            viewModel.Mod.OpenFolderCommand.CanExecute(null))
        {
            viewModel.Mod.OpenFolderCommand.Execute(null);
        }
    }

    private void ArchiveActionsButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                ContextMenu: { } menu
            } element)
        {
            menu.PlacementTarget = element;
            menu.IsOpen = true;
        }
    }

    private void ArchiveActionMenuButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                ContextMenu: { } menu
            } element)
        {
            return;
        }
        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private static void CopyText(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != "—")
            Clipboard.SetText(value);
    }
}
