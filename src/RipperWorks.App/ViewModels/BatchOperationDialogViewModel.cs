using RipperWorks.App.Services;
using RipperWorks.Organizer;

namespace RipperWorks.App.ViewModels;

public sealed class BatchOperationDialogViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private bool _isPlan = true;
    private bool _isProgress;
    private bool _isResult;
    private int _completed;
    private int _total;
    private int _successful;
    private int _failed;
    private int _remaining;
    private string _currentMod = "—";
    private BatchOperationResult? _result;

    private BatchOperationDialogViewModel(
        BatchOperationKind kind,
        LocalizationService localization)
    {
        Kind = kind;
        _localization = localization;
    }

    public static BatchOperationDialogViewModel ForInstall(
        BatchInstallPlan plan,
        LocalizationService localization)
    {
        var value = new BatchOperationDialogViewModel(
            BatchOperationKind.Install,
            localization)
        {
            InstallPlan = plan,
            PlanRows = plan.Items.Select((item, index) =>
                BatchPlanRowViewModel.FromInstall(
                    item,
                    index + 1,
                    localization)).ToArray(),
            CycleText = FormatCycle(plan.CyclePackages, localization)
        };
        return value;
    }

    public static BatchOperationDialogViewModel ForRemoval(
        BatchRemovalPlan plan,
        LocalizationService localization)
    {
        var value = new BatchOperationDialogViewModel(
            BatchOperationKind.Remove,
            localization)
        {
            RemovalPlan = plan,
            PlanRows = plan.Items.Select((item, index) =>
                BatchPlanRowViewModel.FromRemoval(
                    item,
                    index + 1,
                    localization)).ToArray(),
            CycleText = FormatCycle(plan.CyclePackages, localization)
        };
        return value;
    }

    public BatchOperationKind Kind { get; }
    public BatchInstallPlan? InstallPlan { get; private init; }
    public BatchRemovalPlan? RemovalPlan { get; private init; }
    public IReadOnlyList<BatchPlanRowViewModel> PlanRows
        { get; private init; } = [];
    public IReadOnlyList<BatchResultRowViewModel> ResultRows
        { get; private set; } = [];
    public string CycleText { get; private init; } = string.Empty;
    public bool HasCycle => !string.IsNullOrWhiteSpace(CycleText);
    public bool IsInstall => Kind == BatchOperationKind.Install;
    public bool IsRemoval => Kind == BatchOperationKind.Remove;
    public bool IsPlan
    {
        get => _isPlan;
        private set => SetProperty(ref _isPlan, value);
    }
    public bool IsProgress
    {
        get => _isProgress;
        private set => SetProperty(ref _isProgress, value);
    }
    public bool IsResult
    {
        get => _isResult;
        private set => SetProperty(ref _isResult, value);
    }
    public bool CanExecute =>
        InstallPlan?.CanExecute ??
        RemovalPlan?.CanExecute ??
        false;
    public string Title => _localization.Get(IsInstall
        ? "BatchInstallationTitle"
        : "BatchRemovalTitle");
    public string PlanSummary => IsInstall
        ? string.Format(
            _localization.Get("BatchInstallPlanSummary"),
            InstallPlan!.SelectedCount,
            InstallPlan.InstallCount,
            InstallPlan.AlreadyInstalledCount,
            InstallPlan.NotReadyCount)
        : string.Format(
            _localization.Get("BatchRemovalPlanSummary"),
            RemovalPlan!.SelectedCount,
            RemovalPlan.RemoveCount,
            RemovalPlan.NotInstalledCount);
    public string ExecuteLabel => IsInstall
        ? string.Format(
            _localization.Get("BatchInstallCount"),
            InstallPlan!.InstallCount)
        : string.Format(
            _localization.Get("BatchRemoveCount"),
            RemovalPlan!.RemoveCount);
    public string CancelLabel => _localization.Get("Cancel");
    public string CloseLabel => _localization.Get("Close");
    public string CancelAfterCurrentLabel =>
        _localization.Get("BatchCancelAfterCurrent");
    public string NumberHeader => "№";
    public string NameHeader => _localization.Get("ColumnName");
    public string StateHeader => _localization.Get("BatchColumnState");
    public string AddHeader => _localization.Get("BatchColumnAdd");
    public string ReplaceHeader => _localization.Get("BatchColumnReplace");
    public string ConflictsHeader =>
        _localization.Get("ModCardConflicts");
    public string BlockedHeader => _localization.Get("ModCardBlocked");
    public string ManagedFilesHeader =>
        _localization.Get("BatchColumnManagedFiles");
    public string FilesToDeleteHeader =>
        _localization.Get("BatchColumnFilesToDelete");
    public string FilesToRestoreHeader =>
        _localization.Get("BatchColumnFilesToRestore");
    public string RelatedHeader =>
        _localization.Get("BatchColumnRelated");
    public string ReasonHeader => _localization.Get("PlanReason");
    public string CurrentModLabel => string.Format(
        _localization.Get("BatchCurrentMod"),
        CurrentMod);
    public string ProgressText => string.Format(
        _localization.Get("BatchCompletedProgress"),
        Completed,
        Total);
    public string ProgressCounters => string.Format(
        _localization.Get(IsInstall
            ? "BatchInstallProgressCounters"
            : "BatchRemovalProgressCounters"),
        Successful,
        Failed,
        Remaining);
    public double ProgressValue => Total <= 0
        ? 0
        : Math.Clamp(Completed * 100d / Total, 0, 100);
    public int Completed
    {
        get => _completed;
        private set => SetProperty(ref _completed, value);
    }
    public int Total
    {
        get => _total;
        private set => SetProperty(ref _total, value);
    }
    public int Successful
    {
        get => _successful;
        private set => SetProperty(ref _successful, value);
    }
    public int Failed
    {
        get => _failed;
        private set => SetProperty(ref _failed, value);
    }
    public int Remaining
    {
        get => _remaining;
        private set => SetProperty(ref _remaining, value);
    }
    public string CurrentMod
    {
        get => _currentMod;
        private set => SetProperty(ref _currentMod, value);
    }
    public string ResultTitle => _localization.Get(IsInstall
        ? "BatchInstallationCompleted"
        : "BatchRemovalCompleted");
    public string ResultSummary => _result is null
        ? string.Empty
        : string.Format(
            _localization.Get("BatchOperationSummary"),
            _result.SuccessfulCount,
            _result.FailedCount,
            _result.NotStartedCount,
            _result.SkippedCount);

    public void Begin()
    {
        IsPlan = false;
        IsProgress = true;
        var firstItem = InstallPlan?.ExecutionOrder.FirstOrDefault()?.Candidate.DisplayName ??
            RemovalPlan?.ExecutionOrder.FirstOrDefault()?.Candidate.DisplayName;
        if (!string.IsNullOrWhiteSpace(firstItem))
        {
            CurrentMod = firstItem;
        }
        Total = InstallPlan?.ExecutionOrder.Count ??
            RemovalPlan?.ExecutionOrder.Count ??
            0;
        Remaining = Total;
        Completed = 0;
        Successful = 0;
        Failed = 0;
        NotifyProgress();
    }

    public void Update(BatchOperationProgress progress)
    {
        Completed = progress.Completed;
        Total = progress.Total;
        CurrentMod = progress.CurrentMod;
        Successful = progress.Successful;
        Failed = progress.Failed;
        Remaining = progress.Remaining;
        NotifyProgress();
    }

    public void Complete(BatchOperationResult result)
    {
        _result = result;
        IsProgress = false;
        IsResult = true;
        ResultRows = result.Items.Select(item =>
            new BatchResultRowViewModel(
                item.DisplayName,
                _localization.Get($"BatchState{item.State}"),
                item.Reason ?? string.Empty)).ToArray();
        OnPropertyChanged(nameof(ResultRows));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(CurrentModLabel));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressCounters));
        OnPropertyChanged(nameof(ProgressValue));
    }

    private static string FormatCycle(
        IReadOnlyList<string> cyclePackages,
        LocalizationService localization) =>
        cyclePackages.Count == 0
            ? string.Empty
            : localization.Get("BatchRelationCycle") +
              Environment.NewLine +
              string.Join(Environment.NewLine, cyclePackages.Select(
                  name => $"• {name}"));
}

