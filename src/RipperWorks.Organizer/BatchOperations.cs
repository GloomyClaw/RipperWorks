using RipperWorks.Core;

namespace RipperWorks.Organizer;

public enum BatchOperationKind
{
    Install,
    Remove
}

public enum BatchItemState
{
    Ready,
    AlreadyInstalled,
    RequiresAnalysis,
    AnalysisError,
    ArchiveMissing,
    InstallationBlocked,
    NotInstalled,
    RemovalBlocked,
    Completed,
    Failed,
    NotStarted,
    Skipped
}

public sealed record BatchPackageCandidate(
    OrganizerPackageRecord Package,
    string DisplayName,
    int VisualOrder);

public sealed record BatchInstallPlanItem
{
    public required BatchPackageCandidate Candidate { get; init; }
    public BatchItemState State { get; init; }
    public InstallPlan? Plan { get; init; }
    public string? Reason { get; init; }
    public bool CanInstall => State == BatchItemState.Ready;
}

public sealed record RelationDecisionRequirement(
    PackageId ChildPackageId,
    string ChildDisplayName,
    PackageId ParentPackageId,
    string ParentDisplayName,
    int OverlappingFileCount);

public sealed record BatchInstallPlan
{
    public required IReadOnlyList<BatchInstallPlanItem> Items { get; init; }
    public IReadOnlyList<BatchInstallPlanItem> ExecutionOrder { get; init; } =
        [];
    public IReadOnlyList<string> CyclePackages { get; init; } = [];
    public IReadOnlyList<RelationDecisionRequirement> Decisions { get; init; } =
        [];
    public string ValidationToken { get; init; } = string.Empty;
    public bool HasCycle => CyclePackages.Count > 0;
    public bool CanExecute =>
        !HasCycle &&
        Decisions.Count == 0 &&
        Items.All(item => item.State is
            BatchItemState.Ready or BatchItemState.AlreadyInstalled) &&
        ExecutionOrder.Count > 0;
    public int SelectedCount => Items.Count;
    public int InstallCount => Items.Count(item => item.CanInstall);
    public int AlreadyInstalledCount => Items.Count(item =>
        item.State == BatchItemState.AlreadyInstalled);
    public int NotReadyCount =>
        Items.Count - InstallCount - AlreadyInstalledCount;
}

public sealed record BatchRemovalPlanItem
{
    public required BatchPackageCandidate Candidate { get; init; }
    public BatchItemState State { get; init; }
    public ModRemovalPlan? Plan { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> RelatedPackages { get; init; } = [];
    public bool CanRemove => State == BatchItemState.Ready;
}

public sealed record BatchRelatedPackageGroup(
    BatchPackageCandidate Owner,
    IReadOnlyList<BatchPackageCandidate> Dependents);

public sealed record BatchRemovalPlan
{
    public required IReadOnlyList<BatchRemovalPlanItem> Items { get; init; }
    public IReadOnlyList<BatchRemovalPlanItem> ExecutionOrder { get; init; } =
        [];
    public IReadOnlyList<BatchRelatedPackageGroup> UnselectedRelated { get; init; } =
        [];
    public IReadOnlyList<string> CyclePackages { get; init; } = [];
    public string ValidationToken { get; init; } = string.Empty;
    public ModifiedFilesAuthorization? AuthorizedModifiedFiles { get; init; }
    public bool HasCycle => CyclePackages.Count > 0;
    public bool CanExecute =>
        !HasCycle &&
        UnselectedRelated.Count == 0 &&
        Items.All(item => item.State is
            BatchItemState.Ready or BatchItemState.NotInstalled) &&
        ExecutionOrder.Count > 0;
    public int SelectedCount => Items.Count;
    public int RemoveCount => Items.Count(item => item.CanRemove);
    public int NotInstalledCount => Items.Count(item =>
        item.State == BatchItemState.NotInstalled);
}

public sealed record BatchOperationProgress(
    BatchOperationKind Kind,
    int Completed,
    int Total,
    string CurrentMod,
    int Successful,
    int Failed,
    int Remaining);

public sealed record BatchPackageStateChanged(
    PackageId PackageId,
    PackageInstallationState InstallationState);

public sealed record BatchOperationItemResult(
    PackageId PackageId,
    string DisplayName,
    BatchItemState State,
    string? Reason = null);

public sealed record BatchOperationResult
{
    public BatchOperationKind Kind { get; init; }
    public required IReadOnlyList<BatchOperationItemResult> Items { get; init; }
    public int SuccessfulCount => Items.Count(item =>
        item.State == BatchItemState.Completed);
    public int FailedCount => Items.Count(item =>
        item.State == BatchItemState.Failed);
    public int NotStartedCount => Items.Count(item =>
        item.State == BatchItemState.NotStarted);
    public int SkippedCount => Items.Count(item =>
        item.State is not (
            BatchItemState.Completed or
            BatchItemState.Failed or
            BatchItemState.NotStarted));
}
