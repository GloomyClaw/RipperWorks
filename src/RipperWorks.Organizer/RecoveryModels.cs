using RipperWorks.Core;

namespace RipperWorks.Organizer;

public enum RecoveryOperationKind
{
    Unknown,
    Install,
    Remove,
    VersionSwitch
}

public enum RecoveryPathActionKind
{
    Delete,
    RestoreContent,
    RestoreRemovalContent
}

public enum VersionSwitchRecoveryAction
{
    None,
    VerifyOriginalSource,
    RecoverSourceRemoval,
    RestoreOriginalSource,
    RollbackTargetAndRestoreOriginalSource
}

public sealed record RecoveryPathAction(
    string RelativeGamePath,
    RecoveryPathActionKind Action,
    string? TargetContentHash,
    IReadOnlyList<string> AllowedCurrentContentHashes);

public sealed record RecoverySourceLayerPlan(
    string RelativeGamePath,
    string SourceContentHash,
    string? PreviousContentHash,
    bool PreviousFileExisted,
    int InsertionOrder,
    string DesiredLiveContentHash);

public sealed record RecoveryPlan
{
    public required Guid OperationId { get; init; }
    public RecoveryOperationKind Kind { get; init; }
    public IReadOnlyList<PackageId> PackageIds { get; init; } = [];
    public PackageId? SourcePackageId { get; init; }
    public PackageId? TargetPackageId { get; init; }
    public Guid? NestedRecoveryOperationId { get; init; }
    public RecoveryOperationKind NestedRecoveryKind { get; init; }
    public PackageId? NestedRecoveryPackageId { get; init; }
    public VersionSwitchRecoveryAction VersionSwitchAction { get; init; }
    public bool SourceAlreadyRestored { get; init; }
    public string? ApprovedLibraryRoot { get; init; }
    public InstallOperationStatus DurableStatus { get; init; }
    public InstallOperationPhase DurablePhase { get; init; }
    public string DesiredState { get; init; } = string.Empty;
    public IReadOnlyList<RecoveryPathAction> PathActions { get; init; } = [];
    public IReadOnlyList<RecoverySourceLayerPlan> SourceLayers { get; init; } = [];
    public IReadOnlyList<string> EvidenceDependencies { get; init; } = [];
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public bool IsDeterministic { get; init; }
    public bool AlreadyRecovered { get; init; }
    public string ValidationToken { get; init; } = string.Empty;
}

public sealed record RecoveryResult
{
    public required Guid OperationId { get; init; }
    public bool Success { get; init; }
    public bool AlreadyRecovered { get; init; }
    public string? ErrorCode { get; init; }
    public string? FailedPath { get; init; }
    public RecoveryPlan? Plan { get; init; }
}
