namespace RipperWorks.Core;

public enum InstallOperationStatus
{
    InProgress = 0,
    Completed = 1,
    RolledBack = 2,
    RecoveryRequired = 3,
    Blocked = 4
}

public enum InstallOperationPhase
{
    Created = 0,
    Staging = 1,
    BackingUp = 2,
    ManifestPlanned = 3,
    Deploying = 4,
    Finalizing = 5,
    Completed = 6,
    RollingBack = 7
}

public sealed record InstallOperationRecord
{
    public required Guid OperationId { get; init; }
    public required PackageId PackageId { get; init; }
    public required string OperationType { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public InstallOperationStatus Status { get; init; }
    public InstallOperationPhase CurrentPhase { get; init; }
    public string? ErrorMessage { get; init; }
    public int AppliedFileCount { get; init; }
}

public sealed record InstalledModRecord
{
    public required PackageId PackageId { get; init; }
    public required string ArchivePath { get; init; }
    public required string ArchiveFingerprint { get; init; }
    public required string AnalysisFingerprint { get; init; }
    public string? SelectedRoot { get; init; }
    public DateTime? InstalledAtUtc { get; init; }
    public PackageInstallationState InstallationState { get; init; }
}

public sealed record InstalledFileRecord
{
    public required PackageId PackageId { get; init; }
    public required string RelativeGamePath { get; init; }
    public required string InstalledContentHash { get; init; }
    public string? PreviousContentHash { get; init; }
    public bool PreviousFileExisted { get; init; }
    public int Sequence { get; init; }
}

public enum ModInstallationProgressPhase
{
    PreparingArchive,
    PreservingOriginals,
    Installing,
    Finalizing,
    RollingBack
}

public sealed record ModInstallationProgress(
    ModInstallationProgressPhase Phase,
    int CompletedFiles,
    int TotalFiles);

public sealed record ModInstallationResult
{
    public bool Success { get; init; }
    public Guid? OperationId { get; init; }
    public InstallOperationStatus Status { get; init; }
    public PackageInstallationState InstallationState { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public InstallPlan? Plan { get; init; }
}

public sealed record ModifiedFileConfirmationIdentity(
    string RelativeGamePath,
    string ObservedLiveHash);

public sealed record ModRemovalPlan
{
    public required PackageId PackageId { get; init; }
    public int FilesToDelete { get; init; }
    public int FilesToRestore { get; init; }
    public int LayersToDetach { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorDetail { get; init; }
    public IReadOnlyList<string> ProblemPaths { get; init; } = [];
    public IReadOnlyList<string> EligibleModifiedPaths { get; init; } = [];
    public IReadOnlyList<ModifiedFileConfirmationIdentity> EligibleModifiedIdentities { get; init; } = [];
    public IReadOnlyList<string> IneligibleProblemPaths { get; init; } = [];
    public bool HasEligibleModifiedFiles =>
        string.Equals(ErrorCode, "InstalledFilesModified", StringComparison.Ordinal) &&
        EligibleModifiedPaths.Count > 0 &&
        IneligibleProblemPaths.Count == 0;
    public bool CanRemove =>
        string.IsNullOrWhiteSpace(ErrorCode) &&
        FilesToDelete + FilesToRestore + LayersToDetach > 0;
}

public sealed record ModRemovalResult
{
    public bool Success { get; init; }
    public Guid? OperationId { get; init; }
    public InstallOperationStatus Status { get; init; }
    public PackageInstallationState InstallationState { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> ProblemPaths { get; init; } = [];
}

public sealed record ModArchiveSwitchPlan
{
    public required PackageId InstalledArchiveId { get; init; }
    public required PackageId TargetArchiveId { get; init; }
    public int FilesToAdd { get; init; }
    public int FilesToReplace { get; init; }
    public int ObsoleteFiles { get; init; }
    public int LowerLayersToRestore { get; init; }
    public int ConflictCount { get; init; }
    public int BlockedCount { get; init; }
    public ArchiveVersionOperation Operation { get; init; }
    public IReadOnlyList<ArchiveSwitchPlanEntry> Entries
        { get; init; } = [];
    public IReadOnlyList<PackageId> BlockingPackageIds
        { get; init; } = [];
    public string ValidationToken { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public string? ErrorDetail { get; init; }
    public IReadOnlyList<string> ProblemPaths { get; init; } = [];
    public IReadOnlyList<string> EligibleModifiedPaths { get; init; } = [];
    public IReadOnlyList<ModifiedFileConfirmationIdentity> EligibleModifiedIdentities { get; init; } = [];
    public IReadOnlyList<string> IneligibleProblemPaths { get; init; } = [];
    public bool HasEligibleModifiedFiles =>
        string.Equals(ErrorCode, "InstalledFilesModified", StringComparison.Ordinal) &&
        EligibleModifiedPaths.Count > 0 &&
        IneligibleProblemPaths.Count == 0;
    public bool CanSwitch => string.IsNullOrWhiteSpace(ErrorCode);
}

public enum ArchiveSwitchPlanAction
{
    Add,
    ReplaceVersion,
    RemoveObsolete,
    RestoreLowerLayer,
    Conflict,
    Blocked,
    Reinstall
}

public sealed class ModifiedFilesAuthorization
{
    public static ModifiedFilesAuthorization Empty { get; } = new(
        new Dictionary<PackageId, IReadOnlyDictionary<string, string?>>());

