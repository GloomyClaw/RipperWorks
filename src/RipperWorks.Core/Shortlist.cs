namespace RipperWorks.Core;

public static class RipperWorksShortlistSchema
{
    public const int CurrentVersion = 1;
}

public readonly record struct ShortlistEntryKey
{
    public string GameDomain { get; }
    public long NexusModId { get; }

    public ShortlistEntryKey(string gameDomain, long nexusModId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        if (nexusModId <= 0)
            throw new ArgumentOutOfRangeException(nameof(nexusModId), "Nexus mod ID must be positive.");

        GameDomain = gameDomain.Trim().ToLowerInvariant();
        NexusModId = nexusModId;
    }

    public static ShortlistEntryKey ForCyberpunk(long nexusModId) =>
        new("cyberpunk2077", nexusModId);

    public bool EqualsCanonical(ShortlistEntryKey other) =>
        NexusModId == other.NexusModId &&
        string.Equals(GameDomain, other.GameDomain, StringComparison.Ordinal);

    public override string ToString() => $"{GameDomain}/{NexusModId}";
}

public sealed record ShortlistEntryRecord
{
    public required string GameDomain { get; init; }
    public required long NexusModId { get; init; }
    public required DateTimeOffset AddedAtUtc { get; init; }
    public string? Name { get; init; }
    public string? Author { get; init; }
    public string? LastKnownVersion { get; init; }

    public ShortlistEntryKey Key => new(GameDomain, NexusModId);
}

public sealed record ShortlistDocument
{
    public int FormatVersion { get; init; } = RipperWorksShortlistSchema.CurrentVersion;
    public IReadOnlyList<ShortlistEntryRecord> Items { get; init; } = [];
}

public interface IShortlistStore
{
    string FilePath { get; }

    Task<ShortlistDocument> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ShortlistDocument document,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShortlistEntryRecord>> LoadEntriesAsync(
        CancellationToken cancellationToken = default);

    Task<bool> AddAsync(
        string gameDomain,
        long nexusModId,
        string? name = null,
        string? author = null,
        string? lastKnownVersion = null,
        DateTimeOffset? addedAtUtc = null,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        string gameDomain,
        long nexusModId,
        CancellationToken cancellationToken = default);

    Task<bool> ContainsAsync(
        string gameDomain,
        long nexusModId,
        CancellationToken cancellationToken = default);
}
