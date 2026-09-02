using System.Security.Cryptography;
using System.Text;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

/// <summary>
/// RF-05 analysis identity formatting and live/stored freshness checks.
/// Kept separate from <see cref="ArchiveAnalyzer"/> entry enumeration.
/// </summary>
public static class ArchiveAnalysisIdentity
{
    public static bool MatchesStoredIdentity(
        PackageAnalysisRecord analysis,
        PackageRecord package)
    {
        if (!ArchiveContentIdentity.TryParse(analysis.Fingerprint, out var identity))
            return false;
        if (identity.PackageId != package.PackageId)
            return false;
        if (identity.AnalyzerVersion != ArchiveAnalyzer.AnalyzerVersion)
            return false;
        if (identity.PolicyVersion != ArchiveTrustPolicy.Version)
            return false;
        if (!string.Equals(
                identity.SelectedRootNormalized,
                ArchiveTrustPolicy.NormalizeRoot(analysis.SelectedRoot),
                StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Live content freshness: re-hashes the archive path. Stored
    /// <see cref="PackageRecord.Sha256"/> alone is never sufficient.
    /// </summary>
    public static bool IsLiveContentCurrent(
        PackageAnalysisRecord analysis,
        PackageRecord package,
        CancellationToken cancellationToken = default)
    {
        if (ArchiveContentIdentity.TryParse(analysis.Fingerprint, out var identity))
        {
            if (!MatchesStoredIdentity(analysis, package))
                return false;
            try
            {
                if (!File.Exists(package.ArchivePath))
                    return false;
                var liveSha = HashFileSync(package.ArchivePath, cancellationToken);
                return string.Equals(
                    liveSha,
                    identity.ArchiveSha256,
                    StringComparison.Ordinal);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        // Legacy path/size/mtime freshness (display only; not install trust).
        return analysis.AnalyzerVersion == ArchiveAnalyzer.AnalyzerVersion &&
            analysis.ArchiveFileSize == package.FileSize &&
            analysis.ArchiveLastWriteUtc == package.LastWriteUtc &&
            string.Equals(
                analysis.ArchivePath,
                package.ArchivePath,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                analysis.Fingerprint,
                ComputeLegacyFingerprint(package, analysis.SelectedRoot),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Display/stored freshness for UI (CanInstall enablement). Does not re-hash
    /// the archive file. Install gates must still call
    /// <see cref="IsLiveContentCurrent"/>.
    /// </summary>
    public static bool IsCurrent(
        PackageAnalysisRecord analysis,
        PackageRecord package)
    {
        if (ArchiveContentIdentity.TryParse(analysis.Fingerprint, out var identity))
        {
            if (!MatchesStoredIdentity(analysis, package))
                return false;
            // Soft signal from persisted package SHA when present — not live proof.
            var stored = ArchiveTrustPolicy.CanonicalizeSha256(package.Sha256);
            if (stored is not null &&
                !string.Equals(stored, identity.ArchiveSha256, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        return analysis.AnalyzerVersion == ArchiveAnalyzer.AnalyzerVersion &&
            analysis.ArchiveFileSize == package.FileSize &&
            analysis.ArchiveLastWriteUtc == package.LastWriteUtc &&
            string.Equals(
                analysis.ArchivePath,
                package.ArchivePath,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                analysis.Fingerprint,
                ComputeLegacyFingerprint(package, analysis.SelectedRoot),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Library display classification without full SHA (no WPF Dispatcher hash).
    /// </summary>
    public static PackageAnalysisState GetDisplayState(
        PackageRecord package,
        PackageAnalysisRecord analysis) =>
        IsCurrent(analysis, package)
            ? analysis.State
            : PackageAnalysisState.Stale;

    public static string ComputeFingerprint(
        PackageRecord package,
        string? selectedRoot) =>
        ComputeLegacyFingerprint(package, selectedRoot);

    public static string BuildContentFingerprint(
        PackageRecord package,
        string archiveSha256,
        string? selectedRoot)
    {
        var sha = ArchiveTrustPolicy.CanonicalizeSha256(archiveSha256)
            ?? throw new ArgumentException(
                "Archive SHA-256 must be 64 lowercase hex characters.",
                nameof(archiveSha256));
        return new ArchiveContentIdentity(
            package.PackageId,
            sha,
            ArchiveAnalyzer.AnalyzerVersion,
            ArchiveTrustPolicy.NormalizeRoot(selectedRoot),
            ArchiveTrustPolicy.Version).FormatFingerprint();
    }

    public static string ComputeLegacyFingerprint(
        PackageRecord package,
        string? selectedRoot)
    {
        var identity = string.Join(
            '\n',
            ArchiveAnalyzer.AnalyzerVersion.ToString(),
            Path.GetFullPath(package.ArchivePath).ToUpperInvariant(),
            package.FileSize.ToString(),
            package.LastWriteUtc.ToUniversalTime().Ticks.ToString(),
            selectedRoot ?? string.Empty);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    public static string HashFileSync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ArchiveContentHasher.BufferSize,
            FileOptions.SequentialScan);
        return ArchiveContentHasher.ComputeStreamSha256(stream, cancellationToken);
    }
}
