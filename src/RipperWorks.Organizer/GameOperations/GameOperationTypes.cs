using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using RipperWorks.Core;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>RF-06 journal format version. Unknown/newer fails closed.</summary>
public static class GameOperationJournalVersions
{
    public const int FormatVersion = 1;
}

/// <summary>RF-06 immutable plan format version.</summary>
public static class GameOperationPlanVersions
{
    public const int Current = 1;
}

public readonly record struct OperationId(Guid Value)
{
    public static OperationId CreateNew() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
    public static OperationId Parse(string value) =>
        new(Guid.Parse(value));
}

/// <summary>
/// Caller-provided opaque key for one logical command.
/// Not derived solely from PackageId/SHA/time.
/// </summary>
public readonly record struct IdempotencyKey
{
    public IdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "Idempotency key is required.",
                nameof(value));
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public enum GameOperationKind
{
    InstallLike = 1,
    RemoveLike = 2
}

public enum GameOperationState
{
    Accepted = 1,
    Queued = 2,
    Validating = 3,
    ReadyToCommit = 4,
    MutationStarted = 5,
    Executing = 6,
    Finalizing = 7,
    Completed = 8,
    CancelledBeforeCommit = 20,
    BlockedBeforeCommit = 21,
    FailedBeforeCommit = 22,
    RecoveryRequired = 30
}

public enum GameOperationPhase
{
    None = 0,
    Accepted = 1,
    Queued = 2,
    Validating = 3,
    ReadyToCommit = 4,
    MutationStarted = 5,
    Executing = 6,
    Finalizing = 7,
    Terminal = 8
}

public enum GameOperationStepState
{
    Pending = 0,
    IntentRecorded = 1,
    Applied = 2,
    Verified = 3,
    Failed = 4
}

public enum GameOperationStepKind
{
    WriteFile = 1,
    DeleteFile = 2,
    VerifyFile = 3
}

