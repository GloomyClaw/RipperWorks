using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public sealed class NexusRequirementAvailabilityResolver(
    OrganizerRepository organizer,
    IDownloaderRepository downloader)
{
    private readonly OrganizerRepository _organizer =
        organizer ?? throw new ArgumentNullException(nameof(organizer));
    private readonly IDownloaderRepository _downloader =
        downloader ?? throw new ArgumentNullException(nameof(downloader));

    public async Task<NexusRequirementLocalState> ResolveAsync(
        NexusModIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var states = await ResolveManyAsync([identity], cancellationToken)
            .ConfigureAwait(false);
        return states[identity];
    }

    public async Task<IReadOnlyDictionary<NexusModIdentity, NexusRequirementLocalState>>
        ResolveManyAsync(
            IEnumerable<NexusModIdentity> identities,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var requested = identities.Distinct().ToArray();
        var result = new Dictionary<NexusModIdentity, NexusRequirementLocalState>();
        var supported = new List<(NexusModIdentity Identity, string Domain)>();
        foreach (var identity in requested)
        {
            if (NexusGameIdentityBridge.TryGetGameDomain(
                    identity.GameId,
                    out var domain))
            {
                supported.Add((identity, domain));
            }
            else
            {
                result[identity] = new(
                    NexusRequirementLocalAvailability.Missing,
                    null);
            }
        }

        if (supported.Count == 0)
            return result;

        var libraryTask = _organizer.LoadLibraryModsAsync(cancellationToken);
        var downloaderTask = _downloader.LoadEntriesAsync(cancellationToken);
        await Task.WhenAll(libraryTask, downloaderTask).ConfigureAwait(false);
        var library = await libraryTask.ConfigureAwait(false);
        var downloads = await downloaderTask.ConfigureAwait(false);

        foreach (var request in supported)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchingMods = library.Where(mod =>
                mod.Source == PackageSource.Nexus &&
                mod.NexusModId == request.Identity.ModId &&
                DomainsEqual(mod.GameDomain, request.Domain)).ToArray();
            var navigationId = matchingMods.Length == 1
                ? matchingMods[0].LibraryModId
                : (LibraryModId?)null;
            if (matchingMods.Any(mod => mod.InstalledArchives.Count > 0))
            {
                result[request.Identity] = new(
                    NexusRequirementLocalAvailability.Installed,
                    navigationId,
                    matchingMods[0].DisplayName);
                continue;
            }
            if (matchingMods.Length > 0)
            {
                result[request.Identity] = new(
                    NexusRequirementLocalAvailability.InLibrary,
                    navigationId,
                    matchingMods[0].DisplayName);
                continue;
            }
            var matchingDownloads = downloads.Where(entry =>
                    entry.Source == DownloaderSource.Nexus &&
                    entry.NexusModId == request.Identity.ModId &&
                    DomainsEqual(entry.GameDomain, request.Domain)).ToArray();
            if (matchingDownloads.Length > 0)
            {
                result[request.Identity] = new(
                    NexusRequirementLocalAvailability.InDownloads,
                    null,
                    matchingDownloads[0].Name);
                continue;
            }
            result[request.Identity] = new(
                NexusRequirementLocalAvailability.Missing,
                null);
        }

        return result;
    }

    internal static bool DomainsEqual(string? candidate, string expected) =>
        string.Equals(
            candidate?.Trim(),
            expected,
            StringComparison.OrdinalIgnoreCase);
}
