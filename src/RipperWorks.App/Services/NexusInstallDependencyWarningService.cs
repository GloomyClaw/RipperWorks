using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public enum NexusDependencyWarningDecisionKind
{
    Continue,
    Cancel,
    OpenLibraryDependency,
    OpenDownloadsDependency,
    OpenUrl
}

public sealed record NexusDependencyWarningResult(
    NexusDependencyWarningDecisionKind Decision,
    LibraryModId? TargetLibraryModId = null,
    NexusModIdentity? TargetNexusIdentity = null,
    Uri? TargetUrl = null)
{
    public static NexusDependencyWarningResult Continue() =>
        new(NexusDependencyWarningDecisionKind.Continue);

    public static NexusDependencyWarningResult Cancel() =>
        new(NexusDependencyWarningDecisionKind.Cancel);

    public static NexusDependencyWarningResult OpenLibrary(
        LibraryModId? libraryModId,
        NexusModIdentity? targetNexusIdentity = null) =>
        new(
            NexusDependencyWarningDecisionKind.OpenLibraryDependency,
            TargetLibraryModId: libraryModId,
            TargetNexusIdentity: targetNexusIdentity);

    public static NexusDependencyWarningResult OpenDownloads(
        NexusModIdentity targetNexusIdentity) =>
        new(
            NexusDependencyWarningDecisionKind.OpenDownloadsDependency,
            TargetNexusIdentity: targetNexusIdentity);

    public static NexusDependencyWarningResult OpenUrl(Uri url) =>
        new(NexusDependencyWarningDecisionKind.OpenUrl, TargetUrl: url);
}

public sealed record NexusDependencyWarningItem(
    string TargetName,
    NexusRequirementLocalAvailability Availability,
    string AvailabilityText,
    string? SourceModDisplayName = null,
    LibraryModId? TargetLibraryModId = null,
    NexusModIdentity? TargetNexusIdentity = null,
    Uri? TargetUrl = null,
    string? ActionText = null,
    bool CanAssist = false);

public sealed class NexusInstallDependencyWarningService
{
    private readonly INexusRequirementRelationsService? _relationsService;
    private readonly LocalizationService _localization;

    public NexusInstallDependencyWarningService(
        INexusRequirementRelationsService? relationsService,
        LocalizationService localization)
    {
        _relationsService = relationsService;
        _localization = localization;
    }

    public async Task<IReadOnlyList<NexusDependencyWarningItem>> GetSingleInstallWarningsAsync(
        LibraryModRecord libraryMod,
        OrganizerPackageRecord targetArchive,
        CancellationToken cancellationToken = default)
    {
        if (_relationsService is null)
        {
            return [];
        }

        // Reinstallation of the exact already-installed target does not warn
        if (targetArchive.Package.InstallationState == PackageInstallationState.Installed)
        {
            return [];
        }

        if (!NexusRequirementRefreshEligibility.TryCreate(libraryMod, out var identity))
        {
            return [];
        }

        try
        {
            var relations = await _relationsService.LoadRelationsAsync(identity, cancellationToken)
                .ConfigureAwait(false);
            return relations.Forward.Relations
                .Where(edge => edge.LocalState.Availability != NexusRequirementLocalAvailability.Installed)
                .Select(edge => CreateWarningItem(edge, libraryMod.EffectiveDisplayName))
                .ToArray();
        }
        catch
        {
            // Cached read failure is non-blocking
            return [];
        }
    }

    public async Task<IReadOnlyList<NexusDependencyWarningItem>> GetBatchInstallWarningsAsync(
        IReadOnlyList<BatchPackageCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        if (_relationsService is null || candidates.Count == 0)
        {
            return [];
        }

        // Filter candidates to only those not yet installed
        var uninstalledCandidates = candidates
            .Where(c => c.Package.Package.InstallationState != PackageInstallationState.Installed)
            .ToArray();

        if (uninstalledCandidates.Length == 0)
        {
            return [];
        }

        // Deduplicate logical Nexus mods to check
        var modNexusIdentities = new Dictionary<NexusModIdentity, (string DisplayName, NexusModIdentity Identity)>();
        foreach (var candidate in uninstalledCandidates)
        {
            if (NexusRequirementRefreshEligibility.TryCreate(candidate.Package.Package, out var id))
            {
                if (!modNexusIdentities.ContainsKey(id))
                {
                    modNexusIdentities[id] = (candidate.DisplayName, id);
                }
            }
        }

        if (modNexusIdentities.Count == 0)
        {
            return [];
        }

        var results = new List<NexusDependencyWarningItem>();

        foreach (var (displayName, identity) in modNexusIdentities.Values)
        {
            try
            {
                var relations = await _relationsService.LoadRelationsAsync(identity, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var edge in relations.Forward.Relations)
                {
                    if (edge.LocalState.Availability == NexusRequirementLocalAvailability.Installed)
                    {
                        continue;
                    }

                    results.Add(CreateWarningItem(edge, displayName));
                }
            }
            catch
            {
                // Non-blocking
            }
        }

        return results;
    }