    private readonly IReadOnlyDictionary<PackageId, IReadOnlyDictionary<string, string?>> _authorizations;

    public ModifiedFilesAuthorization(
        IReadOnlyDictionary<PackageId, IReadOnlyDictionary<string, string?>> authorizations)
    {
        _authorizations = authorizations ??
            throw new ArgumentNullException(nameof(authorizations));
    }

    public static ModifiedFilesAuthorization ForPackage(
        PackageId packageId,
        IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                dict[NormalizePath(path)] = null;
            }
        }
        return new ModifiedFilesAuthorization(
            new Dictionary<PackageId, IReadOnlyDictionary<string, string?>>
            {
                [packageId] = dict
            });
    }

    public static ModifiedFilesAuthorization ForPackage(
        PackageId packageId,
        IEnumerable<ModifiedFileConfirmationIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in identities)
        {
            if (!string.IsNullOrWhiteSpace(id.RelativeGamePath))
            {
                dict[NormalizePath(id.RelativeGamePath)] = id.ObservedLiveHash;
            }
        }
        return new ModifiedFilesAuthorization(
            new Dictionary<PackageId, IReadOnlyDictionary<string, string?>>
            {
                [packageId] = dict
            });
    }

    public static ModifiedFilesAuthorization Combine(
        IEnumerable<ModifiedFilesAuthorization> authorizations)
    {
        ArgumentNullException.ThrowIfNull(authorizations);
        var combined = new Dictionary<PackageId, Dictionary<string, string?>>();
        foreach (var auth in authorizations)
        {
            foreach (var (packageId, paths) in auth._authorizations)
            {
                if (!combined.TryGetValue(packageId, out var dict))
                {
                    dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    combined[packageId] = dict;
                }
                foreach (var (path, hash) in paths)
                {
                    dict[NormalizePath(path)] = hash;
                }
            }
        }
        return new ModifiedFilesAuthorization(
            combined.ToDictionary(
                k => k.Key,
                v => (IReadOnlyDictionary<string, string?>)v.Value));
    }

    public bool IsAuthorized(PackageId packageId, string relativeGamePath, string? currentLiveHash = null)
    {
        if (string.IsNullOrWhiteSpace(relativeGamePath))
            return false;
        if (!_authorizations.TryGetValue(packageId, out var paths))
            return false;
        if (!paths.TryGetValue(NormalizePath(relativeGamePath), out var expectedHash))
            return false;
        if (expectedHash is null)
            return true;
        if (currentLiveHash is null)
            return false;
        return string.Equals(expectedHash, currentLiveHash, StringComparison.Ordinal);
    }

    public bool HasAnyAuthorization =>
        _authorizations.Values.Any(paths => paths.Count > 0);

    public IReadOnlyCollection<PackageId> AuthorizedPackages =>
        _authorizations.Keys.ToList();

    public IReadOnlySet<string> GetAuthorizedPaths(PackageId packageId) =>
        _authorizations.TryGetValue(packageId, out var paths)
            ? paths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');
}

public sealed record ArchiveSwitchPlanEntry(
    string RelativeGamePath,
    ArchiveSwitchPlanAction Action,
    string? OwnerDisplayName = null);

public enum ArchiveVersionOperation
{
    Install,
    Update,
    Rollback,
    Reinstall
}

public sealed record ModArchiveSwitchResult
{
    public bool Success { get; init; }
    public bool RolledBack { get; init; }
    public bool RecoveryRequired { get; init; }
    public Guid? OperationId { get; init; }
    public string? ErrorCode { get; init; }
    public ModArchiveSwitchPlan? Plan { get; init; }
}

public enum LibraryArchiveDeletionStatus
{
    Deleted,
    InstalledArchiveProtected,
    RemovalFailed,
    ArchiveNotFound,
    Failed
}

public sealed record LibraryArchiveDeletionResult
{
    public LibraryArchiveDeletionStatus Status { get; init; }
    public bool Success =>
        Status == LibraryArchiveDeletionStatus.Deleted;
    public bool ParentRemoved { get; init; }
    public bool ParentHadRelations { get; init; }
    public string? ErrorMessage { get; init; }
}

public enum LibraryModFullDeletionStatus
{
    Deleted,
    ModNotFound,
    RemovalFailed,
    FileSystemFailed,
    Failed
}

public sealed record LibraryModFullDeletionResult
{
    public LibraryModFullDeletionStatus Status { get; init; }
    public bool Success =>
        Status == LibraryModFullDeletionStatus.Deleted;
    public int DeletedArchiveCount { get; init; }
    public IReadOnlyList<string> MissingPaths { get; init; } = [];
    public string? ErrorMessage { get; init; }
}
