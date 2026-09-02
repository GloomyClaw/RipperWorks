using RipperWorks.Core;
using RipperWorks.Downloader;

namespace RipperWorks.App.Services;

public enum NexusRequirementSyncOutcome
{
    Success,
    Partial,
    Failure
}

public enum NexusRequirementTraversalOutcome
{
    Complete,
    Failed
}

public sealed record NexusRequirementTraversalSyncResult(
    NexusRequirementSnapshotOwner Owner,
    NexusRequirementTraversalOutcome Outcome,
    NexusRequirementFailure? Failure,
    IReadOnlyList<NexusRequirementCanonicalKey>? FreshCanonicalKeys = null,
    bool IsSourceAmbiguous = false)
{
    public IReadOnlyList<NexusRequirementCanonicalKey> FreshCanonicalKeys { get; init; } =
        FreshCanonicalKeys ?? [];
    public bool IsSourceAmbiguous { get; init; } = IsSourceAmbiguous;
}

public sealed record NexusRequirementSyncResult(
    NexusRequirementSyncOutcome Outcome,
    NexusRequirementTraversalSyncResult Forward,
    NexusRequirementTraversalSyncResult Reverse);

public enum NexusRequirementLocalAvailability
{
    Installed,
    InLibrary,
    InDownloads,
    Missing,
    External
}

public sealed record NexusRequirementLocalState(
    NexusRequirementLocalAvailability Availability,
    LibraryModId? LibraryModId,
    string? DisplayName = null);

public sealed record NexusRequirementProjectedEdge(
    NexusModRequirementEdge Edge,
    NexusRequirementLocalState LocalState);

public sealed record NexusRequirementTraversalProjection(
    NexusRequirementSnapshotRecord? State,
    IReadOnlyList<NexusRequirementProjectedEdge> Relations,
    int FilteredSelfRelationCount,
    bool IsSourceAmbiguous = false);

public sealed record NexusRequirementRelations(
    NexusRequirementTraversalProjection Forward,
    NexusRequirementTraversalProjection Reverse)
{
    public bool IsForwardSourceAmbiguous => Forward.IsSourceAmbiguous;
}

public static class NexusGameIdentityBridge
{
    public const long Cyberpunk2077NexusGameId = 3333;
    public const string Cyberpunk2077GameDomain = "cyberpunk2077";

    public static bool TryGetGameDomain(long nexusGameId, out string domain)
    {
        if (nexusGameId == Cyberpunk2077NexusGameId)
        {
            domain = Cyberpunk2077GameDomain;
            return true;
        }

        domain = string.Empty;
        return false;
    }

    public static bool TryGetNexusGameId(
        string? gameDomain,
        out long nexusGameId)
    {
        if (string.Equals(
                gameDomain?.Trim(),
                Cyberpunk2077GameDomain,
                StringComparison.OrdinalIgnoreCase))
        {
            nexusGameId = Cyberpunk2077NexusGameId;
            return true;
        }

        nexusGameId = 0;
        return false;
    }

    public static bool TryParseNexusModUrl(string? rawUrl, out NexusModIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return TryParseNexusModUrl(uri, out identity);
    }

    public static bool TryParseNexusModUrl(Uri? uri, out NexusModIdentity identity)
    {
        identity = default;
        if (uri is null || !uri.IsAbsoluteUri)
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        if (!string.Equals(host, "nexusmods.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, "www.nexusmods.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3)
            return false;

        if (!TryGetNexusGameId(segments[0], out var gameId))
            return false;

        if (!string.Equals(segments[1], "mods", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!long.TryParse(segments[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var modId) || modId <= 0)
            return false;

        identity = new NexusModIdentity(gameId, modId);
        return true;
    }

    public static NexusModIdentity? GetEffectiveTargetIdentity(NexusRequirementTarget? target)
    {
        return target switch
        {
            NexusModRequirementTarget modTarget => modTarget.Identity,
            NexusExternalRequirementTarget extTarget =>
                (extTarget.ClickableUrl is { } uri && TryParseNexusModUrl(uri, out var id1)) ? id1 :
                TryParseNexusModUrl(extTarget.ProviderUrl, out var id2) ? id2 : null,
            _ => null
        };
    }
}
