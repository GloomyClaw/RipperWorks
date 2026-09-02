namespace RipperWorks.Core;

public sealed record NexusModLocalState
{
    public required string GameDomain { get; init; }
    public required long NexusModId { get; init; }
    public bool IsShortlisted { get; init; }
    public bool IsInDownloader { get; init; }
    public bool IsDownloaded { get; init; }
    public bool IsInLibrary { get; init; }
    public IReadOnlyList<string> LocalVersions { get; init; } = Array.AsReadOnly(Array.Empty<string>());
    public bool IsInstalled { get; init; }
    public string? InstalledVersion { get; init; }
}

public interface INexusModLocalStateService
{
    Task<NexusModLocalState> QueryAsync(
        string gameDomain,
        long nexusModId,
        CancellationToken cancellationToken = default);
}
