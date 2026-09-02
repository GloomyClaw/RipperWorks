using RipperWorks.Core;

namespace RipperWorks.Organizer;

public interface IPackageAnalysisRunner
{
    Task<PackageAnalysisRecord> AnalyzeAndPersistAsync(
        PackageRecord package,
        bool force,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Test-only fault phases for SaveAnalysisAsync transaction boundaries.
/// </summary>
public enum AnalysisSaveFaultPhase
{
    None = 0,
    AfterDeleteOldBeforeInsert = 1,
    AfterInsertBeforePackageSha = 2,
    AfterPackageShaBeforeCommit = 3
}

public sealed class OrganizerAnalysisService(
    ArchiveAnalyzer analyzer,
    OrganizerRepository repository) : IPackageAnalysisRunner
{
    /// <summary>
    /// Test-only: runs after analyzer returns and before content lease/save.
    /// </summary>
    internal Func<ArchiveAnalysisDraft, CancellationToken, Task>?
        TestOnlyAfterAnalyzeBeforePersist { get; set; }

    /// <summary>
    /// Test-only: runs after exclusive content lease is acquired and before
    /// SaveAnalysis. Production holds the lease across this hook so replace
    /// attempts against the path fail while the lease is open.
    /// </summary>
    internal Func<ArchiveAnalysisDraft, VerifiedArchiveLease, CancellationToken, Task>?
        TestOnlyAfterLeaseBeforeSave { get; set; }

    public async Task<PackageAnalysisRecord> AnalyzeAndPersistAsync(
        PackageRecord package,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var existing = await repository.LoadAnalysisAsync(
            package.PackageId,
            cancellationToken);

        if (!force &&
            existing is not null &&
            ArchiveTrustGate.IsTrustedForInstall(existing, package) &&
            ArchiveAnalyzer.IsLiveContentCurrent(
                existing,
                package,
                cancellationToken))
        {
            return existing;
        }

        var draft = await analyzer.AnalyzeAsync(package, cancellationToken)
            .ConfigureAwait(false);

        if (TestOnlyAfterAnalyzeBeforePersist is not null)
        {
            await TestOnlyAfterAnalyzeBeforePersist(draft, cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        PackageAnalysisRecord saved;
        await using (var lease = await VerifiedArchiveLease.AcquireForDraftAsync(
                         draft,
                         cancellationToken)
                     .ConfigureAwait(false))
        {
            if (TestOnlyAfterLeaseBeforeSave is not null)
            {
                await TestOnlyAfterLeaseBeforeSave(
                        draft,
                        lease,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (draft.State is PackageAnalysisState.Ready or
                PackageAnalysisState.RequiresSelection)
            {
                var expected = ArchiveTrustPolicy.CanonicalizeSha256(
                    draft.ArchiveSha256);
                if (expected is null ||
                    !string.Equals(
                        expected,
                        lease.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Analysis draft identity does not match leased archive bytes.");
                }
            }

            // Commit under exclusive lease; then lease releases on dispose.
            // SaveAnalysisAsync owns post-commit cancellation semantics:
            // user CT after the pre-commit point cannot turn commit into OCE.
            saved = await repository.SaveAnalysisAsync(draft, cancellationToken)
                .ConfigureAwait(false);
        }

        return saved;
    }

    public async Task<PackageAnalysisRecord> SelectRootAsync(
        PackageRecord package,
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var analysis = await repository.LoadAnalysisAsync(
            package.PackageId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The package has no saved analysis.");
        if (!ArchiveContentIdentity.TryParse(
                analysis.Fingerprint,
                out var identity))
        {
            throw new InvalidOperationException(
                "Legacy analysis cannot select a root for install trust. Reanalyze first.");
        }

        if (!ArchiveAnalyzer.IsLiveContentCurrent(
                analysis,
                package,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The archive changed after analysis. Reanalyze it before selecting a root.");
        }

        package = package with { Sha256 = identity.ArchiveSha256 };
        if (!analysis.DetectedRoots.Contains(
                root,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The selected root does not belong to the current archive analysis.");
        }

        var selectedDraft = analyzer.ApplySelectedRoot(
            package,
            analysis,
            root);

        await using var lease = await VerifiedArchiveLease.AcquireForDraftAsync(
                selectedDraft,
                cancellationToken)
            .ConfigureAwait(false);
        return await repository.SaveAnalysisAsync(
            selectedDraft,
            cancellationToken);
    }

    /// <summary>
    /// Display classification only — no full-file rehash (safe for UI binding).
    /// Install/analyze paths must use <see cref="ArchiveAnalyzer.IsLiveContentCurrent"/>.
    /// </summary>
    public static PackageAnalysisState GetEffectiveState(
        PackageRecord package,
        PackageAnalysisRecord? analysis) =>
        analysis is null
            ? PackageAnalysisState.NotAnalyzed
            : ArchiveAnalysisIdentity.GetDisplayState(package, analysis);
}

public sealed class AnalysisQueueService(
    IPackageAnalysisRunner runner)
{
    public async Task<IReadOnlyList<PackageAnalysisRecord>> AnalyzeAsync(
        IReadOnlyList<PackageRecord> packages,
        bool force,
        IProgress<AnalysisQueueProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var results = new List<PackageAnalysisRecord>(packages.Count);
        progress?.Report(new(0, packages.Count, null, false));

        for (var index = 0; index < packages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = packages[index];
            progress?.Report(new(
                index,
                packages.Count,
                package.PackageId,
                false));

            var result = await runner.AnalyzeAndPersistAsync(
                package,
                force,
                cancellationToken).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(new(
                index + 1,
                packages.Count,
                null,
                false));
        }

        return results;
    }
}
