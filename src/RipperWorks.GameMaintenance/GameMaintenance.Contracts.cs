using RipperWorks.Core;

namespace RipperWorks.GameMaintenance;

public enum GameMaintenanceScanStatus
{
    Clean,
    ForeignModificationDetected,
    ManagedContentPresent,
    InvalidGameRoot,
    ScanFailed,
    GameRunning
}

public sealed record GameMaintenanceCandidateFile(
    string RelativeGamePath,
    long ByteLength,
    string ObservedSha256);

public sealed record GameMaintenanceScanResult(
    string GameRoot,
    Guid ScanId,
    DateTimeOffset ScannedAtUtc,
    GameMaintenanceScanStatus Status,
    IReadOnlyList<GameMaintenanceCandidateFile> Candidates,
    string? ErrorMessage = null)
{
    public long TotalSizeBytes => Candidates.Sum(c => c.ByteLength);
    public int FileCount => Candidates.Count;
}

public sealed record GameMaintenanceCleanupOptions(
    bool CreateBackup,
    string? CustomBackupRoot = null);

public enum GameMaintenanceCleanupStatus
{
    Success,
    GameRunning,
    InvalidScan,
    CandidateMismatch,
    BackupFailed,
    CleanupFailed,
    PathSafetyViolation
}

public sealed record GameMaintenanceCleanupResult(
    Guid ScanId,
    GameMaintenanceCleanupStatus Status,
    int DeletedCount,
    string? BackupId = null,
    string? ErrorMessage = null);

public sealed record GameMaintenanceBackupManifest(
    int FormatVersion,
    string BackupId,
    DateTimeOffset CreatedAtUtc,
    string CanonicalGameRoot,
    IReadOnlyList<GameMaintenanceBackupFileEntry> Files);

public sealed record GameMaintenanceBackupFileEntry(
    string RelativeGamePath,
    long ByteLength,
    string Sha256);

public sealed record GameMaintenanceBackupHeader(
    string BackupId,
    DateTimeOffset CreatedAtUtc,
    string CanonicalGameRoot,
    int FileCount,
    long TotalSizeBytes,
    string BackupDirectoryPath);

public enum GameMaintenanceRestorePreflightStatus
{
    Ready,
    Conflict,
    BackupNotFound,
    InvalidManifest,
    GameRunning,
    PathSafetyViolation,
    ManagedInstallationsPresent
}

public sealed record GameMaintenanceRestoreConflict(
    string RelativeGamePath,
    string ExistingLiveSha256,
    string BackupSha256);

public sealed record GameMaintenanceRestorePreflightResult(
    string BackupId,
    GameMaintenanceRestorePreflightStatus Status,
    IReadOnlyList<string> FilesToRestore,
    IReadOnlyList<string> UnchangedFiles,
    IReadOnlyList<GameMaintenanceRestoreConflict> Conflicts,
    string? ErrorMessage = null);

public enum GameMaintenanceRestoreStatus
{
    Success,
    PreflightConflict,
    GameRunning,
    RestoreFailed,
    VerificationFailed,
    BackupNotFound,
    ManagedInstallationsPresent,
    PathSafetyViolation
}

public sealed record GameMaintenanceRestoreResult(
    string BackupId,
    GameMaintenanceRestoreStatus Status,
    int RestoredCount,
    string? ErrorMessage = null);

public interface IGameMaintenanceService
{
    Task<GameMaintenanceScanResult> ScanAsync(
        string gameRoot,
        CancellationToken cancellationToken = default);

    Task<GameMaintenanceCleanupResult> CleanupAsync(
        GameMaintenanceScanResult scan,
        GameMaintenanceCleanupOptions options,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameMaintenanceBackupHeader>> ListBackupsAsync(
        string gameRoot,
        string? customBackupRoot = null,
        CancellationToken cancellationToken = default);

    Task<GameMaintenanceRestorePreflightResult> PreflightRestoreAsync(
        string backupId,
        string gameRoot,
        string? customBackupRoot = null,
        CancellationToken cancellationToken = default);

    Task<GameMaintenanceRestoreResult> RestoreAsync(
        string backupId,
        string gameRoot,
        string? customBackupRoot = null,
        CancellationToken cancellationToken = default);
}

public interface IGameMaintenanceManagedStateProvider
{
    Task<bool> HasManagedInstallationsAsync(
        string gameRoot,
        CancellationToken cancellationToken = default);
}

public sealed class NullGameMaintenanceManagedStateProvider : IGameMaintenanceManagedStateProvider
{
    public static readonly NullGameMaintenanceManagedStateProvider Instance = new();
    public Task<bool> HasManagedInstallationsAsync(string gameRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