/// <summary>
/// Canonical absolute game-root identity for queue/idempotency isolation.
/// Windows: case-insensitive full path without trailing separator.
/// </summary>
public sealed class CanonicalProfileKey : IEquatable<CanonicalProfileKey>
{
    private CanonicalProfileKey(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(
        string? gameRoot,
        out CanonicalProfileKey? key,
        out string? errorCode)
    {
        key = null;
        errorCode = null;
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            errorCode = "ProfileRootEmpty";
            return false;
        }

        var trimmed = gameRoot.Trim();
        if (!Path.IsPathRooted(trimmed))
        {
            errorCode = "ProfileRootRelative";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch
        {
            errorCode = "ProfileRootInvalid";
            return false;
        }

        // Drop trailing directory separators (except root like C:\).
        full = full.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (full.EndsWith(':'))
            full += Path.DirectorySeparatorChar;

        // Canonical form: upper-invariant for Windows identity.
        var identity = full.ToUpperInvariant();
        key = new CanonicalProfileKey(identity);
        return true;
    }

    public static CanonicalProfileKey Create(string gameRoot)
    {
        if (!TryCreate(gameRoot, out var key, out var error) || key is null)
            throw new ArgumentException(error ?? "Invalid profile root.");
        return key;
    }

    public bool Equals(CanonicalProfileKey? other) =>
        other is not null &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is CanonicalProfileKey other && Equals(other);

    public override int GetHashCode() =>
        StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>
/// Explicit per-resource expected identities for one relative path.
/// <see cref="ExpectedBeforeIdentity"/> / <see cref="ExpectedAfterIdentity"/>
/// are part of the immutable plan (PlanHash) and must not be inferred from
/// another step's <see cref="ContentIdentity"/>.
/// Convention: <c>Missing</c> means the path must not exist; otherwise a
/// content token or 64-hex SHA-256 for the resource at RelativePath.
/// </summary>
public sealed record GameOperationStepSpec(
    int Sequence,
    string StepKey,
    GameOperationStepKind StepKind,
    string RelativePath,
    string? ContentIdentity,
    string? ExpectedBeforeIdentity = null,
    string? ExpectedAfterIdentity = null);

/// <summary>Well-known content-identity tokens for plan steps.</summary>
public static class GameOperationContentIdentities
{
    public const string Missing = GameOperationStepIdentity.Missing;
    public const string Sha256Prefix = GameOperationStepIdentity.Sha256Prefix;
}

/// <summary>
/// Immutable plan. No ViewModels, services, streams, or CTS.
/// Steps and preconditions are fixed at construction; hash is deterministic.
/// </summary>
public sealed class GameOperationPlan
{
    private readonly GameOperationStepSpec[] _steps;
    private readonly Dictionary<string, string> _preconditions;

    public GameOperationPlan(
        int planVersion,
        GameOperationKind operationKind,
        CanonicalProfileKey profileKey,
        string operationIdentity,
        PackageId? packageId,
        string? archiveSha256,
        int? analyzerVersion,
        string? selectedRoot,
        int? policyVersion,
        IReadOnlyList<GameOperationStepSpec> steps,
        IReadOnlyDictionary<string, string>? expectedPreconditions = null,
        string? sourceArchivePath = null)
    {
        if (planVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(planVersion));
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationIdentity);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
            throw new ArgumentException("Plan requires at least one step.");
        if (!Enum.IsDefined(operationKind))
            throw new ArgumentOutOfRangeException(nameof(operationKind));

        var ordered = steps
            .OrderBy(s => s.Sequence)
            .Select(s => s with
            {
                RelativePath = (s.RelativePath ?? string.Empty)
                    .Replace('/', '\\').Trim(),
                StepKey = (s.StepKey ?? string.Empty)
                    .Replace('/', '\\').Trim()
            })
            .ToArray();
        ValidateSteps(ordered);

        PlanVersion = planVersion;
        OperationKind = operationKind;
        ProfileKey = profileKey;
        OperationIdentity = operationIdentity.Trim();
        PackageId = packageId;
        ArchiveSha256 = ArchiveTrustPolicy.CanonicalizeSha256(archiveSha256);
        AnalyzerVersion = analyzerVersion;
        SelectedRoot = string.IsNullOrWhiteSpace(selectedRoot)
            ? null
            : ArchiveTrustPolicy.NormalizeRoot(selectedRoot);
        PolicyVersion = policyVersion;
        SourceArchivePath = string.IsNullOrWhiteSpace(sourceArchivePath)
            ? null
            : Path.GetFullPath(sourceArchivePath.Trim());
        _steps = ordered;
        Steps = Array.AsReadOnly(_steps);
        _preconditions = expectedPreconditions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(
                expectedPreconditions,
                StringComparer.Ordinal);
        ExpectedPreconditions = new ReadOnlyDictionary<string, string>(_preconditions);
        PlanHash = ComputePlanHash(this);
    }

    public int PlanVersion { get; }
    public GameOperationKind OperationKind { get; }
    public CanonicalProfileKey ProfileKey { get; }
    public string OperationIdentity { get; }
    public PackageId? PackageId { get; }
    public string? ArchiveSha256 { get; }
    public int? AnalyzerVersion { get; }
    public string? SelectedRoot { get; }
    public int? PolicyVersion { get; }
    /// <summary>Optional live archive path for RF-05 revalidation (not hashed).</summary>
    public string? SourceArchivePath { get; }
    public IReadOnlyList<GameOperationStepSpec> Steps { get; }
    public IReadOnlyDictionary<string, string> ExpectedPreconditions { get; }
    public string PlanHash { get; }

    public string Serialize() => PlanSerialization.Serialize(this);

    public static GameOperationPlan Deserialize(string json) =>
        PlanSerialization.Deserialize(json);

    private static void ValidateSteps(GameOperationStepSpec[] steps)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            if (step.Sequence != i + 1)
            {
                throw new ArgumentException(
                    "Step sequences must be unique and strictly 1..n.");
            }

            if (!Enum.IsDefined(step.StepKind))
                throw new ArgumentException("Undefined step kind.");
            if (string.IsNullOrWhiteSpace(step.StepKey))
                throw new ArgumentException("StepKey is required.");
            if (string.IsNullOrWhiteSpace(step.RelativePath))
                throw new ArgumentException("RelativePath is required.");
            if (step.RelativePath.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(step.RelativePath))
            {
                throw new ArgumentException("Unsafe relative path.");
            }
        }
    }

    private static string ComputePlanHash(GameOperationPlan plan)
    {
        var payload = PlanSerialization.SerializeCanonical(plan);
        var bytes = Encoding.UTF8.GetBytes(payload);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
public sealed record GameOperationResult
{
    public required OperationId OperationId { get; init; }
    public required GameOperationState State { get; init; }
    public required string PlanHash { get; init; }
    public required string IdempotencyKey { get; init; }
    public string? ErrorCode { get; init; }
    public string? RedactedErrorDetail { get; init; }
    public bool CancellationRequested { get; init; }
    public bool JoinedExisting { get; init; }
    public int ExecutorInvocationCount { get; init; }
    public int SyntheticWriteCount { get; init; }
}

public sealed record PreconditionOutcome(
    bool IsAllowed,
    string? ErrorCode = null,
    string? RedactedDetail = null)
{
    public static PreconditionOutcome Allow() => new(true);
    public static PreconditionOutcome Block(string code, string? detail = null) =>
        new(false, code, detail);
}

/// <summary>
/// Read-only RF-05 identity authority for install-like pre-mutation checks.
/// Production: <see cref="OrganizerRepositoryRf05IdentitySource"/>.
/// Tests may supply a mutable seam; the default validator never invents a
/// "current" snapshot from the plan itself when this source is absent.
/// </summary>
public interface IGameOperationRf05IdentitySource
{
    Task<Rf05IdentitySnapshot?> GetCurrentAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Current store-backed RF-05 identity for one package.
/// <see cref="IsTrustedRf05Fingerprint"/> is false for legacy fingerprints.
/// </summary>
public sealed record Rf05IdentitySnapshot(
    PackageId PackageId,
    string SelectedRoot,
    int AnalyzerVersion,
    int PolicyVersion,
    string? ExpectedArchiveSha256,
    bool IsTrustedRf05Fingerprint = true);

/// <summary>
/// Optional managed-path guard for synthetic safe-primitive fixtures.
/// Null guard allows all paths (shadow default).
/// </summary>
public interface IShadowManagedPathGuard
{
    bool CanWrite(string relativePath);
    bool CanDelete(string relativePath);
}

public interface IGameOperationPreconditionValidator
{
    Task<PreconditionOutcome> ValidateAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default);
}

public interface IGameOperationExecutor
{
    Task PrepareAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default);

    Task ExecuteStepAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default);

    Task VerifyStepAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default);

    Task FinalizeAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit binding between a shadow executor and its one TEMP harness.
