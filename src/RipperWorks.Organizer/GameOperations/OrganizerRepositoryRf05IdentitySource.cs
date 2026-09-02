using RipperWorks.Core;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Production read-only RF-05 identity adapter. Loads the current
/// <see cref="PackageAnalysisRecord"/> from <see cref="OrganizerRepository"/>
/// and exposes the trusted content identity without inventing fields from
/// the operation plan. Does not mutate the organizer store or game root.
/// </summary>
public sealed class OrganizerRepositoryRf05IdentitySource
    : IGameOperationRf05IdentitySource
{
    private readonly OrganizerRepository _repository;

    public OrganizerRepositoryRf05IdentitySource(OrganizerRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<Rf05IdentitySnapshot?> GetCurrentAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (plan.PackageId is null)
            return null;

        var analysis = await _repository
            .LoadAnalysisAsync(plan.PackageId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (analysis is null)
            return null;

        if (analysis.State != PackageAnalysisState.Ready)
            return null;

        if (ArchiveTrustGate.IsLegacyAnalysis(analysis) ||
            !ArchiveContentIdentity.TryParse(
                analysis.Fingerprint,
                out var identity))
        {
            // Legacy / non-RF-05 fingerprint: surface as untrusted, not invented.
            var legacyRoot = ArchiveTrustPolicy.NormalizeRoot(analysis.SelectedRoot);
            return new Rf05IdentitySnapshot(
                analysis.PackageId,
                legacyRoot,
                analysis.AnalyzerVersion,
                ArchiveTrustPolicy.Version,
                ExpectedArchiveSha256: null,
                IsTrustedRf05Fingerprint: false);
        }

        if (!identity.PackageId.Equals(analysis.PackageId))
            return null;

        // Analysis package association and fingerprint must agree on package.
        var selectedRoot = ArchiveTrustPolicy.NormalizeRoot(analysis.SelectedRoot);
        if (!string.Equals(
                identity.SelectedRootNormalized,
                selectedRoot,
                StringComparison.Ordinal))
        {
            // Store row disagrees with its own fingerprint → untrusted.
            return new Rf05IdentitySnapshot(
                identity.PackageId,
                selectedRoot,
                identity.AnalyzerVersion,
                identity.PolicyVersion,
                identity.ArchiveSha256,
                IsTrustedRf05Fingerprint: false);
        }

        return new Rf05IdentitySnapshot(
            identity.PackageId,
            identity.SelectedRootNormalized,
            identity.AnalyzerVersion,
            identity.PolicyVersion,
            identity.ArchiveSha256,
            IsTrustedRf05Fingerprint: true);
    }
}
