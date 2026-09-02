using System.Windows;
using RipperWorks.App.Services;
using RipperWorks.Core;

namespace RipperWorks.App;

public partial class InstallConfirmationWindow : Window
{
    public InstallConfirmationWindow(
        string modName,
        InstallPlan plan,
        LocalizationService localization)
    {
        InitializeComponent();
        DataContext = new
        {
            Title = localization.Get("InstallConfirmationTitle"),
            Question = string.Format(
                localization.Get("InstallConfirmationQuestion"),
                modName),
            AddSummary = string.Format(
                localization.Get("PlanAddSummary"),
                plan.AddCount),
            ReplaceSummary = string.Format(
                localization.Get("PlanReplaceSummary"),
                plan.ReplaceExistingCount),
            OverlaySummary = string.Format(
                localization.Get("PlanOverlaySummary"),
                plan.OverlayCount),
            ConflictSummary = string.Format(
                localization.Get("PlanConflictSummary"),
                plan.ConflictCount),
            CancelLabel = localization.Get("Cancel"),
            InstallLabel = localization.Get("Install")
        };
    }

    private void CancelButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = false;

    private void InstallButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        DialogResult = true;
}
