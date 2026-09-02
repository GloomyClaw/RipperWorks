using System.Text.RegularExpressions;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

/// <summary>
/// Content-bound archive trust and resource-budget policy version.
/// </summary>
public static class ArchiveTrustPolicy
{
    /// <summary>
    /// Policy v2: v1 content identity plus SAF-02 analysis/extraction budgets.
    /// </summary>
    public const int Version = 2;

    public const string FingerprintPrefix = "rf05";

    private static readonly Regex Sha256Hex = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string NormalizeRoot(string? selectedRoot) =>
        string.IsNullOrWhiteSpace(selectedRoot)
            ? string.Empty
            : selectedRoot.Trim().Replace('/', '\\');

    public static string? CanonicalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim().ToLowerInvariant();
        return Sha256Hex.IsMatch(trimmed) ? trimmed : null;
    }

    public static bool IsValidSha256(string? value) =>
        CanonicalizeSha256(value) is not null;
}

/// <summary>
/// Trusted analysis identity fields for RF-05.
/// </summary>
public sealed record ArchiveContentIdentity(
    PackageId PackageId,
    string ArchiveSha256,
    int AnalyzerVersion,
    string SelectedRoot,
    int PolicyVersion)
{
    public string SelectedRootNormalized =>
        ArchiveTrustPolicy.NormalizeRoot(SelectedRoot);

    public string FormatFingerprint() =>
        string.Join(
            '|',
            ArchiveTrustPolicy.FingerprintPrefix,
            ArchiveSha256,
            AnalyzerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PolicyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Uri.EscapeDataString(SelectedRootNormalized),
            PackageId.Value);

    public static bool TryParse(
        string? fingerprint,
        out ArchiveContentIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(fingerprint))
            return false;
        var parts = fingerprint.Split('|');
        if (parts.Length != 6)
            return false;
        if (!string.Equals(
                parts[0],
                ArchiveTrustPolicy.FingerprintPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        var sha = ArchiveTrustPolicy.CanonicalizeSha256(parts[1]);
        if (sha is null)
            return false;
        if (!int.TryParse(
                parts[2],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var analyzerVersion))
        {
            return false;
        }

        if (!int.TryParse(
                parts[3],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var policyVersion))
        {
            return false;
        }

        string root;
        try
        {
            root = Uri.UnescapeDataString(parts[4]);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(parts[5]))
            return false;

        identity = new ArchiveContentIdentity(
            new PackageId(parts[5]),
            sha,
            analyzerVersion,
            ArchiveTrustPolicy.NormalizeRoot(root),
            policyVersion);
        return true;
    }
}

public enum ArchiveTrustStatus
{
    Trusted = 0,
    MissingAnalysis = 1,
    LegacyAnalysis = 2,
    StaleContent = 3,
    RequiresSelection = 4,
    AnalyzerVersionMismatch = 5,
    PolicyVersionMismatch = 6,
    ArchiveMissing = 7,
    ArchiveUnreadable = 8,
    PackageMismatch = 9,
    RootChanged = 10,
    AnalysisNotReady = 11
}

public sealed record ArchiveTrustAssessment(
    ArchiveTrustStatus Status,
    string Code,
    string? Detail = null,
    ArchiveContentIdentity? Identity = null)
{
    public bool IsTrusted => Status == ArchiveTrustStatus.Trusted;

    public static ArchiveTrustAssessment Trusted(ArchiveContentIdentity identity) =>
        new(ArchiveTrustStatus.Trusted, "Trusted", Identity: identity);

    public static ArchiveTrustAssessment Reject(
        ArchiveTrustStatus status,
        string code,
        string? detail = null) =>
        new(status, code, detail);
}