/// Binding is construction-only and must perform no filesystem mutation.
/// </summary>
internal interface IShadowHarnessBoundExecutor
{
    ShadowHarnessPermit? BoundPermit { get; }
    IGameOperationResourceStateReader ResourceStateReader { get; }
    IGameOperationPlanContractValidator PlanContractValidator { get; }
    PreconditionOutcome ValidateHarness(ShadowHarnessPermit permit);
}

public interface IGameOperationJournal
{
    int FormatVersion { get; }
    string DatabasePath { get; }
    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
    Task<GameOperationRecord?> FindByIdempotencyAsync(
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);
    Task<GameOperationRecord> InsertAcceptedAsync(
        OperationId operationId,
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey,
        GameOperationPlan plan,
        CancellationToken cancellationToken = default);
    Task TransitionAsync(
        OperationId operationId,
        GameOperationState from,
        GameOperationState to,
        GameOperationPhase phase,
        string? errorCode = null,
        string? redactedDetail = null,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Records step intent from the durable SerializedPlan authority.
    /// Caller-passed plan is not accepted; step fields are checked against
    /// the persisted plan inside one SQLite transaction.
    /// </summary>
    Task RecordStepIntentAsync(
        OperationId operationId,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default);
    Task RecordStepAppliedAsync(
        OperationId operationId,
        int sequence,
        CancellationToken cancellationToken = default);
    Task RecordStepVerifiedAsync(
        OperationId operationId,
        int sequence,
        CancellationToken cancellationToken = default);
    Task SetCancellationRequestedAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default);
    Task<GameOperationRecord?> LoadAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameOperationRecord>> LoadByProfileAsync(
        CanonicalProfileKey profileKey,
        CancellationToken cancellationToken = default);
    Task<bool> HasIncompletePostCommitAsync(
        CanonicalProfileKey profileKey,
        CancellationToken cancellationToken = default);
}

public sealed record GameOperationRecord
{
    public required OperationId OperationId { get; init; }
    public required string CanonicalProfileKey { get; init; }
    public required string IdempotencyKey { get; init; }
    public required GameOperationKind OperationKind { get; init; }
    public required int PlanVersion { get; init; }
    public required string PlanHash { get; init; }
    public required string SerializedPlan { get; init; }
    public required GameOperationState State { get; init; }
    public required GameOperationPhase Phase { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public bool CancellationRequested { get; init; }
    public string? ErrorCode { get; init; }
    public string? RedactedErrorDetail { get; init; }
    public int LastSequence { get; init; }
    public int JournalFormatVersion { get; init; }
    public IReadOnlyList<GameOperationStepRecord> Steps { get; init; } = [];
}

public sealed record GameOperationStepRecord
{
    public required OperationId OperationId { get; init; }
    public required int Sequence { get; init; }
    public required string StepKey { get; init; }
    public required GameOperationStepKind StepKind { get; init; }
    public required GameOperationStepState State { get; init; }
    public DateTime? IntentRecordedAtUtc { get; init; }
    public DateTime? AppliedAtUtc { get; init; }
    public DateTime? VerifiedAtUtc { get; init; }
    public string? ExpectedBeforeIdentity { get; init; }
    public string? ExpectedAfterIdentity { get; init; }
    public string? ErrorCode { get; init; }
    public string? RedactedErrorDetail { get; init; }
}
