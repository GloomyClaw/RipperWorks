using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public static class NexusRequirementRefreshEligibility
{
    public static bool TryCreate(
        DownloaderEntry? entry,
        out NexusModIdentity identity)
    {
        if (entry?.Source == DownloaderSource.Nexus &&
            entry.NexusModId is > 0 and var modId &&
            NexusGameIdentityBridge.TryGetNexusGameId(
                entry.GameDomain,
                out var gameId))
        {
            identity = new(gameId, modId);
            return true;
        }

        identity = default;
        return false;
    }

    public static bool TryCreate(
        LibraryModRecord? mod,
        out NexusModIdentity identity)
    {
        if (mod?.Source == PackageSource.Nexus &&
            mod.NexusModId is > 0 and var modId &&
            NexusGameIdentityBridge.TryGetNexusGameId(
                mod.GameDomain,
                out var gameId))
        {
            identity = new(gameId, modId);
            return true;
        }

        identity = default;
        return false;
    }

    public static bool TryCreate(
        PackageRecord? package,
        out NexusModIdentity identity)
    {
        if (package?.Source == PackageSource.Nexus &&
            package.NexusModId is > 0 and var modId &&
            NexusGameIdentityBridge.TryGetNexusGameId(
                package.GameDomain,
                out var gameId))
        {
            identity = new(gameId, modId);
            return true;
        }

        identity = default;
        return false;
    }
}
