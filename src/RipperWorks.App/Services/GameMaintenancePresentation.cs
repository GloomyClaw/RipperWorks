using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;
using RipperWorks.GameMaintenance;

namespace RipperWorks.App.Services;

public sealed class GameMaintenanceCleanupDialogViewModel : ObservableObject
{
    private bool _createBackup = true;

    public GameMaintenanceCleanupDialogViewModel(
        GameMaintenanceScanResult scan,
        LocalizationService localization)
    {
        Title = localization.Get("GameMaintenanceCleanupTitle");
        Heading = localization.Get("GameMaintenanceCleanupTitle");
        Subtitle = string.Format(
            localization.Get("GameMaintenanceCandidateFilesHeading"),
            scan.Candidates.Count);
        CandidatePaths = scan.Candidates.Select(c => c.RelativeGamePath).ToList();
        CreateBackupLabel = localization.Get("GameMaintenanceCreateBackupCheckbox");
        CleanLabel = localization.Get("GameMaintenanceCleanButton");
        CancelLabel = localization.Get("Cancel");
    }

    public string Title { get; }
    public string Heading { get; }
    public string Subtitle { get; }
    public IReadOnlyList<string> CandidatePaths { get; }
    public string CreateBackupLabel { get; }
    public string CleanLabel { get; }
    public string CancelLabel { get; }

    public bool CreateBackup
    {
        get => _createBackup;
        set => SetProperty(ref _createBackup, value);
    }
}

public sealed class GameMaintenanceBackupItemViewModel(
    GameMaintenanceBackupHeader header,
    LocalizationService localization) : ObservableObject
{
    public GameMaintenanceBackupHeader Header { get; } = header;
    public string BackupId => Header.BackupId;
    public int FileCount => Header.FileCount;
    public string FormattedDate =>
        Header.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo(localization.CurrentLanguage));
    public string FormattedSize => FormatSize(Header.TotalSizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{(bytes / 1024.0):F1} KB";
        return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
    }
}

public sealed class GameMaintenanceRestoreDialogViewModel : ObservableObject
{
    private GameMaintenanceBackupItemViewModel? _selectedBackup;

    public GameMaintenanceRestoreDialogViewModel(
        IReadOnlyList<GameMaintenanceBackupHeader> backups,
        LocalizationService localization)
    {
        Title = localization.Get("GameMaintenanceRestoreTitle");
        Heading = localization.Get("GameMaintenanceRestoreTitle");
        Subtitle = localization.Get("GameMaintenanceAvailableBackupsHeading");
        Backups = new ObservableCollection<GameMaintenanceBackupItemViewModel>(
            backups.Select(b => new GameMaintenanceBackupItemViewModel(b, localization)));
        _selectedBackup = Backups.FirstOrDefault();
        RestoreLabel = localization.Get("GameMaintenanceRestoreButton");
        CancelLabel = localization.Get("Cancel");
        DateHeader = localization.Get("GameMaintenanceDateHeader");
        FilesHeader = localization.Get("GameMaintenanceFilesHeader");
        SizeHeader = localization.Get("GameMaintenanceSizeHeader");
        IdHeader = localization.Get("GameMaintenanceIdHeader");
    }

    public string Title { get; }
    public string Heading { get; }
    public string Subtitle { get; }
    public ObservableCollection<GameMaintenanceBackupItemViewModel> Backups { get; }
    public string RestoreLabel { get; }
    public string CancelLabel { get; }
    public string DateHeader { get; }
    public string FilesHeader { get; }
    public string SizeHeader { get; }
    public string IdHeader { get; }

    public GameMaintenanceBackupItemViewModel? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                OnPropertyChanged(nameof(CanRestore));
            }
        }
    }

    public bool CanRestore => SelectedBackup != null;
}

public interface IGameMaintenanceDialogService
{
    bool PromptCleanupPreview(
        GameMaintenanceScanResult scan,
        out bool createBackup);

    GameMaintenanceBackupHeader? PromptRestoreSelection(
        IReadOnlyList<GameMaintenanceBackupHeader> backups);

    void ShowConflicts(
        IReadOnlyList<GameMaintenanceRestoreConflict> conflicts);
}

public sealed class GameMaintenanceDialogService(
    LocalizationService localization,
    IUserDialogService dialogs) : IGameMaintenanceDialogService
{
    private readonly LocalizationService _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    private readonly IUserDialogService _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

    public bool PromptCleanupPreview(
        GameMaintenanceScanResult scan,
        out bool createBackup)
    {
        createBackup = true;
        var vm = new GameMaintenanceCleanupDialogViewModel(scan, _localization);
        var window = new GameMaintenanceCleanupWindow
        {
            DataContext = vm,
            Owner = Application.Current?.MainWindow
        };

        var result = window.ShowDialog() == true;
        if (!result)
            return false;

        createBackup = vm.CreateBackup;
        if (!createBackup)
        {
            var confirmed = _dialogs.Confirm(
                _localization.Get("GameMaintenanceNoBackupWarning"),
                _localization.Get("GameMaintenanceCleanupTitle"));
            if (!confirmed)
                return false;
        }

        return true;
    }

    public GameMaintenanceBackupHeader? PromptRestoreSelection(
        IReadOnlyList<GameMaintenanceBackupHeader> backups)
    {
        if (backups.Count == 0)
        {
            _dialogs.ShowInfo(_localization.Get("GameMaintenanceNoBackupsFound"));
            return null;
        }

        var vm = new GameMaintenanceRestoreDialogViewModel(backups, _localization);
        var window = new GameMaintenanceRestoreWindow
        {
            DataContext = vm,
            Owner = Application.Current?.MainWindow
        };

        var result = window.ShowDialog() == true;
        return result && vm.SelectedBackup != null ? vm.SelectedBackup.Header : null;
    }

    public void ShowConflicts(
        IReadOnlyList<GameMaintenanceRestoreConflict> conflicts)
    {
        var message = _localization.Get("GameMaintenanceRestoreConflictHeading") + Environment.NewLine +
                      _localization.Get("GameMaintenanceRestoreConflictMessage") + Environment.NewLine +
                      string.Join(Environment.NewLine, conflicts.Select(c => $"• {c.RelativeGamePath}"));

        _dialogs.ShowError(message);
    }
}
