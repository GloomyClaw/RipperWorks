using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed class DownloaderArchiveMetadataLookup(
    IDownloaderRepository repository) : IDownloaderArchiveMetadataLookup
{
    public async Task<DownloaderArchiveMetadata?> FindAsync(
        string gameDomain,
        long nexusModId,
        long nexusFileId,
        string? sha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        var candidates = (await repository.LoadEntriesAsync(cancellationToken))
            .Where(entry =>
                entry.Source == DownloaderSource.Nexus &&
                string.Equals(
                    entry.GameDomain,
                    gameDomain,
                    StringComparison.OrdinalIgnoreCase) &&
                entry.NexusModId == nexusModId &&
                entry.NexusFileId == nexusFileId)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            candidates = candidates
                .Where(entry =>
                    string.IsNullOrWhiteSpace(entry.Sha256) ||
                    string.Equals(
                        entry.Sha256,
                        sha256,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var match = candidates
            .OrderByDescending(entry =>
                !string.IsNullOrWhiteSpace(entry.Sha256) &&
                string.Equals(
                    entry.Sha256,
                    sha256,
                    StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.UpdatedAt)
            .FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(entry.Version));
        return match is null
            ? null
            : new(match.Version.Trim(), NullIfWhiteSpace(match.Sha256));
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
