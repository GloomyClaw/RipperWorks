using System.Text.RegularExpressions;

namespace RipperWorks.App.Services;

/// <summary>
/// Prevents typed/stored Nexus keys from appearing in Settings credential
/// status, dialog, or session-facing text.
/// </summary>
public static class CredentialUiSanitizer
{
    // Opaque key-like tokens (Nexus keys and test secrets). Threshold is
    // intentionally modest so short synthetic keys are also redacted.
    private static readonly Regex LongToken = new(
        @"\b[A-Za-z0-9+/=_\-]{12,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Sanitize(
        string? rawMessage,
        params string?[] secrets)
    {
        var message = string.IsNullOrWhiteSpace(rawMessage)
            ? "Credential operation failed."
            : rawMessage;

        foreach (var secret in secrets)
        {
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 4)
                continue;
            message = message.Replace(
                secret,
                "[redacted]",
                StringComparison.Ordinal);
            var trimmed = secret.Trim();
            if (!string.Equals(trimmed, secret, StringComparison.Ordinal) &&
                trimmed.Length >= 4)
            {
                message = message.Replace(
                    trimmed,
                    "[redacted]",
                    StringComparison.Ordinal);
            }
        }

        // Defense in depth: long opaque tokens that look like keys.
        message = LongToken.Replace(message, "[redacted]");
        return message;
    }
}
