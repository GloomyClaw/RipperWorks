using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed class OrganizerLibraryArchiveLookup(
    OrganizerRepository repository) : ILibraryArchiveLookup
{
    public async Task<string?> FindArchivePathAsync(
        string gameDomain,
        long nexusModId,
        long nexusFileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDomain))
            return null;
        var matches = (await repository.LoadPackagesAsync(
                presentOnly: false,
                cancellationToken: cancellationToken))
            .Where(item =>
                item.Package.NexusModId == nexusModId &&
                item.Package.NexusFileId == nexusFileId &&
                string.Equals(
                    GetGameDomain(item.Package.NexusUrl),
                    gameDomain,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item =>
                File.Exists(item.Package.ArchivePath))
            .ThenByDescending(item => item.IsPresent)
            .ToArray();
        return matches.FirstOrDefault()?.Package.ArchivePath;
    }

    private static string? GetGameDomain(string? nexusUrl)
    {
        if (!Uri.TryCreate(nexusUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith(
                "nexusmods.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var segments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        var modsIndex = Array.FindIndex(
            segments,
            segment => string.Equals(
                segment,
                "mods",
                StringComparison.OrdinalIgnoreCase));
        return modsIndex > 0
            ? segments[modsIndex - 1]
            : null;
    }
}
