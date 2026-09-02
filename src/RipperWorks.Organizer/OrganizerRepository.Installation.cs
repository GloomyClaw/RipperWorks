using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class OrganizerRepository : IInstalledFrameworkStateProvider
{
    public async Task CreateInstallOperationAsync(
        Guid operationId,
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO InstallOperations (
                OperationId, PackageId, OperationType, StartedAtUtc,
                Status, CurrentPhase, AppliedFileCount)
            VALUES (
                $operationId, $packageId, 'Install', $startedAtUtc,
                $status, $phase, 0);
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue("$startedAtUtc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue(
            "$status",
            (int)InstallOperationStatus.InProgress);
        command.Parameters.AddWithValue(
            "$phase",
            (int)InstallOperationPhase.Created);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateRemoveOperationAsync(
        Guid operationId,
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO InstallOperations (
                OperationId, PackageId, OperationType, StartedAtUtc,
                Status, CurrentPhase, AppliedFileCount)
            VALUES (
                $operationId, $packageId, 'Remove', $startedAtUtc,
                $status, $phase, 0);
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue("$startedAtUtc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue(
            "$status",
            (int)InstallOperationStatus.InProgress);
        command.Parameters.AddWithValue(
            "$phase",
            (int)InstallOperationPhase.Created);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateInstallOperationAsync(
        Guid operationId,
        InstallOperationPhase phase,
        InstallOperationStatus status = InstallOperationStatus.InProgress,
        string? errorMessage = null,
        bool completed = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE InstallOperations
            SET CurrentPhase = $phase,
                Status = $status,
                ErrorMessage = $errorMessage,
                CompletedAtUtc = CASE
                    WHEN $completed = 1 THEN $completedAtUtc
                    ELSE CompletedAtUtc
                END
            WHERE OperationId = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$phase", (int)phase);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$errorMessage", DbValue(errorMessage));
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("$completedAtUtc", FormatUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SavePlannedInstallManifestAsync(
        Guid operationId,
        OrganizerPackageRecord mod,
        IReadOnlyList<InstalledFileRecord> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(files);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var modCommand = connection.CreateCommand())
        {
            modCommand.Transaction = (SqliteTransaction)transaction;
            modCommand.CommandText =
                """
                INSERT INTO InstalledMods (
                    PackageId, ArchivePath, ArchiveFingerprint,
                    AnalysisFingerprint, SelectedRoot, InstalledAtUtc,
                    InstallationState)
                VALUES (
                    $packageId, $archivePath, $archiveFingerprint,
                    $analysisFingerprint, $selectedRoot, NULL, $state)
                ON CONFLICT(PackageId) DO UPDATE SET
                    ArchivePath = excluded.ArchivePath,
                    ArchiveFingerprint = excluded.ArchiveFingerprint,
                    AnalysisFingerprint = excluded.AnalysisFingerprint,
                    SelectedRoot = excluded.SelectedRoot,
                    InstalledAtUtc = NULL,
                    InstallationState = excluded.InstallationState;
                DELETE FROM InstalledFiles WHERE PackageId = $packageId;
                DELETE FROM InstallOperationFiles
                    WHERE OperationId = $operationId;
                """;
            modCommand.Parameters.AddWithValue(
                "$packageId",
                mod.Package.PackageId.Value);
            modCommand.Parameters.AddWithValue(
                "$archivePath",
                Path.GetFullPath(mod.Package.ArchivePath));
            modCommand.Parameters.AddWithValue(
                "$archiveFingerprint",
                BuildArchiveFingerprint(mod.Package));
            modCommand.Parameters.AddWithValue(
                "$analysisFingerprint",
                mod.Analysis?.Fingerprint ??
                throw new InvalidOperationException("Analysis is missing."));
            modCommand.Parameters.AddWithValue(
                "$selectedRoot",
                DbValue(mod.Analysis.SelectedRoot ?? mod.Analysis.DetectedRoot));
            modCommand.Parameters.AddWithValue(
                "$state",
                (int)PackageInstallationState.NotInstalled);
            modCommand.Parameters.AddWithValue(
                "$operationId",
                operationId.ToString("N"));
            await modCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var file in files.OrderBy(item => item.Sequence))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO InstalledFiles (
                    PackageId, RelativeGamePath, InstalledContentHash,
                    PreviousContentHash, PreviousFileExisted, Sequence)
                VALUES (
                    $packageId, $relativePath, $newHash,
                    $previousHash, $previousExisted, $sequence);

                INSERT INTO InstallOperationFiles (
                    OperationId, RelativeGamePath, NewContentHash,
                    PreviousContentHash, PreviousFileExisted, Sequence, Applied)
                VALUES (
                    $operationId, $relativePath, $newHash,
                    $previousHash, $previousExisted, $sequence, 0);
                """;
            command.Parameters.AddWithValue("$packageId", file.PackageId.Value);
            command.Parameters.AddWithValue("$relativePath", file.RelativeGamePath);
            command.Parameters.AddWithValue("$newHash", file.InstalledContentHash);
            command.Parameters.AddWithValue(
                "$previousHash",
                DbValue(file.PreviousContentHash));
            command.Parameters.AddWithValue(
                "$previousExisted",
                file.PreviousFileExisted ? 1 : 0);
            command.Parameters.AddWithValue("$sequence", file.Sequence);
            command.Parameters.AddWithValue(
                "$operationId",
                operationId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var phaseCommand = connection.CreateCommand())
        {
            phaseCommand.Transaction = (SqliteTransaction)transaction;
            phaseCommand.CommandText =
                """
                UPDATE InstallOperations
                SET CurrentPhase = $phase
                WHERE OperationId = $operationId;
                """;
            phaseCommand.Parameters.AddWithValue(
                "$operationId",
                operationId.ToString("N"));
            phaseCommand.Parameters.AddWithValue(
                "$phase",
                (int)InstallOperationPhase.ManifestPlanned);
            await phaseCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveRemovalOperationFilesAsync(
        Guid operationId,
        IReadOnlyList<InstalledFileRecord> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        foreach (var file in files.OrderBy(item => item.Sequence))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO InstallOperationFiles (
                    OperationId, RelativeGamePath, NewContentHash,
                    PreviousContentHash, PreviousFileExisted, Sequence, Applied)
                VALUES (
                    $operationId, $relativePath, $installedHash,
                    $previousHash, $previousExisted, $sequence, 0);
                """;
            command.Parameters.AddWithValue(
                "$operationId",
                operationId.ToString("N"));
            command.Parameters.AddWithValue(
                "$relativePath",
                file.RelativeGamePath);
            command.Parameters.AddWithValue(
                "$installedHash",
                file.InstalledContentHash);
            command.Parameters.AddWithValue(
                "$previousHash",
                DbValue(file.PreviousContentHash));
            command.Parameters.AddWithValue(
                "$previousExisted",
                file.PreviousFileExisted ? 1 : 0);
            command.Parameters.AddWithValue("$sequence", file.Sequence);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var phaseCommand = connection.CreateCommand();
        phaseCommand.Transaction = (SqliteTransaction)transaction;
        phaseCommand.CommandText =
            """
            UPDATE InstallOperations
            SET CurrentPhase = $phase
            WHERE OperationId = $operationId;
            """;
        phaseCommand.Parameters.AddWithValue(
            "$operationId",
            operationId.ToString("N"));
        phaseCommand.Parameters.AddWithValue(
            "$phase",
            (int)InstallOperationPhase.ManifestPlanned);
        await phaseCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkInstallOperationFileAppliedAsync(
        Guid operationId,
        int sequence,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            UPDATE InstallOperationFiles
            SET Applied = 1
            WHERE OperationId = $operationId AND Sequence = $sequence;

            UPDATE InstallOperations
            SET AppliedFileCount = (
                    SELECT COUNT(*)
                    FROM InstallOperationFiles
                    WHERE OperationId = $operationId AND Applied = 1),
                CurrentPhase = $phase
            WHERE OperationId = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue(
            "$phase",
            (int)InstallOperationPhase.Deploying);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteInstallAsync(
        Guid operationId,
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var now = FormatUtc(DateTime.UtcNow);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO ManagedPaths (
                RelativePath, BaseFileExisted, BaseContentHash)
            SELECT RelativeGamePath, PreviousFileExisted, PreviousContentHash
            FROM InstalledFiles
            WHERE PackageId = $packageId;

            INSERT INTO ManagedPathLayers (
                RelativePath, LayerOrder, PackageId, ContentHash,
                OperationId, InstalledAtUtc)
            SELECT f.RelativeGamePath,
                   COALESCE((
                       SELECT MAX(existing.LayerOrder) + 1
                       FROM ManagedPathLayers existing
                       WHERE existing.RelativePath =
                             f.RelativeGamePath COLLATE NOCASE
                   ), 1),
                   f.PackageId, f.InstalledContentHash,
                   $operationId, $now
            FROM InstalledFiles f
            WHERE f.PackageId = $packageId;

            UPDATE InstalledMods
            SET InstalledAtUtc = $now,
                InstallationState = $installationState
            WHERE PackageId = $packageId;

            UPDATE Packages
            SET InstallationState = $installationState
            WHERE PackageId = $packageId;

            UPDATE InstallOperations
            SET CompletedAtUtc = $now,
                Status = $status,
                CurrentPhase = $phase,
                ErrorMessage = NULL
            WHERE OperationId = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue(
            "$installationState",
            (int)PackageInstallationState.Installed);
        command.Parameters.AddWithValue(
            "$status",
            (int)InstallOperationStatus.Completed);
        command.Parameters.AddWithValue(
            "$phase",
            (int)InstallOperationPhase.Completed);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteInstallRollbackAsync(
        Guid operationId,
        PackageId packageId,
        bool recoveryRequired,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var state = recoveryRequired
            ? PackageInstallationState.PartiallyInstalled
            : PackageInstallationState.NotInstalled;
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            UPDATE Packages
            SET InstallationState = $installationState
            WHERE PackageId = $packageId;

            UPDATE InstalledMods
            SET InstallationState = $installationState
            WHERE PackageId = $packageId;

            UPDATE InstallOperations
            SET CompletedAtUtc = $completedAtUtc,
                Status = $status,
                CurrentPhase = $phase,
                ErrorMessage = $errorMessage
            WHERE OperationId = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue("$installationState", (int)state);
        command.Parameters.AddWithValue(
            "$status",
            (int)(recoveryRequired
                ? InstallOperationStatus.RecoveryRequired
                : InstallOperationStatus.RolledBack));
        command.Parameters.AddWithValue(
            "$phase",
            (int)InstallOperationPhase.RollingBack);
        command.Parameters.AddWithValue("$errorMessage", DbValue(errorMessage));
        command.Parameters.AddWithValue(
            "$completedAtUtc",
            FormatUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (!recoveryRequired)
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = (SqliteTransaction)transaction;
            cleanup.CommandText =
                """
                DELETE FROM InstalledFiles WHERE PackageId = $packageId;
                DELETE FROM InstalledMods WHERE PackageId = $packageId;
                """;
            cleanup.Parameters.AddWithValue("$packageId", packageId.Value);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteRemovalAsync(
        Guid operationId,
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE TEMP TABLE RemovedManagedLayers (
                RelativePath TEXT NOT NULL COLLATE NOCASE,
                LayerOrder INTEGER NOT NULL
            );

            INSERT INTO RemovedManagedLayers (RelativePath, LayerOrder)
            SELECT RelativePath, LayerOrder
            FROM ManagedPathLayers
            WHERE PackageId = $packageId;

            DELETE FROM ManagedPathLayers
            WHERE PackageId = $packageId;

            UPDATE ManagedPathLayers
            SET LayerOrder = LayerOrder + 1000000
            WHERE EXISTS (
                SELECT 1
                FROM RemovedManagedLayers removed
                WHERE removed.RelativePath =
                      ManagedPathLayers.RelativePath COLLATE NOCASE
                  AND removed.LayerOrder < ManagedPathLayers.LayerOrder
            );

            UPDATE ManagedPathLayers
            SET LayerOrder = LayerOrder - 1000001
            WHERE LayerOrder > 1000000
              AND EXISTS (
                  SELECT 1
                  FROM RemovedManagedLayers removed
                  WHERE removed.RelativePath =
                        ManagedPathLayers.RelativePath COLLATE NOCASE
              );

            DELETE FROM ManagedPaths
            WHERE NOT EXISTS (
                SELECT 1
                FROM ManagedPathLayers layer
                WHERE layer.RelativePath =
                      ManagedPaths.RelativePath COLLATE NOCASE
            );

            DROP TABLE RemovedManagedLayers;

            DELETE FROM InstalledFiles WHERE PackageId = $packageId;
            DELETE FROM InstalledMods WHERE PackageId = $packageId;

            UPDATE Packages
            SET InstallationState = $notInstalled
            WHERE PackageId = $packageId;

            UPDATE InstallOperations
            SET CompletedAtUtc = $completedAtUtc,
                Status = $status,
                CurrentPhase = $phase,
                ErrorMessage = NULL
            WHERE OperationId = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue(
            "$notInstalled",
            (int)PackageInstallationState.NotInstalled);
        command.Parameters.AddWithValue(
            "$status",
            (int)InstallOperationStatus.Completed);
        command.Parameters.AddWithValue(
            "$phase",
            (int)InstallOperationPhase.Completed);
        command.Parameters.AddWithValue(
            "$completedAtUtc",
            FormatUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteRemovalRollbackAsync(
        Guid operationId,
        PackageId packageId,
        bool recoveryRequired,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var state = recoveryRequired
            ? PackageInstallationState.PartiallyInstalled
            : PackageInstallationState.Installed;
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            UPDATE Packages
            SET InstallationState = $installationState
            WHERE PackageId = $packageId;

            UPDATE InstalledMods
            SET InstallationState = $installationState
            WHERE PackageId = $packageId;

            UPDATE InstallOperations
            SET CompletedAtUtc = $completedAtUtc,
                Status = $status,
                CurrentPhase = $phase,
                ErrorMessage = $errorMessage
            WHERE OperationId = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue("$installationState", (int)state);
        command.Parameters.AddWithValue(
            "$status",
            (int)(recoveryRequired
                ? InstallOperationStatus.RecoveryRequired
                : InstallOperationStatus.RolledBack));
        command.Parameters.AddWithValue(
            "$phase",
            (int)InstallOperationPhase.RollingBack);
        command.Parameters.AddWithValue("$errorMessage", DbValue(errorMessage));
        command.Parameters.AddWithValue(
            "$completedAtUtc",
            FormatUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InstallOperationFileRecord>>
        LoadInstallOperationFilesAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, OperationId, RelativeGamePath, NewContentHash,
                   PreviousContentHash, PreviousFileExisted, Sequence, Applied
            FROM InstallOperationFiles
            WHERE OperationId = $operationId
            ORDER BY Sequence DESC;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        var result = new List<InstallOperationFileRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new InstallOperationFileRecord
            {
                Id = reader.GetInt64(0),
                OperationId = Guid.ParseExact(reader.GetString(1), "N"),
                RelativeGamePath = reader.GetString(2),
                NewContentHash = reader.GetString(3),
                PreviousContentHash = GetNullableString(reader, 4),
                PreviousFileExisted = reader.GetInt32(5) != 0,
                Sequence = reader.GetInt32(6),
                Applied = reader.GetInt32(7) != 0
            });
        }
        return result;
    }

    public async Task<InstalledModRecord?> LoadInstalledModAsync(
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT PackageId, ArchivePath, ArchiveFingerprint,
                   AnalysisFingerprint, SelectedRoot, InstalledAtUtc,
                   InstallationState
            FROM InstalledMods
            WHERE PackageId = $packageId;
            """;
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new InstalledModRecord
        {
            PackageId = new PackageId(reader.GetString(0)),
            ArchivePath = reader.GetString(1),
            ArchiveFingerprint = reader.GetString(2),
            AnalysisFingerprint = reader.GetString(3),
            SelectedRoot = GetNullableString(reader, 4),
            InstalledAtUtc = reader.IsDBNull(5)
                ? null
                : ParseUtc(reader.GetString(5)),
            InstallationState =
                (PackageInstallationState)reader.GetInt32(6)
        };
    }

    public async Task<IReadOnlyList<InstalledFileRecord>>
        LoadInstalledFilesAsync(
            PackageId packageId,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT PackageId, RelativeGamePath, InstalledContentHash,
                   PreviousContentHash, PreviousFileExisted, Sequence
            FROM InstalledFiles
            WHERE PackageId = $packageId
            ORDER BY Sequence;
            """;
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        var result = new List<InstalledFileRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new InstalledFileRecord
            {
                PackageId = new PackageId(reader.GetString(0)),
                RelativeGamePath = reader.GetString(1),
                InstalledContentHash = reader.GetString(2),
                PreviousContentHash = GetNullableString(reader, 3),
                PreviousFileExisted = reader.GetInt32(4) != 0,
                Sequence = reader.GetInt32(5)
            });
        }
        return result;
    }

    public async Task<InstallOperationRecord?> LoadLatestInstallOperationAsync(
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT OperationId, PackageId, OperationType, StartedAtUtc,
                   CompletedAtUtc, Status, CurrentPhase, ErrorMessage,
                   AppliedFileCount
            FROM InstallOperations
            WHERE PackageId = $packageId
            ORDER BY StartedAtUtc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new InstallOperationRecord
        {
            OperationId = Guid.ParseExact(reader.GetString(0), "N"),
            PackageId = new PackageId(reader.GetString(1)),
            OperationType = reader.GetString(2),
            StartedAtUtc = ParseUtc(reader.GetString(3)),
            CompletedAtUtc = reader.IsDBNull(4)
                ? null
                : ParseUtc(reader.GetString(4)),
            Status = (InstallOperationStatus)reader.GetInt32(5),
            CurrentPhase = (InstallOperationPhase)reader.GetInt32(6),
            ErrorMessage = GetNullableString(reader, 7),
            AppliedFileCount = reader.GetInt32(8)
        };
    }

    public async Task<InstalledPathOwner?> FindInstalledPathOwnerAsync(
        string relativeGamePath,
        PackageId requestingPackageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT l.PackageId,
                   COALESCE(NULLIF(p.CustomDisplayName, ''),
                            p.IndexedDisplayName),
                   EXISTS(
                       SELECT 1
                       FROM LibraryModRelations r
                       JOIN Packages requesting
                         ON requesting.PackageId =
                            $requestingPackageId
                       JOIN Packages owning
                         ON owning.PackageId = l.PackageId
                       WHERE r.FromLibraryModId =
                             requesting.LibraryModId
                         AND r.ToLibraryModId =
                             owning.LibraryModId
                         AND r.RelationType = $addOnOf
                         AND r.IsConfirmed = 1)
            FROM ManagedPathLayers l
            JOIN Packages p ON p.PackageId = l.PackageId
            WHERE l.RelativePath = $relativePath COLLATE NOCASE
            ORDER BY l.LayerOrder DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$relativePath", relativeGamePath);
        command.Parameters.AddWithValue(
            "$requestingPackageId",
            requestingPackageId.Value);
        command.Parameters.AddWithValue(
            "$addOnOf",
            (int)PackageRelationType.AddOnOf);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new InstalledPathOwner(
            new PackageId(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2) != 0);
    }

    public async Task<bool> HasRecoveryRequiredAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ExecuteScalarLongAsync(
            connection,
            $"SELECT EXISTS(SELECT 1 FROM InstallOperations WHERE Status = {(int)InstallOperationStatus.RecoveryRequired});",
            cancellationToken) != 0;
    }

    public async Task<InstalledFrameworkState> GetInstalledFrameworkStateAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var pathCommand = connection.CreateCommand();
        pathCommand.CommandText =
            """
            SELECT DISTINCT f.RelativeGamePath
            FROM InstalledFiles f
            INNER JOIN InstalledMods m ON m.PackageId = f.PackageId
            WHERE m.InstallationState = 1;
            """;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await pathCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    var normalized = reader.GetString(0).Replace('\\', '/').TrimStart('/');
                    paths.Add(normalized);
                }
            }
        }

        await using var identityCommand = connection.CreateCommand();
        identityCommand.CommandText =
            """
            SELECT DISTINCT p.GameDomain, p.NexusModId
            FROM Packages p
            INNER JOIN InstalledMods m ON m.PackageId = p.PackageId
            WHERE m.InstallationState = 1 AND p.NexusModId IS NOT NULL AND p.GameDomain IS NOT NULL AND TRIM(p.GameDomain) != '';
            """;
        var identities = new HashSet<InstalledNexusModIdentity>();
        await using (var reader = await identityCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    var domain = reader.GetString(0).Trim();
                    if (!string.IsNullOrEmpty(domain))
                    {
                        var modId = reader.GetInt64(1);
                        identities.Add(new InstalledNexusModIdentity(domain, modId));
                    }
                }
            }
        }

        return new InstalledFrameworkState(paths, identities);
    }

    public async Task<bool> HasManagedInstallationsAsync(
        string gameRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return false;
        var profile = await LoadGameProfileAsync(cancellationToken);
        if (profile is null || string.IsNullOrWhiteSpace(profile.GameRoot)) return false;

        var normalizedRequested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        var normalizedProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.GameRoot));
        if (!string.Equals(normalizedRequested, normalizedProfile, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await using var connection = await OpenAsync(cancellationToken);
        return await ExecuteScalarLongAsync(
            connection,
            "SELECT EXISTS(SELECT 1 FROM InstalledMods WHERE InstallationState IN (1, 2));",
            cancellationToken) != 0;
    }

    private static string BuildArchiveFingerprint(PackageRecord package) =>
        $"{package.FileSize}:{package.LastWriteUtc.ToUniversalTime():O}";
}