public sealed record BatchPlanRowViewModel(
    int Number,
    string Name,
    string State,
    string Tone,
    int Add,
    int Replace,
    int Conflicts,
    int Blocked,
    int ManagedFiles,
    int FilesToDelete,
    int FilesToRestore,
    string Related)
{
    public static BatchPlanRowViewModel FromInstall(
        BatchInstallPlanItem item,
        int number,
        LocalizationService localization) =>
        new(
            number,
            item.Candidate.DisplayName,
            localization.Get($"BatchState{item.State}"),
            ToneFor(item.State),
            item.Plan?.AddCount ?? 0,
            item.Plan?.ReplaceExistingCount ?? 0,
            item.Plan?.ConflictCount ?? 0,
            item.Plan?.BlockedCount ?? 0,
            0,
            0,
            0,
            string.Empty);

    public static BatchPlanRowViewModel FromRemoval(
        BatchRemovalPlanItem item,
        int number,
        LocalizationService localization) =>
        new(
            number,
            item.Candidate.DisplayName,
            localization.Get($"BatchState{item.State}"),
            ToneFor(item.State),
            0,
            0,
            0,
            0,
            (item.Plan?.FilesToDelete ?? 0) +
            (item.Plan?.FilesToRestore ?? 0) +
            (item.Plan?.LayersToDetach ?? 0),
            item.Plan?.FilesToDelete ?? 0,
            item.Plan?.FilesToRestore ?? 0,
            string.Join(", ", item.RelatedPackages));

    private static string ToneFor(BatchItemState state) => state switch
    {
        BatchItemState.Ready or
        BatchItemState.Completed => "Success",
        BatchItemState.RequiresAnalysis or
        BatchItemState.NotStarted => "Warning",
        BatchItemState.AnalysisError or
        BatchItemState.InstallationBlocked or
        BatchItemState.RemovalBlocked or
        BatchItemState.Failed => "Error",
        _ => "Neutral"
    };
}

public sealed record BatchResultRowViewModel(
    string Name,
    string State,
    string Reason,
    string Tone)
{
    public BatchResultRowViewModel(
        string name,
        string state,
        string reason)
        : this(
            name,
            state,
            reason,
            state.Contains("ошиб", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("error", StringComparison.OrdinalIgnoreCase)
                ? "Error"
                : state.Contains("заверш", StringComparison.OrdinalIgnoreCase) ||
                  state.Contains("completed", StringComparison.OrdinalIgnoreCase)
                    ? "Success"
                    : "Neutral")
    {
    }
}
