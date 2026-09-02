using System.Windows;
using RipperWorks.App.Services;
using RipperWorks.Core;

namespace RipperWorks.App;

public partial class RemovalConfirmationWindow : Window
{
    public RemovalConfirmationWindow(
        string modName,
        ModRemovalPlan plan,
        LocalizationService localization)
    {
        InitializeComponent();
        DataContext = new
        {
            Title = localization.Get("RemovalConfirmationTitle"),
            Question = string.Format(
                localization.Get("RemovalConfirmationQuestion"),
                modName),
            DeleteSummary = string.Format(
                localization.Get("RemovalDeleteSummary"),
                plan.FilesToDelete),
            RestoreSummary = string.Format(
                localization.Get("RemovalRestoreSummary"),
                plan.FilesToRestore),
            ArchiveNotice = localization.Get("RemovalArchiveNotice"),
            CancelLabel = localization.Get("Cancel"),
            RemoveLabel = localization.Get("RemoveMod")
        };
    }

    private void CancelButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = false;

    private void RemoveButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = true;
}
