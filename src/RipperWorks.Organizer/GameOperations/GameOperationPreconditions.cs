using System.Security.Cryptography;
using System.Text;
using RipperWorks.Core;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Default read-only precondition validator for synthetic/shadow plans.
/// Does not create staging or mutate game root.
/// </summary>
public sealed class DefaultGameOperationPreconditionValidator
    : IGameOperationPreconditionValidator
{
    private readonly IGameOperationRf05IdentitySource? _identitySource;

    public DefaultGameOperationPreconditionValidator(
        IGameOperationRf05IdentitySource? identitySource = null)
    {
        _identitySource = identitySource;
    }

    public async Task<PreconditionOutcome> ValidateAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (plan.PlanVersion != GameOperationPlanVersions.Current)
        {
            return PreconditionOutcome.Block("UnsupportedPlanVersion");
        }

        if (plan.ProfileKey.Value.Length == 0)
        {
            return PreconditionOutcome.Block("ProfileRootEmpty");
        }

        // Profile root must still exist and be absolute (re-check identity).
        if (!CanonicalProfileKey.TryCreate(
                plan.ProfileKey.Value,
                out var rekey,
                out var error) ||
            rekey is null ||
            !rekey.Equals(plan.ProfileKey))
        {
            return PreconditionOutcome.Block(
                error ?? "ProfileRootInvalid");
        }

        // Root directory MUST exist. Validator never creates it.
        if (!Directory.Exists(plan.ProfileKey.Value))
        {
            return PreconditionOutcome.Block("ProfileRootMissing");
        }

        if (plan.ExpectedPreconditions.TryGetValue(
                "DestinationHash",
                out var expectedHash) &&
            plan.Steps.Count > 0)
        {
            var first = plan.Steps[0];
            var full = SafeJoin(plan.ProfileKey.Value, first.RelativePath);
            if (File.Exists(full))
            {
                var actual = HashFile(full);
                if (!string.Equals(
                        actual,
                        expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return PreconditionOutcome.Block(
                        "DestinationHashMismatch");
                }
            }
        }

        if (plan.ExpectedPreconditions.TryGetValue(
                "RequiredArchiveSha256",
                out var requiredSha) &&
            !string.IsNullOrWhiteSpace(plan.ArchiveSha256) &&
            !string.Equals(
                plan.ArchiveSha256,
                ArchiveTrustPolicy.CanonicalizeSha256(requiredSha),
                StringComparison.Ordinal))
        {
            return PreconditionOutcome.Block("ArchiveIdentityMismatch");
        }

        if (plan.ExpectedPreconditions.TryGetValue(
                "RequiredSourceExists",
                out var sourcePath) &&
            !string.IsNullOrWhiteSpace(sourcePath) &&
            !File.Exists(sourcePath))
        {
            return PreconditionOutcome.Block("SourceMissing");
        }

        // RF-05 full identity when SourceArchivePath is set on install-like.
        if (plan.OperationKind == GameOperationKind.InstallLike &&
            !string.IsNullOrWhiteSpace(plan.SourceArchivePath))
        {
            var identityOutcome = await ValidateRf05InstallIdentityAsync(
                    plan,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!identityOutcome.IsAllowed)
                return identityOutcome;
        }

        if (plan.ExpectedPreconditions.TryGetValue(
                "Throw",
                out var throwFlag) &&
            string.Equals(throwFlag, "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ValidatorFault");
        }

        return PreconditionOutcome.Allow();
    }

    private async Task<PreconditionOutcome> ValidateRf05InstallIdentityAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken)
    {
        // Plan must declare the full RF-05 identity tuple.
        if (plan.PackageId is null ||
            string.IsNullOrWhiteSpace(plan.ArchiveSha256) ||
            plan.AnalyzerVersion is not int planAnalyzer ||
            plan.PolicyVersion is not int planPolicy ||
            string.IsNullOrWhiteSpace(plan.SelectedRoot))
        {
            return PreconditionOutcome.Block("AnalysisStale");
        }

        if (planAnalyzer != ArchiveAnalyzer.AnalyzerVersion ||
            planPolicy != ArchiveTrustPolicy.Version)
        {
            return PreconditionOutcome.Block("AnalysisStale");
        }

        if (string.IsNullOrWhiteSpace(plan.SourceArchivePath) ||
            !File.Exists(plan.SourceArchivePath))
        {
            return PreconditionOutcome.Block("AnalysisStale");
        }

        // Fail closed: never invent a "current" snapshot from plan fields.
        // Production uses OrganizerRepositoryRf05IdentitySource; tests may
        // supply MutableRf05IdentitySource as an additional fault seam only.
        if (_identitySource is null)
            return PreconditionOutcome.Block("AnalysisStale");

        var snapshot = await _identitySource
            .GetCurrentAsync(plan, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            return PreconditionOutcome.Block("AnalysisStale");

        if (!snapshot.IsTrustedRf05Fingerprint)
            return PreconditionOutcome.Block("AnalysisStale");

        var planRoot = ArchiveTrustPolicy.NormalizeRoot(plan.SelectedRoot);
        var snapRoot = ArchiveTrustPolicy.NormalizeRoot(snapshot.SelectedRoot);
        if (!snapshot.PackageId.Equals(plan.PackageId.Value) ||
            !string.Equals(snapRoot, planRoot, StringComparison.Ordinal) ||
            snapshot.AnalyzerVersion != planAnalyzer ||
            snapshot.PolicyVersion != planPolicy)
        {
            return PreconditionOutcome.Block("AnalysisStale");
        }

        var liveSha = await ArchiveContentHasher.ComputeFileSha256Async(
                plan.SourceArchivePath!,
                cancellationToken)
            .ConfigureAwait(false);

        // PackageId, ArchiveSha256 (plan), live archive bytes, and current
        // trusted analysis SHA must all refer to the same content.
        if (!string.Equals(
                liveSha,
                plan.ArchiveSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return PreconditionOutcome.Block("AnalysisStale");
        }

        if (string.IsNullOrWhiteSpace(snapshot.ExpectedArchiveSha256) ||
            !string.Equals(
                liveSha,
                snapshot.ExpectedArchiveSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return PreconditionOutcome.Block("AnalysisStale");
        }

        // Fingerprint reconstructs as RF-05 trusted (not legacy).
        var identity = new ArchiveContentIdentity(
            plan.PackageId.Value,
            liveSha,
            planAnalyzer,
            planRoot,
            planPolicy);
        if (!ArchiveContentIdentity.TryParse(
                identity.FormatFingerprint(),
                out var reparsed) ||
            reparsed.PackageId != plan.PackageId.Value ||
            !string.Equals(
                reparsed.ArchiveSha256,
                liveSha,
                StringComparison.OrdinalIgnoreCase))
        {
            return PreconditionOutcome.Block("AnalysisStale");
        }

        return PreconditionOutcome.Allow();
    }

    private static string SafeJoin(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!combined.StartsWith(
                fullRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PathEscape");
        }

        return combined;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
