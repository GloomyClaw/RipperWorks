namespace RipperWorks.Core;

public enum PackageRelationType
{
    Requires = 0,
    AddOnOf = 1
}

public enum PackageRelationSource
{
    User = 0,
    DetectedOverlap = 1
}

public enum DependencyHealth
{
    Satisfied = 0,
    Missing = 1
}

public sealed record PackageRelationRecord
{
    public long RelationId { get; init; }
    public required PackageId FromPackageId { get; init; }
    public required PackageId ToPackageId { get; init; }
    public PackageRelationType RelationType { get; init; }
    public PackageRelationSource Source { get; init; }
    public bool IsConfirmed { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed record PackageRelationView
{
    public required PackageRelationRecord Relation { get; init; }
    public required string FromDisplayName { get; init; }
    public required string ToDisplayName { get; init; }
    public PackageInstallationState FromInstallationState { get; init; }
    public PackageInstallationState ToInstallationState { get; init; }
    public bool FromArchivePresent { get; init; }
    public bool ToArchivePresent { get; init; }
}

public sealed record ManagedPathRecord
{
    public required string RelativePath { get; init; }
    public bool BaseFileExisted { get; init; }
    public string? BaseContentHash { get; init; }
}

public sealed record ManagedPathLayerRecord
{
    public required string RelativePath { get; init; }
    public int LayerOrder { get; init; }
    public required PackageId PackageId { get; init; }
    public required string ContentHash { get; init; }
    public required string OperationId { get; init; }
    public DateTime InstalledAtUtc { get; init; }
}

public sealed record RelationMutationResult(
    bool Success,
    string? ErrorCode = null,
    PackageRelationRecord? Relation = null);
