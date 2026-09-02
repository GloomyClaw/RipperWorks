namespace RipperWorks.Downloader;

public sealed record NexusRequirementSnapshotRecord(
    NexusRequirementSnapshotOwner Owner,
    long SuccessfulGeneration,
    DateTimeOffset? LastSuccessfulAtUtc,
    DateTimeOffset LastAttemptAtUtc,
    NexusRequirementFailure? LatestFailure,
    NexusRequirementSnapshot? Snapshot)
{
    public bool HasSuccessfulSnapshot => Snapshot is not null;
}

internal sealed record NexusRequirementSnapshotStateRow(
    long SuccessfulGeneration,
    DateTimeOffset? LastSuccessfulAtUtc,
    DateTimeOffset LastAttemptAtUtc,
    NexusRequirementFailure? LatestFailure);
