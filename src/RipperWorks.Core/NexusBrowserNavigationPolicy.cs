using System.Globalization;

namespace RipperWorks.Core;

public sealed record NexusPageContext
{
    public required string GameDomain { get; init; }
    public required long NexusModId { get; init; }
}

public enum NexusBrowserNavigationClassification
{
    AllowedModPage,
    AllowedCyberpunkPage,
    AllowedNexusAuthPage,
    BlockedWrongGame,
    BlockedWrongHost,
    BlockedNonDefaultPort,
    BlockedUnsupportedScheme,
    BlockedMalformed,
    NxmProtocol
}

public sealed record NexusBrowserNavigationDecision
{
    public required NexusBrowserNavigationClassification Classification { get; init; }
    public bool IsAllowed => Classification is
        NexusBrowserNavigationClassification.AllowedModPage or
        NexusBrowserNavigationClassification.AllowedCyberpunkPage or
        NexusBrowserNavigationClassification.AllowedNexusAuthPage;
    public NexusPageContext? PageContext { get; init; }
    public string? Reason { get; init; }
}

public static class NexusBrowserNavigationPolicy
{
    public const string CyberpunkGameDomain = "cyberpunk2077";

    private const string UsersHost = "users.nexusmods.com";

    private static readonly HashSet<string> CanonicalWebsiteHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "nexusmods.com",
        "www.nexusmods.com"
    };

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "nexusmods.com",
        "www.nexusmods.com",
        UsersHost
    };

    public static bool IsValidOpenNexusWebUrl(string? url, out Uri? validUri)
    {
        validUri = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri is null)
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (!CanonicalWebsiteHosts.Contains(uri.Host))
            return false;

        var decision = Evaluate(uri);
        if (!decision.IsAllowed || decision.Classification == NexusBrowserNavigationClassification.AllowedNexusAuthPage)
            return false;

        validUri = uri;
        return true;
    }

    public static NexusBrowserNavigationDecision Evaluate(string? uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString))
        {
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.BlockedMalformed,
                Reason = "URI string is null or empty."
            };
        }

        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.BlockedMalformed,
                Reason = "Malformed absolute URI."
            };
        }

        return Evaluate(uri);
    }

    public static NexusBrowserNavigationDecision Evaluate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.BlockedMalformed,
                Reason = "URI must be absolute."
            };
        }

        if (string.Equals(uri.Scheme, "nxm", StringComparison.OrdinalIgnoreCase))
        {
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.NxmProtocol,
                Reason = "NXM protocol requires dedicated product handling."
            };
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.BlockedUnsupportedScheme,
                Reason = $"Unsupported URI scheme '{uri.Scheme}'. Top-level Nexus browser requires HTTPS."
            };
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.BlockedWrongHost,
                Reason = $"Host '{uri.Host}' is not an accepted Nexus host."
            };
        }

        if (!uri.IsDefaultPort)
        {
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.BlockedNonDefaultPort,
                Reason = $"Non-default port '{uri.Port}' is not permitted."
            };
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.BlockedMalformed,
                Reason = "User-info in authority is not permitted."
            };
        }

        var path = uri.AbsolutePath;

        // Dedicated SSO / Identity portal on users.nexusmods.com
        if (string.Equals(uri.Host, UsersHost, StringComparison.OrdinalIgnoreCase))
        {
            if (IsAllowedUsersAuthPath(path))
            {
                return new NexusBrowserNavigationDecision
                {
                    Classification = NexusBrowserNavigationClassification.AllowedNexusAuthPage,
                    Reason = "Nexus user authentication and identity portal."
                };
            }

            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.BlockedMalformed,
                Reason = $"Path '{path}' on users.nexusmods.com is not an allowed authentication path."
            };
        }

        var segments = path.Split('/');
        if (segments.Length < 2 || string.IsNullOrEmpty(segments[1]))
        {
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.BlockedWrongGame,
                Reason = "Root Nexus page does not specify Cyberpunk 2077 game domain."
            };
        }

        var firstSegment = segments[1];

        // Family A: Classic Cyberpunk 2077 route (/cyberpunk2077/...)
        if (string.Equals(firstSegment, CyberpunkGameDomain, StringComparison.OrdinalIgnoreCase))
        {
            // Canonical Mod Page:
            // Shape 1 (exact path without trailing slash): ["", "cyberpunk2077", "mods", "{modId}"] -> Length 4
            // Shape 2 (exact path with single trailing slash): ["", "cyberpunk2077", "mods", "{modId}", ""] -> Length 5
            if (segments.Length >= 4 && string.Equals(segments[2], "mods", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length == 4 || (segments.Length == 5 && string.IsNullOrEmpty(segments[4])))
                {
                    var modIdStr = segments[3];
                    if (long.TryParse(modIdStr, NumberStyles.None, CultureInfo.InvariantCulture, out var modId) && modId > 0)
                    {
                        return new NexusBrowserNavigationDecision
                        {
                            Classification = NexusBrowserNavigationClassification.AllowedModPage,
                            PageContext = new NexusPageContext
                            {
                                GameDomain = CyberpunkGameDomain,
                                NexusModId = modId
                            }
                        };
                    }
                }
            }

            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.AllowedCyberpunkPage,
                Reason = "General Cyberpunk 2077 Nexus page."
            };
        }

        // Family B: Current game route (/games/cyberpunk2077/...)
        if (string.Equals(firstSegment, "games", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length < 3 || string.IsNullOrEmpty(segments[2]))
            {
                return new NexusBrowserNavigationDecision
                {
                    Classification = NexusBrowserNavigationClassification.BlockedWrongGame,
                    Reason = "Root Nexus page does not specify Cyberpunk 2077 game domain."
                };
            }

            var gameSegment = segments[2];
            if (!string.Equals(gameSegment, CyberpunkGameDomain, StringComparison.OrdinalIgnoreCase))
            {
                return new NexusBrowserNavigationDecision
                {
                    Classification = NexusBrowserNavigationClassification.BlockedWrongGame,
                    Reason = $"Game domain '{gameSegment}' is not '{CyberpunkGameDomain}'."
                };
            }

            // Family B provides navigation permission only. PageContext remains null.
            return new NexusBrowserNavigationDecision
            {
                Classification = NexusBrowserNavigationClassification.AllowedCyberpunkPage,
                Reason = "Current Cyberpunk 2077 game hub page."
            };
        }

        return new NexusBrowserNavigationDecision
        {
            Classification = NexusBrowserNavigationClassification.BlockedWrongGame,
            Reason = $"Game domain '{firstSegment}' is not '{CyberpunkGameDomain}'."
        };
    }

    private static bool IsAllowedUsersAuthPath(string path)
    {
        return string.Equals(path, "/auth/sign_in", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "/auth/sign_in/", StringComparison.OrdinalIgnoreCase);
    }
}
