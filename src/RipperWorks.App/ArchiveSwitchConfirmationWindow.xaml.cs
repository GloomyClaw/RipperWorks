using System.Windows;
using RipperWorks.App.Services;
using RipperWorks.Core;

namespace RipperWorks.App;

public partial class ArchiveSwitchConfirmationWindow : Window
{
    public ArchiveSwitchConfirmationWindow(
        string modName,
        string installedVersion,
        string targetVersion,
        ModArchiveSwitchPlan plan,
        LocalizationService localization)
    {
        InitializeComponent();
        var operationKey = plan.Operation switch
        {
            ArchiveVersionOperation.Rollback => "Rollback",
            ArchiveVersionOperation.Reinstall => "Reinstall",
            _ => "Update"
        };
        DataContext = new
        {
            Title = localization.Get($"ArchiveSwitch{operationKey}Title"),
            Heading = string.Format(
                localization.Get($"ArchiveSwitch{operationKey}Heading"),
                modName),
            InstalledVersion = string.Format(
                localization.Get("ArchiveSwitchInstalled"),
                installedVersion),
            TargetVersion = string.Format(
                localization.Get("ArchiveSwitchTarget"),
                targetVersion),
            AddSummary = string.Format(
                localization.Get("PlanAddSummary"),
                plan.FilesToAdd),
            ReplaceSummary = string.Format(
                localization.Get("PlanReplaceSummary"),
                plan.FilesToReplace),
            ObsoleteSummary = string.Format(
                localization.Get("ArchiveSwitchObsolete"),
                plan.ObsoleteFiles),
            RestoreSummary = string.Format(
                localization.Get("ArchiveSwitchRestored"),
                plan.LowerLayersToRestore),
            ConflictSummary = string.Format(
                localization.Get("ArchiveSwitchConflicts"),
                plan.ConflictCount),
            DeleteOldArchiveLabel =
                localization.Get("ArchiveSwitchDeleteOldArchive"),
            CancelLabel = localization.Get("Cancel"),
            ConfirmLabel = localization.Get(
                $"ArchiveSwitch{operationKey}Confirm")
        };
    }

    public bool DeleteOldArchive =>
        DeleteOldArchiveCheckBox.IsChecked == true;

    private void CancelButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = false;

    private void ConfirmButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = true;
}
