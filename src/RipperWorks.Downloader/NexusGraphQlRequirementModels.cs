using System.Collections.ObjectModel;
using System.Net;

namespace RipperWorks.Downloader;

public readonly record struct NexusModIdentity(long GameId, long ModId);

public enum NexusRequirementTraversal
{
    ForwardRequirements = 0,
    ReverseRequiredBy = 1
}

public enum NexusRequirementTargetKind
{
    NexusMod = 0,
    External = 1
}

public abstract record NexusRequirementTarget
{
    public abstract NexusRequirementTargetKind Kind { get; }
}

public sealed record NexusModRequirementTarget(
    NexusModIdentity Identity,
    string? DisplayName,
    string? ProviderUrl,
    Uri? ClickableUrl) : NexusRequirementTarget
{
    public override NexusRequirementTargetKind Kind =>
        NexusRequirementTargetKind.NexusMod;
}

public sealed record NexusExternalRequirementTarget(
    string? DisplayName,
    string? ProviderUrl,
    Uri? ClickableUrl) : NexusRequirementTarget
{
    public override NexusRequirementTargetKind Kind =>
        NexusRequirementTargetKind.External;
}

public sealed record NexusModRequirementEndpointMetadata(
    string? DisplayName,
    string? ProviderUrl,
    Uri? ClickableUrl);

public readonly record struct NexusRequirementCanonicalKey(
    NexusModIdentity Source,
    NexusRequirementTargetKind TargetKind,
    NexusModIdentity? NexusTargetIdentity);

public sealed record NexusModRequirementEdge(
    NexusModIdentity Source,
    NexusRequirementTarget Target,
    NexusModRequirementEndpointMetadata? SourceMetadata,
    string? ProviderRequirementId,
    string? Notes,
    NexusRequirementTraversal ObservedThrough)
{
    public NexusRequirementCanonicalKey? CanonicalKey =>
        Target is NexusModRequirementTarget nexusTarget
            ? new(
                Source,
                NexusRequirementTargetKind.NexusMod,
                nexusTarget.Identity)
            : null;
}

public sealed record NexusRequirementSnapshotOwner(
    NexusModIdentity QueriedMod,
    NexusRequirementTraversal Traversal);

public sealed record NexusRequirementSnapshot
{
    public NexusRequirementSnapshot(
        NexusRequirementSnapshotOwner owner,
        IEnumerable<NexusModRequirementEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        Owner = owner;
        Edges = new ReadOnlyCollection<NexusModRequirementEdge>(
            edges.ToArray());
    }

    public NexusRequirementSnapshotOwner Owner { get; }
    public IReadOnlyList<NexusModRequirementEdge> Edges { get; }
}

public enum NexusRequirementFailureKind
{
    Transport = 0,
    Http = 1,
    InvalidJson = 2,
    GraphQl = 3,
    PartialGraphQl = 4,
    MalformedIdentity = 5,
    Pagination = 6,
    Cancelled = 7
}

public sealed record NexusRequirementFailure(
    NexusRequirementFailureKind Kind,
    string Message,
    HttpStatusCode? StatusCode = null);

public sealed record NexusRequirementQueryResult
{
    private NexusRequirementQueryResult(
        NexusRequirementSnapshot? snapshot,
        NexusRequirementFailure? failure,
        bool? legacyModRequirementsEnabled = true)
    {
        Snapshot = snapshot;
        Failure = failure;
        LegacyModRequirementsEnabled = legacyModRequirementsEnabled;
    }

    public bool IsComplete => Snapshot is not null && Failure is null;
    public NexusRequirementSnapshot? Snapshot { get; }
    public NexusRequirementFailure? Failure { get; }
    public bool? LegacyModRequirementsEnabled { get; }

    public static NexusRequirementQueryResult Complete(
        NexusRequirementSnapshot snapshot,
        bool? legacyModRequirementsEnabled = true) =>
        new(snapshot, null, legacyModRequirementsEnabled);

    public static NexusRequirementQueryResult Modern(
        bool legacyModRequirementsEnabled = false) =>
        new(null, null, legacyModRequirementsEnabled);

    public static NexusRequirementQueryResult Failed(
        NexusRequirementFailure failure,
        bool? legacyModRequirementsEnabled = null) =>
        new(null, failure, legacyModRequirementsEnabled);
}

public sealed record NexusGraphQlRequirementClientOptions
{
    public static readonly Uri DefaultEndpoint =
        new("https://api.nexusmods.com/v2/graphql");

    public Uri Endpoint { get; init; } = DefaultEndpoint;
    public int PageSize { get; init; } = 50;
    public int MaximumPageCount { get; init; } = 100;
    public int MaximumNodeCount { get; init; } = 5_000;
}
