using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed record RecoveryRepositoryEvidence(
    InstallOperationRecord? Operation,
    GameProfileRecord? Profile,
    bool PackageExists,
    IReadOnlyList<InstallOperationFileRecord> OperationFiles,
    InstalledModRecord? InstalledMod,
    IReadOnlyList<InstalledFileRecord> InstalledFiles,
    IReadOnlyList<ManagedPathLayerRecord> PackageLayers,
    IReadOnlyList<PackageRelationView> Relations,
    IReadOnlyList<InstallOperationRecord> LaterOperations);

public sealed partial class OrganizerRepository
{
    public async Task<IReadOnlyList<Guid>>
        LoadRecoveryRequiredOperationIdsAsync(
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT OperationId
            FROM InstallOperations
            WHERE Status = $status
            ORDER BY StartedAtUtc, OperationId;
            """;
        command.Parameters.AddWithValue(
            "$status", (int)InstallOperationStatus.RecoveryRequired);
        var values = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            values.Add(Guid.ParseExact(reader.GetString(0), "N"));
        return values;
    }

    internal async Task<RecoveryRepositoryEvidence> LoadRecoveryEvidenceAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await LoadRecoveryOperationAsync(
            operationId, cancellationToken);
        if (operation is null)
        {
            return new(null, await LoadGameProfileAsync(cancellationToken),
                false, [], null, [], [], [], []);
        }

        var packages = await LoadPackagesAsync(false, cancellationToken);
        var packageExists = packages.Any(value =>
            value.Package.PackageId == operation.PackageId);
        return new(
            operation,
            await LoadGameProfileAsync(cancellationToken),
            packageExists,
            await LoadInstallOperationFilesAsync(operationId, cancellationToken),
            await LoadInstalledModAsync(operation.PackageId, cancellationToken),
            await LoadInstalledFilesAsync(operation.PackageId, cancellationToken),
            await LoadManagedPathLayersForPackageAsync(
                operation.PackageId, cancellationToken),
            await LoadPackageRelationsAsync(
                operation.PackageId, cancellationToken),
            await LoadLaterOperationsAsync(operation, cancellationToken));
    }

    internal async Task<InstallOperationRecord?> LoadRecoveryOperationAsync(
        Guid operationId,
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
            WHERE OperationId = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadRecoveryOperation(reader)
            : null;
    }

    internal async Task<IReadOnlyList<InstallOperationRecord>>
        LoadRecoveryOperationsSinceAsync(
            IReadOnlyList<PackageId> packageIds,
            DateTime startedAtUtc,
            Guid excludedOperationId,
            CancellationToken cancellationToken = default)
    {
        var values = new List<InstallOperationRecord>();
        foreach (var packageId in packageIds.Distinct())
        {
            var anchor = new InstallOperationRecord
            {
                OperationId = excludedOperationId,
                PackageId = packageId,
                OperationType = string.Empty,
                StartedAtUtc = startedAtUtc
            };
            values.AddRange(await LoadLaterOperationsAsync(
                anchor, cancellationToken));
        }
        return values.OrderBy(value => value.StartedAtUtc)
            .ThenBy(value => value.OperationId).ToArray();
    }

    internal async Task<InstallOperationRecord?>
        LoadLatestCompletedOperationBeforeAsync(
            PackageId packageId,
            string operationType,
            DateTime beforeUtc,
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
              AND OperationType = $operationType
              AND Status = $completed
              AND StartedAtUtc < $before
            ORDER BY StartedAtUtc DESC, OperationId DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue("$operationType", operationType);
        command.Parameters.AddWithValue(
            "$completed", (int)InstallOperationStatus.Completed);
        command.Parameters.AddWithValue("$before", FormatUtc(beforeUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadRecoveryOperation(reader)
            : null;
    }

    internal async Task RestoreVersionSwitchSourceLayersAsync(
        PackageId packageId,
        IReadOnlyList<RecoverySourceLayerPlan> plans,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        foreach (var plan in plans)
        {
            var layers = await LoadLayersInTransactionAsync(
                connection, (SqliteTransaction)transaction,
                plan.RelativeGamePath, cancellationToken);
            var sourceLayers = layers.Where(value =>
                value.PackageId == packageId).ToArray();
            if (sourceLayers.Length != 1 ||
                sourceLayers[0].ContentHash != plan.SourceContentHash)
                throw new InvalidOperationException("Source layer evidence changed.");
            var others = layers.Where(value => value.PackageId != packageId)
                .OrderBy(value => value.LayerOrder).ToList();
            if (plan.InsertionOrder < 0 || plan.InsertionOrder > others.Count)
                throw new InvalidOperationException("Source layer order changed.");
            others.Insert(plan.InsertionOrder, sourceLayers[0]);
            await ReplaceLayersInTransactionAsync(
                connection, (SqliteTransaction)transaction,
                plan.RelativeGamePath, others, cancellationToken);
            await using var installedFile = connection.CreateCommand();
            installedFile.Transaction = (SqliteTransaction)transaction;
            installedFile.CommandText =
                """
                UPDATE InstalledFiles
                SET PreviousContentHash = $previousHash,
                    PreviousFileExisted = $previousExisted
                WHERE PackageId = $packageId
                  AND RelativeGamePath = $path COLLATE NOCASE;
                """;
            installedFile.Parameters.AddWithValue("$packageId", packageId.Value);
            installedFile.Parameters.AddWithValue("$path", plan.RelativeGamePath);
            installedFile.Parameters.AddWithValue(
                "$previousHash", DbValue(plan.PreviousContentHash));
            installedFile.Parameters.AddWithValue(
                "$previousExisted", plan.PreviousFileExisted ? 1 : 0);
            if (await installedFile.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Installed file evidence changed.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task TerminalizeInstallRecoveryAsync(
        Guid operationId,
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = (SqliteTransaction)transaction;
            guard.CommandText =
                """
                UPDATE InstallOperations
                SET ErrorMessage = ErrorMessage
                WHERE OperationId = $operationId
                  AND Status = $recoveryRequired;
                """;
            guard.Parameters.AddWithValue(
                "$operationId", operationId.ToString("N"));
            guard.Parameters.AddWithValue(
                "$recoveryRequired",
                (int)InstallOperationStatus.RecoveryRequired);
            if (await guard.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Recovery evidence changed.");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            DELETE FROM InstalledFiles WHERE PackageId = $packageId;
            DELETE FROM InstalledMods WHERE PackageId = $packageId;
            UPDATE Packages
            SET InstallationState = $notInstalled
            WHERE PackageId = $packageId;
            UPDATE InstallOperations
            SET Status = $rolledBack,
                CurrentPhase = $rollingBack,
                CompletedAtUtc = $completedAt,
                ErrorMessage = 'RecoveredToPreOperationState'
            WHERE OperationId = $operationId
              AND Status = $recoveryRequired;
            """;
        AddTerminalParameters(command, operationId, packageId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task TerminalizeRecoveryOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE InstallOperations
            SET Status = $rolledBack,
                CurrentPhase = $rollingBack,
                CompletedAtUtc = $completedAt,
                ErrorMessage = 'RecoveredToPreOperationState'
            WHERE OperationId = $operationId
              AND Status = $recoveryRequired;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue(
            "$rolledBack", (int)InstallOperationStatus.RolledBack);
        command.Parameters.AddWithValue(
            "$rollingBack", (int)InstallOperationPhase.RollingBack);
        command.Parameters.AddWithValue(
            "$recoveryRequired", (int)InstallOperationStatus.RecoveryRequired);
        command.Parameters.AddWithValue("$completedAt", FormatUtc(DateTime.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Recovery evidence changed.");
    }

    internal async Task TerminalizeRemovalRecoveryAsync(
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
            UPDATE Packages
            SET InstallationState = $installed
            WHERE PackageId = $packageId;
            UPDATE InstalledMods
            SET InstallationState = $installed
            WHERE PackageId = $packageId;
            UPDATE InstallOperations
            SET Status = $rolledBack,
                CurrentPhase = $rollingBack,
                CompletedAtUtc = $completedAt,
                ErrorMessage = 'RecoveredToPreOperationState'
            WHERE OperationId = $operationId
              AND Status = $recoveryRequired;
            """;
        command.Parameters.AddWithValue(
            "$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue(
            "$installed", (int)PackageInstallationState.Installed);
        command.Parameters.AddWithValue(
            "$rolledBack", (int)InstallOperationStatus.RolledBack);
        command.Parameters.AddWithValue(
            "$rollingBack", (int)InstallOperationPhase.RollingBack);
        command.Parameters.AddWithValue(
            "$recoveryRequired",
            (int)InstallOperationStatus.RecoveryRequired);
        command.Parameters.AddWithValue(
            "$completedAt", FormatUtc(DateTime.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken) < 3)
            throw new InvalidOperationException("Recovery evidence changed.");
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<InstallOperationRecord>>
        LoadLaterOperationsAsync(
            InstallOperationRecord operation,
            CancellationToken cancellationToken)
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
              AND StartedAtUtc >= $startedAt
              AND OperationId <> $operationId
            ORDER BY StartedAtUtc, OperationId;
            """;
        command.Parameters.AddWithValue("$packageId", operation.PackageId.Value);
        command.Parameters.AddWithValue("$startedAt", FormatUtc(operation.StartedAtUtc));
        command.Parameters.AddWithValue(
            "$operationId", operation.OperationId.ToString("N"));
        var values = new List<InstallOperationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            values.Add(ReadRecoveryOperation(reader));
        return values;
    }

    private static InstallOperationRecord ReadRecoveryOperation(
        SqliteDataReader reader) => new()
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

    private static void AddTerminalParameters(
        SqliteCommand command,
        Guid operationId,
        PackageId packageId)
    {
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue(
            "$notInstalled", (int)PackageInstallationState.NotInstalled);
        command.Parameters.AddWithValue(
            "$rolledBack", (int)InstallOperationStatus.RolledBack);
        command.Parameters.AddWithValue(
            "$rollingBack", (int)InstallOperationPhase.RollingBack);
        command.Parameters.AddWithValue(
            "$recoveryRequired", (int)InstallOperationStatus.RecoveryRequired);
        command.Parameters.AddWithValue("$completedAt", FormatUtc(DateTime.UtcNow));
    }

    private static async Task<List<ManagedPathLayerRecord>>
        LoadLayersInTransactionAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string relativePath,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT RelativePath, LayerOrder, PackageId, ContentHash,
                   OperationId, InstalledAtUtc
            FROM ManagedPathLayers
            WHERE RelativePath = $path COLLATE NOCASE
            ORDER BY LayerOrder;
            """;
        command.Parameters.AddWithValue("$path", relativePath);
        var values = new List<ManagedPathLayerRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new ManagedPathLayerRecord
            {
                RelativePath = reader.GetString(0),
                LayerOrder = reader.GetInt32(1),
                PackageId = new PackageId(reader.GetString(2)),
                ContentHash = reader.GetString(3),
                OperationId = reader.GetString(4),
                InstalledAtUtc = ParseUtc(reader.GetString(5))
            });
        }
        return values;
    }

    private static async Task ReplaceLayersInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string relativePath,
        IReadOnlyList<ManagedPathLayerRecord> layers,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM ManagedPathLayers WHERE RelativePath = $path COLLATE NOCASE;";
            delete.Parameters.AddWithValue("$path", relativePath);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var order = 0; order < layers.Count; order++)
        {
            var layer = layers[order];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO ManagedPathLayers (
                    RelativePath, LayerOrder, PackageId, ContentHash,
                    OperationId, InstalledAtUtc)
                VALUES ($path, $order, $packageId, $hash, $operationId, $installed);
                """;
            insert.Parameters.AddWithValue("$path", relativePath);
            insert.Parameters.AddWithValue("$order", order);
            insert.Parameters.AddWithValue("$packageId", layer.PackageId.Value);
            insert.Parameters.AddWithValue("$hash", layer.ContentHash);
            insert.Parameters.AddWithValue("$operationId", layer.OperationId);
            insert.Parameters.AddWithValue("$installed", FormatUtc(layer.InstalledAtUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
