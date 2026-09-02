using RipperWorks.Core;

namespace RipperWorks.Organizer;

public static class ArchiveAnalyzerResultCodes
{
    public const string EmptyArchive = "EmptyArchive";
    public const string PasswordProtected = "PasswordProtected";
    public const string MultiVolumeOrIncomplete = "MultiVolumeOrIncomplete";
    public const string UnsafeEntry = "UnsafeEntry";
    public const string UnknownRoot = "UnknownRoot";
    public const string NestedArchiveOnly = "NestedArchiveOnly";
    public const string MultipleInstallRoots = "MultipleInstallRoots";
    public const string DuplicateInstallPath = "DuplicateInstallPath";
    public const string NoInstallableFiles = "NoInstallableFiles";
    public const string CorruptArchive = "CorruptArchive";
    public const string FomodSelectionRequired = "FomodSelectionRequired";
    public const string UnsupportedFomod = "UnsupportedFomod";
}

public sealed record ArchiveAnalysisEntry
{
    public long Id { get; init; }
    public required string OriginalPath { get; init; }
    public required string NormalizedPath { get; init; }
    public string? RelativeInstallPath { get; init; }
    public long EntrySize { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsInstallable { get; init; }
    public string? WarningCode { get; init; }
}

public sealed record ArchiveAnalysisDraft
{
    public required PackageRecord Package { get; init; }
    public int AnalyzerVersion { get; init; }
    public PackageAnalysisState State { get; init; }
    public string? DetectedRoot { get; init; }
    public string? SelectedRoot { get; init; }
    public IReadOnlyList<string> DetectedRoots { get; init; } = [];
    public IReadOnlyList<ArchiveAnalysisEntry> Entries { get; init; } = [];
    public string? ResultCode { get; init; }
    public string? ResultMessage { get; init; }
    public required string Fingerprint { get; init; }
    /// <summary>
    /// RF-05: SHA-256 of bytes actually analyzed (64 lowercase hex), when known.
    /// </summary>
    public string? ArchiveSha256 { get; init; }
    public int PolicyVersion { get; init; } = ArchiveTrustPolicy.Version;
    public int WarningCount =>
        Entries.Count(entry => !string.IsNullOrWhiteSpace(entry.WarningCode));
    public int InstallableFileCount =>
        Entries.Count(entry => entry.IsInstallable && !entry.IsDirectory);
}

public sealed record PackageAnalysisRecord
{
    public long Id { get; init; }
    public required PackageId PackageId { get; init; }
    public int AnalyzerVersion { get; init; }
    public PackageAnalysisState State { get; init; }
    public string? DetectedRoot { get; init; }
    public string? SelectedRoot { get; init; }
    public int EntryCount { get; init; }
    public int InstallableFileCount { get; init; }
    public int WarningCount { get; init; }
    public DateTime AnalyzedAtUtc { get; init; }
    public string? ResultCode { get; init; }
    public string? ResultMessage { get; init; }
    public long ArchiveFileSize { get; init; }
    public DateTime ArchiveLastWriteUtc { get; init; }
    public required string ArchivePath { get; init; }
    public required string Fingerprint { get; init; }
    public IReadOnlyList<string> DetectedRoots { get; init; } = [];
    public IReadOnlyList<ArchiveAnalysisEntry> Entries { get; init; } = [];
}

public sealed record LibraryGroupRecord(
    long Id,
    string Name,
    int SortOrder,
    bool IsExpanded,
    DateTime CreatedAtUtc,
    bool IsSelected);

public sealed record LibraryReconciliationResult(
    int FoundArchiveCount,
    int AddedCount,
    int UpdatedCount,
    int RemovedMissingCount,
    int MergedDuplicateCount);

public sealed record OrganizerPackageRecord
{
    public required PackageRecord Package { get; init; }
    public string? CustomDisplayName { get; init; }
    public string? CustomVersion { get; init; }
    public string Note { get; init; } = string.Empty;
    public long? GroupId { get; init; }
    public bool IsPresent { get; init; } = true;
    public DateTime? LastIndexedAtUtc { get; init; }
    public PackageAnalysisRecord? Analysis { get; init; }
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(CustomDisplayName)
            ? Package.DisplayName
            : CustomDisplayName;
    public string EffectiveVersion =>
        string.IsNullOrWhiteSpace(CustomVersion)
            ? Package.Version ?? string.Empty
            : CustomVersion;
}

public sealed record LibraryModRecord
{
    public required LibraryModId LibraryModId { get; init; }
    public required string DisplayName { get; init; }
    public string? CustomDisplayName { get; init; }
    public PackageSource Source { get; init; }
    public string? GameDomain { get; init; }
    public long? NexusModId { get; init; }
    public string? Author { get; init; }
    public string? Category { get; init; }
    public string? NexusUrl { get; init; }
    public long? GroupId { get; init; }
    public PackageId? PreferredArchiveId { get; init; }
    public IReadOnlyList<OrganizerPackageRecord> Archives { get; init; } = [];
    public IReadOnlyList<ArchiveFamilyLinkRecord> ArchiveLinks
        { get; init; } = [];

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(CustomDisplayName)
            ? DisplayName
            : CustomDisplayName;

