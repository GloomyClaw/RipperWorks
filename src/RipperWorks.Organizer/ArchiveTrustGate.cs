using RipperWorks.Core;

namespace RipperWorks.Organizer;

/// <summary>
/// Domain trust gate for analysis display classification, install planning,
/// and pre-mutation revalidation. UI is not the trust authority.
/// </summary>
public static class ArchiveTrustGate
{
    public static bool IsLegacyAnalysis(PackageAnalysisRecord? analysis)
    {
        if (analysis is null)
            return false;
        return !ArchiveContentIdentity.TryParse(analysis.Fingerprint, out _);
    }

    public static bool TryGetIdentity(
        PackageAnalysisRecord analysis,
        out ArchiveContentIdentity identity) =>
        ArchiveContentIdentity.TryParse(analysis.Fingerprint, out identity);

    /// <summary>
    /// Install authorization requires a parseable RF-05 identity and Ready state.
    /// Does not perform live file I/O; pair with content revalidation.
    /// </summary>
    public static bool IsTrustedForInstall(
        PackageAnalysisRecord? analysis,
        PackageRecord package)
    {
        if (analysis is null)
            return false;
        if (analysis.State != PackageAnalysisState.Ready)
            return false;
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

        if (analysis.DetectedRoots.Count > 1 &&
            string.IsNullOrWhiteSpace(analysis.SelectedRoot))
        {
            return false;
        }

        return true;
    }

    public static async Task<ArchiveTrustAssessment> AssessForInstallAsync(
        OrganizerPackageRecord mod,
        CancellationToken cancellationToken = default)
    {
        using var trustTiming = OperationPerformanceDiagnostics.MeasureMetric(
            "ARCHIVE_TRUST");
        ArgumentNullException.ThrowIfNull(mod);
        if (!mod.IsPresent || !File.Exists(mod.Package.ArchivePath))
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.ArchiveMissing,
                "ArchiveMissing");
        }

        if (mod.Analysis is null)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.MissingAnalysis,
                "AnalysisMissing");
        }

        if (IsLegacyAnalysis(mod.Analysis))
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.LegacyAnalysis,
                "RequiresReanalysis",
                "Legacy analysis cannot authorize install.");
        }

        if (mod.Analysis.State != PackageAnalysisState.Ready)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.AnalysisNotReady,
                "AnalysisNotReady");
        }

        if (!ArchiveContentIdentity.TryParse(
                mod.Analysis.Fingerprint,
                out var identity))
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.LegacyAnalysis,
                "RequiresReanalysis");
        }

        if (identity.PackageId != mod.Package.PackageId)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.PackageMismatch,
                "PackageMismatch");
        }

        if (identity.AnalyzerVersion != ArchiveAnalyzer.AnalyzerVersion)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.AnalyzerVersionMismatch,
                "AnalyzerVersionMismatch");
        }

        if (identity.PolicyVersion != ArchiveTrustPolicy.Version)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.PolicyVersionMismatch,
                "PolicyVersionMismatch");
        }

        if (mod.Analysis.DetectedRoots.Count > 1 &&
            string.IsNullOrWhiteSpace(mod.Analysis.SelectedRoot))
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.RequiresSelection,
                "InstallRootNotSelected");
        }

        if (!string.Equals(
                identity.SelectedRootNormalized,
                ArchiveTrustPolicy.NormalizeRoot(mod.Analysis.SelectedRoot),
                StringComparison.Ordinal))
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.RootChanged,
                "RootChanged");
        }

        string actualSha;
        try
        {
            ObserveArchiveHash(mod.Package.ArchivePath);
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "ARCHIVE_SHA",
                       "archive_sha"))
            {
                actualSha = await ArchiveContentHasher.ComputeFileSha256Async(
                        mod.Package.ArchivePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Redacted: no path or raw exception text in user-facing code.
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.ArchiveUnreadable,
                "ArchiveUnreadable",
                "Archive could not be read for trust verification.");
        }

        if (!string.Equals(
                actualSha,
                identity.ArchiveSha256,
                StringComparison.Ordinal))
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.StaleContent,
                "AnalysisStale",
                "Archive bytes no longer match trusted analysis.");
        }

        return ArchiveTrustAssessment.Trusted(identity);
    }

    /// <summary>
    /// Pre-mutation check against the immutable plan identity.
    /// </summary>
    public static async Task<ArchiveTrustAssessment> AssessBeforeMutationAsync(
        InstallPlan plan,
        OrganizerPackageRecord mod,
        CancellationToken cancellationToken = default)
    {
        using var trustTiming = OperationPerformanceDiagnostics.MeasureMetric(
            "ARCHIVE_TRUST");
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mod);

        if (string.IsNullOrWhiteSpace(plan.ExpectedArchiveSha256) ||
            plan.ExpectedPolicyVersion is null ||
            plan.ExpectedAnalyzerVersion is null)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.LegacyAnalysis,
                "RequiresReanalysis",
                "Install plan lacks content identity.");
        }

        if (plan.PackageId != mod.Package.PackageId)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.PackageMismatch,
                "PackageMismatch");
        }

        if (plan.ExpectedAnalyzerVersion != ArchiveAnalyzer.AnalyzerVersion)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.AnalyzerVersionMismatch,
                "AnalyzerVersionMismatch");
        }

        if (plan.ExpectedPolicyVersion != ArchiveTrustPolicy.Version)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.PolicyVersionMismatch,
                "PolicyVersionMismatch");
        }

        var planRoot = ArchiveTrustPolicy.NormalizeRoot(plan.ExpectedSelectedRoot);
        var analysisRoot = ArchiveTrustPolicy.NormalizeRoot(
            mod.Analysis?.SelectedRoot);
        if (!string.Equals(planRoot, analysisRoot, StringComparison.Ordinal))
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.RootChanged,
                "RootChanged");
        }

        if (!mod.IsPresent || !File.Exists(mod.Package.ArchivePath))
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.ArchiveMissing,
                "ArchiveMissing");
        }

        string actualSha;
        try
        {
            ObserveArchiveHash(mod.Package.ArchivePath);
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "ARCHIVE_SHA",
                       "archive_sha"))
            {
                actualSha = await ArchiveContentHasher.ComputeFileSha256Async(
                        mod.Package.ArchivePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.ArchiveUnreadable,
                "ArchiveUnreadable",
                "Archive could not be read for trust verification.");
        }

        var expected = ArchiveTrustPolicy.CanonicalizeSha256(
            plan.ExpectedArchiveSha256);
        if (expected is null ||
            !string.Equals(actualSha, expected, StringComparison.Ordinal))
        {
            return ArchiveTrustAssessment.Reject(
                ArchiveTrustStatus.StaleContent,
                "AnalysisStale",
                "Archive bytes changed after plan.");
        }

        return ArchiveTrustAssessment.Trusted(
            new ArchiveContentIdentity(
                mod.Package.PackageId,
                actualSha,
                ArchiveAnalyzer.AnalyzerVersion,
                planRoot,
                ArchiveTrustPolicy.Version));
    }

    private static void ObserveArchiveHash(string archivePath)
    {
        if (OperationPerformanceDiagnostics.CurrentContext is not (
                OperationPerformanceContext.Install or
                OperationPerformanceContext.InstallPreflight))
            return;
        OperationPerformanceDiagnostics.AddCounter(
            "archive_hash_pass_count");
        try
        {
            OperationPerformanceDiagnostics.AddCounter(
                "archive_bytes_hashed",
                new FileInfo(archivePath).Length);
        }
        catch
        {
            // The authoritative hash operation owns any real I/O failure.
        }
    }
}
