namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Explicit step resource identities for ExpectedBefore/After and ContentIdentity.
/// Formats:
/// - <c>Missing</c> — path must not exist
/// - <c>Sha256:&lt;64 hex&gt;</c> — production content hash
/// - other non-empty text — synthetic fixture content token only
/// </summary>
public static class GameOperationStepIdentity
{
    public const string Missing = "Missing";
    public const string Sha256Prefix = "Sha256:";

    public static bool IsMissing(string? identity) =>
        string.Equals(identity, Missing, StringComparison.Ordinal);

    public static string FormatSha256(string hex64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex64);
        var hex = hex64.Trim().ToLowerInvariant();
        if (!IsHex64(hex))
            throw new ArgumentException("InvalidSha256Identity", nameof(hex64));
        return Sha256Prefix + hex;
    }

    public static bool TryGetSha256(string? identity, out string hex64)
    {
        hex64 = string.Empty;
        if (string.IsNullOrWhiteSpace(identity))
            return false;
        var value = identity.Trim();
        if (value.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            var hex = value[Sha256Prefix.Length..].Trim().ToLowerInvariant();
            if (!IsHex64(hex))
                return false;
            hex64 = hex;
            return true;
        }

        // Bare 64-hex is accepted as SHA for plan/hash compatibility.
        if (IsHex64(value))
        {
            hex64 = value.ToLowerInvariant();
            return true;
        }

        return false;
    }

    public static bool IsSyntheticContent(string? identity) =>
        !string.IsNullOrWhiteSpace(identity) &&
        !IsMissing(identity) &&
        !TryGetSha256(identity, out _);

    public static bool IsHex64(string value)
    {
        if (value.Length != 64)
            return false;
        foreach (var c in value)
        {
            var isHex =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }

        return true;
    }
}