    public OrganizerPackageRecord? PreferredArchive =>
        SelectArchive(PreferredArchiveId);

    public IReadOnlyList<OrganizerPackageRecord> InstalledArchives =>
        Archives.Where(archive =>
            archive.Package.InstallationState ==
            PackageInstallationState.Installed).ToArray();

    public PackageInstallationState InstallationState =>
        InstalledArchives.Count > 0
            ? PackageInstallationState.Installed
            : Archives.Any(archive =>
                archive.Package.InstallationState ==
                PackageInstallationState.PartiallyInstalled)
                ? PackageInstallationState.PartiallyInstalled
                : Archives.Any(archive =>
                    archive.Package.InstallationState ==
                    PackageInstallationState.Unknown)
                    ? PackageInstallationState.Unknown
                    : PackageInstallationState.NotInstalled;

    public OrganizerPackageRecord? SelectArchive(PackageId? preferred)
    {
        if (preferred is { } preferredId)
        {
            var selected = Archives.FirstOrDefault(archive =>
                archive.Package.PackageId == preferredId);
            if (selected is not null)
                return selected;
        }
        var installed = InstalledArchives
            .OrderByDescending(archive =>
                archive.Package.DownloadedAtUtc ??
                archive.LastIndexedAtUtc ??
                archive.Package.LastWriteUtc)
            .FirstOrDefault();
        if (installed is not null)
            return installed;
        return Archives
            .OrderByDescending(archive =>
                archive.Package.DownloadedAtUtc ??
                archive.LastIndexedAtUtc ??
                archive.Package.LastWriteUtc)
            .FirstOrDefault();
    }

    public ArchiveVersionOperation GetArchiveOperation(
        PackageId installedArchiveId,
        PackageId targetArchiveId)
    {
        if (installedArchiveId == targetArchiveId)
            return ArchiveVersionOperation.Reinstall;
        if (CanReach(installedArchiveId, targetArchiveId))
            return ArchiveVersionOperation.Update;
        if (CanReach(targetArchiveId, installedArchiveId))
            return ArchiveVersionOperation.Rollback;
        return ArchiveVersionOperation.Install;
    }

    private bool CanReach(PackageId from, PackageId to)
    {
        var visited = new HashSet<PackageId>();
        var queue = new Queue<PackageId>();
        queue.Enqueue(from);
        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current))
                continue;
            foreach (var next in ArchiveLinks
                         .Where(link =>
                             link.FromArchiveId == current)
                         .Select(link => link.ToArchiveId))
            {
                if (next == to)
                    return true;
                queue.Enqueue(next);
            }
        }
        return false;
    }
}

public sealed record LibraryModMigrationResult(
    int ArchiveCount,
    int ModCount,
    int GroupedArchiveCount);

public sealed record ArchiveRecordDeletionResult(
    bool Deleted,
    bool ParentRemoved,
    bool ParentHadRelations);

public sealed record ArchiveFamilyLinkRecord(
    LibraryModId LibraryModId,
    PackageId FromArchiveId,
    PackageId ToArchiveId,
    string Source);

public sealed record ConfirmedArchiveUpdatePair(
    string GameDomain,
    long NexusModId,
    long CurrentFileId,
    long AvailableFileId,
    string Source = "NexusUpdateCheck");

public sealed record ArchiveFamilyReconciliationResult(
    int ConfirmedPairCount,
    int UpdatedArchiveCount);

public sealed record AnalysisQueueProgress(
    int Completed,
    int Total,
    PackageId? CurrentPackageId,
    bool IsCancellationRequested);

public sealed record InstallOperationFileRecord
{
    public long Id { get; init; }
    public required Guid OperationId { get; init; }
    public required string RelativeGamePath { get; init; }
    public required string NewContentHash { get; init; }
    public string? PreviousContentHash { get; init; }
    public bool PreviousFileExisted { get; init; }
    public int Sequence { get; init; }
    public bool Applied { get; init; }
}
