using RipperWorks.Core;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public sealed class NexusModLocalStateService : INexusModLocalStateService
{
    private static readonly IReadOnlyList<string> EmptyVersions = Array.AsReadOnly(Array.Empty<string>());

    private readonly IShortlistStore _shortlistStore;
    private readonly IDownloaderRepository _downloaderRepository;
    private readonly OrganizerRepository _organizerRepository;

    public NexusModLocalStateService(
        IShortlistStore shortlistStore,
        IDownloaderRepository downloaderRepository,
        OrganizerRepository organizerRepository)
    {
        _shortlistStore = shortlistStore ?? throw new ArgumentNullException(nameof(shortlistStore));
        _downloaderRepository = downloaderRepository ?? throw new ArgumentNullException(nameof(downloaderRepository));
        _organizerRepository = organizerRepository ?? throw new ArgumentNullException(nameof(organizerRepository));
    }

    public async Task<NexusModLocalState> QueryAsync(
        string gameDomain,
        long nexusModId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        if (nexusModId <= 0)
            throw new ArgumentOutOfRangeException(nameof(nexusModId), "Nexus mod ID must be positive.");

        var canonicalDomain = gameDomain.Trim().ToLowerInvariant();

        var shortlistTask = _shortlistStore.ContainsAsync(canonicalDomain, nexusModId, cancellationToken);
        var downloaderTask = _downloaderRepository.LoadEntriesAsync(cancellationToken);
        var libraryTask = _organizerRepository.LoadLibraryModsAsync(cancellationToken);

        await Task.WhenAll(shortlistTask, downloaderTask, libraryTask).ConfigureAwait(false);

        var isShortlisted = await shortlistTask.ConfigureAwait(false);
        var allDownloaderEntries = await downloaderTask.ConfigureAwait(false);
        var allLibraryMods = await libraryTask.ConfigureAwait(false);

        var matchingDownloads = allDownloaderEntries
            .Where(e => e.Source == DownloaderSource.Nexus &&
                        e.NexusModId == nexusModId &&
                        string.Equals(e.GameDomain?.Trim(), canonicalDomain, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var isInDownloader = matchingDownloads.Count > 0;
        var isDownloaded = matchingDownloads.Any(e => e.Status == DownloaderStatus.Downloaded);

        var matchingMod = allLibraryMods.FirstOrDefault(m =>
            m.Source == PackageSource.Nexus &&
            m.NexusModId == nexusModId &&
            string.Equals(m.GameDomain?.Trim(), canonicalDomain, StringComparison.OrdinalIgnoreCase));

        var isInLibrary = matchingMod is not null;
        var localVersions = matchingMod is not null
            ? Array.AsReadOnly(matchingMod.Archives
                .Select(a => a.EffectiveVersion)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray())
            : EmptyVersions;

        var isInstalled = matchingMod is not null && matchingMod.InstalledArchives.Count > 0;

        string? installedVersion = null;
        if (matchingMod is not null && isInstalled)
        {
            var distinctInstalledVersions = matchingMod.InstalledArchives
                .Select(a => a.EffectiveVersion)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctInstalledVersions.Count == 1)
            {
                installedVersion = distinctInstalledVersions[0];
            }
        }

        return new NexusModLocalState
        {
            GameDomain = canonicalDomain,
            NexusModId = nexusModId,
            IsShortlisted = isShortlisted,
            IsInDownloader = isInDownloader,
            IsDownloaded = isDownloaded,
            IsInLibrary = isInLibrary,
            LocalVersions = localVersions,
            IsInstalled = isInstalled,
            InstalledVersion = installedVersion
        };
    }
}
