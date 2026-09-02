using RipperWorks.App.Services;
using RipperWorks.Core;

namespace RipperWorks.App.ViewModels;

public sealed class InstallPlanDialogViewModel
{
    public InstallPlanDialogViewModel(
        InstallPlan plan,
        LocalizationService localization,
        bool allowInstall = false)
    {
        Plan = plan;
        Title = localization.Get("InstallPlanTitle");
        PathLabel = localization.Get("ColumnPath");
        ActionLabel = localization.Get("PlanAction");
        ReasonLabel = localization.Get("PlanReason");
        AddSummary = string.Format(
            localization.Get("PlanAddSummary"),
            plan.AddCount);
        ReplaceSummary = string.Format(
            localization.Get("PlanReplaceSummary"),
            plan.ReplaceExistingCount);
        OverlaySummary = string.Format(
            localization.Get("PlanOverlaySummary"),
            plan.OverlayCount);
        ConflictSummary = string.Format(
            localization.Get("PlanConflictSummary"),
            plan.ConflictCount);
        BlockedSummary = string.Format(
            localization.Get("PlanBlockedSummary"),
            plan.BlockedCount);
        Disclaimer = localization.Get("InstallPlanDisclaimer");
        FutureInstallLabel = localization.Get("FutureInstall");
        InstallLabel = localization.Get("Install");
        CloseLabel = localization.Get("Close");
        ErrorMessage = string.IsNullOrWhiteSpace(plan.ErrorCode)
            ? string.Empty
            : localization.Get($"PlanError{plan.ErrorCode}");
        Entries = plan.Entries
            .Select(entry => new InstallPlanEntryViewModel(
                entry.RelativeGamePath,
                localization.Get($"PlanAction{entry.Action}"),
                LocalizeReason(entry.Reason, localization),
                entry.Action switch
                {
                    InstallPlanAction.Add => "Success",
                    InstallPlanAction.ReplaceExisting => "Warning",
                    InstallPlanAction.OverlayMod => "Overlay",
                    InstallPlanAction.Conflict => "Conflict",
                    InstallPlanAction.Blocked => "Error",
                    InstallPlanAction.AlreadyInstalled => "Success",
                    _ => "Neutral"
                }))
            .ToArray();
        CanInstall = allowInstall &&
            string.IsNullOrWhiteSpace(plan.ErrorCode) &&
            plan.Entries.Count > 0 &&
            plan.ConflictCount == 0 &&
            plan.BlockedCount == 0;
    }

    public InstallPlan Plan { get; }
    public IReadOnlyList<InstallPlanEntryViewModel> Entries { get; }
    public string Title { get; }
    public string PathLabel { get; }
    public string ActionLabel { get; }
    public string ReasonLabel { get; }
    public string AddSummary { get; }
    public string ReplaceSummary { get; }
    public string OverlaySummary { get; }
    public string ConflictSummary { get; }
    public string BlockedSummary { get; }
    public string Disclaimer { get; }
    public string FutureInstallLabel { get; }
    public string InstallLabel { get; }
    public string CloseLabel { get; }
    public string ErrorMessage { get; }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool CanInstall { get; }

    private static string LocalizeReason(
        string? reason,
        LocalizationService localization)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return string.Empty;
        var localized = localization.Get($"PlanReason{reason}");
        return localized.StartsWith(
            "PlanReason",
            StringComparison.Ordinal)
                ? reason
                : localized;
    }
}

public sealed record InstallPlanEntryViewModel(
    string Path,
    string Action,
    string Reason,
    string Tone);