    private NexusDependencyWarningItem CreateWarningItem(
        NexusRequirementProjectedEdge edge,
        string sourceModDisplayName)
    {
        var targetName = edge.Edge.Target switch
        {
            NexusModRequirementTarget targetMod => targetMod.DisplayName ??
                string.Format(_localization.Get("NexusModFallback"), targetMod.Identity.ModId),
            NexusExternalRequirementTarget targetExt => targetExt.DisplayName ??
                _localization.Get("NexusExternalRequirement"),
            _ => _localization.Get("NexusExternalRequirement")
        };

        var availabilityText = edge.LocalState.Availability switch
        {
            NexusRequirementLocalAvailability.InLibrary => _localization.Get("NexusRelationStateInLibrary"),
            NexusRequirementLocalAvailability.InDownloads => _localization.Get("NexusRelationStateInDownloads"),
            NexusRequirementLocalAvailability.Missing => _localization.Get("NexusRelationStateMissing"),
            NexusRequirementLocalAvailability.External => _localization.Get("NexusRelationStateExternal"),
            _ => _localization.Get("NexusRelationStateMissing")
        };

        LibraryModId? targetLibraryModId = null;
        NexusModIdentity? targetNexusIdentity = null;
        Uri? targetUrl = null;
        string? actionText = null;
        var canAssist = false;

        targetNexusIdentity = NexusGameIdentityBridge.GetEffectiveTargetIdentity(edge.Edge.Target);

        switch (edge.LocalState.Availability)
        {
            case NexusRequirementLocalAvailability.InLibrary:
                targetLibraryModId = edge.LocalState.LibraryModId;
                actionText = _localization.Get("NexusDependencyActionOpenLibrary");
                canAssist = true;
                break;

            case NexusRequirementLocalAvailability.InDownloads:
                if (targetNexusIdentity is { } identity && identity.ModId > 0)
                {
                    actionText = _localization.Get("NexusDependencyActionOpenDownloads");
                    canAssist = true;
                }
                break;

            case NexusRequirementLocalAvailability.Missing:
                if (targetNexusIdentity is { } missingIdentity)
                {
                    if (NexusGameIdentityBridge.TryGetGameDomain(missingIdentity.GameId, out var domain) &&
                        !string.IsNullOrWhiteSpace(domain))
                    {
                        targetUrl = new Uri($"https://www.nexusmods.com/{domain}/mods/{missingIdentity.ModId}");
                    }
                    else if (edge.Edge.Target is NexusModRequirementTarget missingTarget &&
                             missingTarget.ClickableUrl is { } clickUrl &&
                             clickUrl.IsAbsoluteUri &&
                             (clickUrl.Scheme == Uri.UriSchemeHttp || clickUrl.Scheme == Uri.UriSchemeHttps))
                    {
                        targetUrl = clickUrl;
                    }
                    else if (edge.Edge.Target is NexusExternalRequirementTarget extMissing &&
                             extMissing.ClickableUrl is { } extClickUrl &&
                             extClickUrl.IsAbsoluteUri &&
                             (extClickUrl.Scheme == Uri.UriSchemeHttp || extClickUrl.Scheme == Uri.UriSchemeHttps))
                    {
                        targetUrl = extClickUrl;
                    }

                    if (targetUrl is not null)
                    {
                        actionText = _localization.Get("NexusDependencyActionOpenNexus");
                        canAssist = true;
                    }
                }
                break;

            case NexusRequirementLocalAvailability.External:
                if (edge.Edge.Target is NexusExternalRequirementTarget extTarget &&
                    extTarget.ClickableUrl is { } extUrl &&
                    extUrl.IsAbsoluteUri &&
                    (extUrl.Scheme == Uri.UriSchemeHttp || extUrl.Scheme == Uri.UriSchemeHttps))
                {
                    targetUrl = extUrl;
                    actionText = _localization.Get("NexusDependencyActionOpenExternal");
                    canAssist = true;
                }
                break;
        }

        return new(
            TargetName: targetName,
            Availability: edge.LocalState.Availability,
            AvailabilityText: availabilityText,
            SourceModDisplayName: sourceModDisplayName,
            TargetLibraryModId: targetLibraryModId,
            TargetNexusIdentity: targetNexusIdentity,
            TargetUrl: targetUrl,
            ActionText: actionText,
            CanAssist: canAssist);
    }
}
