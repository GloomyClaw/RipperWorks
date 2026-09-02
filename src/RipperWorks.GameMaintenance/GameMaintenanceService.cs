using RipperWorks.Core;

namespace RipperWorks.GameMaintenance;

public sealed class GameMaintenanceService(
    IGameProcessAdapter processAdapter,
    IGameMaintenanceManagedStateProvider? managedStateProvider = null) : IGameMaintenanceService
{
    private readonly IGameProcessAdapter _processAdapter = processAdapter ?? throw new ArgumentNullException(nameof(processAdapter));
    private readonly IGameMaintenanceManagedStateProvider _managedStateProvider = managedStateProvider ?? NullGameMaintenanceManagedStateProvider.Instance;

    public async Task<GameMaintenanceScanResult> ScanAsync(
        string gameRoot,
        CancellationToken cancellationToken = default)
    {
        if (!GameMaintenanceScanner.TryValidateRootAndRuntime(
            gameRoot,
            _processAdapter,
            out var normalizedRoot,
            out var errorStatus,
            out var errorMsg))
        {
            return new GameMaintenanceScanResult(
                normalizedRoot,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                errorStatus,
                [],
                errorMsg);
        }

        if (await _managedStateProvider.HasManagedInstallationsAsync(normalizedRoot, cancellationToken))
        {
            return new GameMaintenanceScanResult(
                normalizedRoot,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                GameMaintenanceScanStatus.ManagedContentPresent,
                []);
        }

        return await GameMaintenanceScanner.ScanAsync(normalizedRoot, _processAdapter, cancellationToken);
    }

    public async Task<GameMaintenanceCleanupResult> CleanupAsync(
        GameMaintenanceScanResult scan,
        GameMaintenanceCleanupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(options);

        if (_processAdapter.IsGameRunning())
        {
            return new GameMaintenanceCleanupResult(
                scan.ScanId,
                GameMaintenanceCleanupStatus.GameRunning,
                0,
                ErrorMessage: "Cyberpunk 2077 is currently running.");
        }

        if (string.IsNullOrWhiteSpace(scan.GameRoot) || !Directory.Exists(scan.GameRoot))
        {
            return new GameMaintenanceCleanupResult(
                scan.ScanId,
                GameMaintenanceCleanupStatus.InvalidScan,
                0,
                ErrorMessage: "Invalid game folder.");
        }

        if (await _managedStateProvider.HasManagedInstallationsAsync(scan.GameRoot, cancellationToken))
        {
            return new GameMaintenanceCleanupResult(
                scan.ScanId,
                GameMaintenanceCleanupStatus.InvalidScan,
                0,
                ErrorMessage: "Cannot perform maintenance cleanup while RipperWorks-managed mods are installed.");
        }

        if (scan.Candidates.Count == 0)
        {
            return new GameMaintenanceCleanupResult(
                scan.ScanId,
                GameMaintenanceCleanupStatus.Success,
                0);
        }

        var normalizedGameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scan.GameRoot));

        // 1. Path Safety & Reparse Validation
        foreach (var candidate in scan.Candidates)
        {
            var targetPath = Path.Combine(normalizedGameRoot, candidate.RelativeGamePath.Replace('/', '\\'));
            if (!GameMaintenanceScanner.IsSafelyContainedWithoutReparse(targetPath, normalizedGameRoot))
            {
                return new GameMaintenanceCleanupResult(
                    scan.ScanId,
                    GameMaintenanceCleanupStatus.PathSafetyViolation,
                    0,
                    ErrorMessage: $"Path safety violation: {candidate.RelativeGamePath}");
            }
        }

        // 2. TOCTOU Validation (Pre-check all candidates before any mutation)
        foreach (var candidate in scan.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(normalizedGameRoot, candidate.RelativeGamePath.Replace('/', '\\'));
            if (!File.Exists(targetPath))
            {
                return new GameMaintenanceCleanupResult(
                    scan.ScanId,
                    GameMaintenanceCleanupStatus.CandidateMismatch,
                    0,
                    ErrorMessage: $"Candidate file was deleted or moved before cleanup: {candidate.RelativeGamePath}");
            }

            if (!GameMaintenanceScanner.IsSafelyContainedWithoutReparse(targetPath, normalizedGameRoot))
            {
                return new GameMaintenanceCleanupResult(
                    scan.ScanId,
                    GameMaintenanceCleanupStatus.PathSafetyViolation,
                    0,
                    ErrorMessage: $"Path safety violation: {candidate.RelativeGamePath}");
            }

            var liveHash = await GameMaintenanceScanner.ComputeSha256Async(targetPath, cancellationToken);
            if (!string.Equals(liveHash, candidate.ObservedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new GameMaintenanceCleanupResult(
                    scan.ScanId,
                    GameMaintenanceCleanupStatus.CandidateMismatch,
                    0,
                    ErrorMessage: $"Candidate file content changed before cleanup: {candidate.RelativeGamePath}");
            }
        }

        // 3. Backup Phase (if requested)
        string? backupId = null;
        if (options.CreateBackup)
        {
            try
            {
                backupId = await GameMaintenanceBackupStore.CreateBackupAsync(
                    normalizedGameRoot,
                    scan.Candidates,
                    options.CustomBackupRoot,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return new GameMaintenanceCleanupResult(
                    scan.ScanId,
                    GameMaintenanceCleanupStatus.BackupFailed,
                    0,
                    ErrorMessage: $"Backup creation failed: {ex.Message}");
            }
        }

        // 4. Deletion Phase
        var deletedCount = 0;
        var parentDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var candidate in scan.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = Path.Combine(normalizedGameRoot, candidate.RelativeGamePath.Replace('/', '\\'));

                if (!GameMaintenanceScanner.IsSafelyContainedWithoutReparse(targetPath, normalizedGameRoot))
                {
                    return new GameMaintenanceCleanupResult(
                        scan.ScanId,
                        GameMaintenanceCleanupStatus.PathSafetyViolation,
                        deletedCount,
                        BackupId: backupId,
                        ErrorMessage: $"Reparse point or safety violation detected during deletion: {candidate.RelativeGamePath}");
                }

                if (File.Exists(targetPath))
                {
                    // Second TOCTOU verification immediately prior to deletion
                    var liveHash = await GameMaintenanceScanner.ComputeSha256Async(targetPath, cancellationToken);
                    if (!string.Equals(liveHash, candidate.ObservedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return new GameMaintenanceCleanupResult(
                            scan.ScanId,
                            GameMaintenanceCleanupStatus.CandidateMismatch,
                            deletedCount,
                            BackupId: backupId,
                            ErrorMessage: $"Candidate file changed during cleanup: {candidate.RelativeGamePath}");
                    }

                    File.Delete(targetPath);
                    deletedCount++;

                    var parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        parentDirectories.Add(parent);
                    }
                }
            }

            // 5. Clean up now-empty parent directories
            CleanEmptyDirectories(parentDirectories, normalizedGameRoot);

            return new GameMaintenanceCleanupResult(
                scan.ScanId,
                GameMaintenanceCleanupStatus.Success,
                deletedCount,
                BackupId: backupId);
        }
        catch (Exception ex)
        {
            return new GameMaintenanceCleanupResult(
                scan.ScanId,
                GameMaintenanceCleanupStatus.CleanupFailed,
                deletedCount,
                BackupId: backupId,
                ErrorMessage: $"Cleanup failed: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<GameMaintenanceBackupHeader>> ListBackupsAsync(
        string gameRoot,
        string? customBackupRoot = null,
        CancellationToken cancellationToken = default) =>
        GameMaintenanceBackupStore.ListBackupsAsync(gameRoot, customBackupRoot, cancellationToken);

    public async Task<GameMaintenanceRestorePreflightResult> PreflightRestoreAsync(
        string backupId,
        string gameRoot,
        string? customBackupRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return new GameMaintenanceRestorePreflightResult(
                backupId,
                GameMaintenanceRestorePreflightStatus.InvalidManifest,
                [], [], [],
                "Invalid game root.");
        }

        if (_processAdapter.IsGameRunning())
        {
            return new GameMaintenanceRestorePreflightResult(
                backupId,
                GameMaintenanceRestorePreflightStatus.GameRunning,
                [], [], [],
                "Cyberpunk 2077 is currently running.");
        }

        var normalizedGameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        if (await _managedStateProvider.HasManagedInstallationsAsync(normalizedGameRoot, cancellationToken))
        {
            return new GameMaintenanceRestorePreflightResult(
                backupId,
                GameMaintenanceRestorePreflightStatus.ManagedInstallationsPresent,
                [], [], [],
                "Cannot restore backups while managed mods are installed.");
        }

        var manifest = await GameMaintenanceBackupStore.LoadManifestAsync(
            backupId, customBackupRoot, cancellationToken);
        if (manifest is null)
        {
            return new GameMaintenanceRestorePreflightResult(
                backupId,
                GameMaintenanceRestorePreflightStatus.BackupNotFound,
                [], [], [],
                $"Backup with id '{backupId}' was not found.");
        }

        var manifestGameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.CanonicalGameRoot));
        if (!string.Equals(manifestGameRoot, normalizedGameRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new GameMaintenanceRestorePreflightResult(
                backupId,
                GameMaintenanceRestorePreflightStatus.InvalidManifest,
                [], [], [],
                $"Backup belongs to a different game root ('{manifest.CanonicalGameRoot}').");
        }

        var filesToRestore = new List<string>();
        var unchangedFiles = new List<string>();
        var conflicts = new List<GameMaintenanceRestoreConflict>();

        foreach (var fileEntry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(normalizedGameRoot, fileEntry.RelativeGamePath.Replace('/', '\\'));

            if (!GameMaintenanceScanner.IsSafelyContainedWithoutReparse(targetPath, normalizedGameRoot))
            {
                return new GameMaintenanceRestorePreflightResult(
                    backupId,
                    GameMaintenanceRestorePreflightStatus.PathSafetyViolation,
                    [], [], [],
                    $"Path safety violation for {fileEntry.RelativeGamePath}");
            }

            if (!File.Exists(targetPath))
            {
                filesToRestore.Add(fileEntry.RelativeGamePath);
            }
            else
            {
                var liveHash = await GameMaintenanceScanner.ComputeSha256Async(targetPath, cancellationToken);
                if (string.Equals(liveHash, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    unchangedFiles.Add(fileEntry.RelativeGamePath);
                }
                else
                {
                    conflicts.Add(new GameMaintenanceRestoreConflict(
                        fileEntry.RelativeGamePath,
                        liveHash,
                        fileEntry.Sha256));
                }
            }
        }

        var status = conflicts.Count > 0
            ? GameMaintenanceRestorePreflightStatus.Conflict
            : GameMaintenanceRestorePreflightStatus.Ready;

        return new GameMaintenanceRestorePreflightResult(
            backupId,
            status,
            filesToRestore,
            unchangedFiles,
            conflicts);
    }

    public async Task<GameMaintenanceRestoreResult> RestoreAsync(
        string backupId,
        string gameRoot,
        string? customBackupRoot = null,
        CancellationToken cancellationToken = default)
    {
        var preflight = await PreflightRestoreAsync(backupId, gameRoot, customBackupRoot, cancellationToken);
        if (preflight.Status != GameMaintenanceRestorePreflightStatus.Ready)
        {
            var status = preflight.Status switch
            {
                GameMaintenanceRestorePreflightStatus.Conflict => GameMaintenanceRestoreStatus.PreflightConflict,
                GameMaintenanceRestorePreflightStatus.GameRunning => GameMaintenanceRestoreStatus.GameRunning,
                GameMaintenanceRestorePreflightStatus.BackupNotFound => GameMaintenanceRestoreStatus.BackupNotFound,
                GameMaintenanceRestorePreflightStatus.ManagedInstallationsPresent => GameMaintenanceRestoreStatus.ManagedInstallationsPresent,
                GameMaintenanceRestorePreflightStatus.PathSafetyViolation => GameMaintenanceRestoreStatus.PathSafetyViolation,
                _ => GameMaintenanceRestoreStatus.RestoreFailed
            };

            return new GameMaintenanceRestoreResult(
                backupId,
                status,
                0,
                ErrorMessage: preflight.ErrorMessage ?? (preflight.Conflicts.Count > 0 ? "Conflicts detected." : "Preflight failed."));
        }

        var backupDir = GameMaintenanceBackupStore.GetBackupDirectory(backupId, customBackupRoot);
        if (backupDir is null)
        {
            return new GameMaintenanceRestoreResult(
                backupId,
                GameMaintenanceRestoreStatus.BackupNotFound,
                0,
                "Backup directory not found.");
        }

        var payloadDir = Path.Combine(backupDir, "payload");
        var normalizedGameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        var manifest = await GameMaintenanceBackupStore.LoadManifestAsync(backupId, customBackupRoot, cancellationToken);
        if (manifest is null)
        {
            return new GameMaintenanceRestoreResult(
                backupId,
                GameMaintenanceRestoreStatus.BackupNotFound,
                0,
                "Manifest not found.");
        }

        // Validate complete payload BEFORE any target mutations
        foreach (var fileEntry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payloadFile = Path.Combine(payloadDir, fileEntry.RelativeGamePath.Replace('/', '\\'));
            if (!File.Exists(payloadFile))
            {
                return new GameMaintenanceRestoreResult(
                    backupId,
                    GameMaintenanceRestoreStatus.VerificationFailed,
                    0,
                    $"Payload file missing from backup: {fileEntry.RelativeGamePath}");
            }

            if (!GameMaintenanceScanner.IsSafelyContainedWithoutReparse(payloadFile, payloadDir))
            {
                return new GameMaintenanceRestoreResult(
                    backupId,
                    GameMaintenanceRestoreStatus.VerificationFailed,
                    0,
                    $"Payload path safety violation: {fileEntry.RelativeGamePath}");
            }

            var livePayloadHash = await GameMaintenanceScanner.ComputeSha256Async(payloadFile, cancellationToken);
            if (!string.Equals(livePayloadHash, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new GameMaintenanceRestoreResult(
                    backupId,
                    GameMaintenanceRestoreStatus.VerificationFailed,
                    0,
                    $"Corrupted backup payload for: {fileEntry.RelativeGamePath}");
            }
        }

        // Safe restore execution
        var restoredCount = 0;
        foreach (var fileEntry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payloadFile = Path.Combine(payloadDir, fileEntry.RelativeGamePath.Replace('/', '\\'));
            var targetPath = Path.Combine(normalizedGameRoot, fileEntry.RelativeGamePath.Replace('/', '\\'));

            if (!GameMaintenanceScanner.IsSafelyContainedWithoutReparse(targetPath, normalizedGameRoot))
            {
                return new GameMaintenanceRestoreResult(
                    backupId,
                    GameMaintenanceRestoreStatus.PathSafetyViolation,
                    restoredCount,
                    $"Path safety violation for target: {fileEntry.RelativeGamePath}");
            }

            if (File.Exists(targetPath))
            {
                var liveHash = await GameMaintenanceScanner.ComputeSha256Async(targetPath, cancellationToken);
                if (string.Equals(liveHash, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // Target has identical bytes -> NO-OP, do not rewrite
                    continue;
                }

                // New conflict appeared after preflight -> NEVER overwrite
                return new GameMaintenanceRestoreResult(
                    backupId,
                    GameMaintenanceRestoreStatus.PreflightConflict,
                    restoredCount,
                    $"Target file appeared with conflicting bytes: {fileEntry.RelativeGamePath}");
            }

            var targetDir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(targetDir);

            // Repeat reparse/containment check after directory creation
            if (!GameMaintenanceScanner.IsSafelyContainedWithoutReparse(targetDir, normalizedGameRoot) ||
                !GameMaintenanceScanner.IsSafelyContainedWithoutReparse(targetPath, normalizedGameRoot))
            {
                return new GameMaintenanceRestoreResult(
                    backupId,
                    GameMaintenanceRestoreStatus.PathSafetyViolation,
                    restoredCount,
                    $"Path safety violation after directory creation for: {fileEntry.RelativeGamePath}");
            }

            // Copy to temporary file first, verify hash, then move atomically without overwrite
            var tempPath = targetPath + ".ripperworks-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (!GameMaintenanceScanner.IsSafelyContainedWithoutReparse(tempPath, normalizedGameRoot))
                {
                    return new GameMaintenanceRestoreResult(
                        backupId,
                        GameMaintenanceRestoreStatus.PathSafetyViolation,
                        restoredCount,
                        $"Path safety violation for temp path: {fileEntry.RelativeGamePath}");
                }

                File.Copy(payloadFile, tempPath, overwrite: false);
                var tempHash = await GameMaintenanceScanner.ComputeSha256Async(tempPath, cancellationToken);
                if (!string.Equals(tempHash, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { if (File.Exists(tempPath) && GameMaintenanceScanner.IsSafelyContainedWithoutReparse(tempPath, normalizedGameRoot)) File.Delete(tempPath); } catch { }
                    return new GameMaintenanceRestoreResult(
                        backupId,
                        GameMaintenanceRestoreStatus.VerificationFailed,
                        restoredCount,
                        $"Restored temp file hash mismatch for: {fileEntry.RelativeGamePath}");
                }

                if (File.Exists(targetPath))
                {
                    try { if (File.Exists(tempPath) && GameMaintenanceScanner.IsSafelyContainedWithoutReparse(tempPath, normalizedGameRoot)) File.Delete(tempPath); } catch { }
                    return new GameMaintenanceRestoreResult(
                        backupId,
                        GameMaintenanceRestoreStatus.PreflightConflict,
                        restoredCount,
                        $"Target file appeared before move: {fileEntry.RelativeGamePath}");
                }

                // Final safety check prior to atomic move
                if (!GameMaintenanceScanner.IsSafelyContainedWithoutReparse(targetPath, normalizedGameRoot) ||
                    !GameMaintenanceScanner.IsSafelyContainedWithoutReparse(tempPath, normalizedGameRoot))
                {
                    try { if (File.Exists(tempPath) && GameMaintenanceScanner.IsSafelyContainedWithoutReparse(tempPath, normalizedGameRoot)) File.Delete(tempPath); } catch { }
                    return new GameMaintenanceRestoreResult(
                        backupId,
                        GameMaintenanceRestoreStatus.PathSafetyViolation,
                        restoredCount,
                        $"Path safety violation immediately prior to move: {fileEntry.RelativeGamePath}");
                }

                File.Move(tempPath, targetPath, overwrite: false);
                restoredCount++;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempPath) && GameMaintenanceScanner.IsSafelyContainedWithoutReparse(tempPath, normalizedGameRoot)) File.Delete(tempPath); } catch { }
                return new GameMaintenanceRestoreResult(
                    backupId,
                    GameMaintenanceRestoreStatus.RestoreFailed,
                    restoredCount,
                    $"Failed restoring file {fileEntry.RelativeGamePath}: {ex.Message}");
            }
        }

        return new GameMaintenanceRestoreResult(
            backupId,
            GameMaintenanceRestoreStatus.Success,
            restoredCount);
    }

    private static void CleanEmptyDirectories(IEnumerable<string> directories, string canonicalRoot)
    {
        var sorted = directories
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d.Length)
            .ToList();

        foreach (var dir in sorted)
        {
            var current = dir;
            while (!string.Equals(current, canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
                   current.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                   Directory.Exists(current))
            {
                try
                {
                    var attr = File.GetAttributes(current);
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                        break;

                    if (!Directory.EnumerateFileSystemEntries(current).Any())
                    {
                        Directory.Delete(current);
                        current = Path.GetDirectoryName(current)!;
                    }
                    else
                    {
                        break;
                    }
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
