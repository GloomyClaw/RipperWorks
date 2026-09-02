using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

internal sealed record VersionSwitchOperationRecord(
    InstallOperationRecord Operation,
    PackageId TargetPackageId,
    ArchiveVersionOperation VersionOperation);

internal static class VersionSwitchOperationType
{
    private const string Prefix = "VersionSwitch";

    public static string Format(
        PackageId targetPackageId,
        ArchiveVersionOperation operation) =>
        $"{Prefix}|{operation}|{Uri.EscapeDataString(targetPackageId.Value)}";

    public static bool TryParse(
        string value,
        out PackageId targetPackageId,
        out ArchiveVersionOperation operation)
    {
        targetPackageId = default;
        operation = ArchiveVersionOperation.Install;
        var parts = value.Split('|');
        if (parts.Length != 3 ||
            !string.Equals(parts[0], Prefix, StringComparison.Ordinal) ||
            !Enum.TryParse(parts[1], out operation) ||
            operation == ArchiveVersionOperation.Install)
        {
            return false;
        }

        var target = Uri.UnescapeDataString(parts[2]);
        if (string.IsNullOrWhiteSpace(target))
            return false;
        targetPackageId = new PackageId(target);
        return true;
    }
}

public sealed partial class OrganizerRepository
{
    internal async Task CreateVersionSwitchOperationAsync(
        Guid operationId,
        PackageId sourcePackageId,
        PackageId targetPackageId,
        ArchiveVersionOperation operation,
        CancellationToken cancellationToken = default)
    {
        if (operation == ArchiveVersionOperation.Install)
            throw new ArgumentException("Install is not a version switch.", nameof(operation));

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO InstallOperations (
                OperationId, PackageId, OperationType, StartedAtUtc,
                Status, CurrentPhase, AppliedFileCount)
            SELECT $operationId, $sourcePackageId, $operationType,
                   $startedAtUtc, $status, $phase, 0
            WHERE EXISTS (
                SELECT 1 FROM Packages WHERE PackageId = $targetPackageId);
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("N"));
        command.Parameters.AddWithValue("$sourcePackageId", sourcePackageId.Value);
        command.Parameters.AddWithValue("$targetPackageId", targetPackageId.Value);
        command.Parameters.AddWithValue(
            "$operationType",
            VersionSwitchOperationType.Format(targetPackageId, operation));
        command.Parameters.AddWithValue("$startedAtUtc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$status", (int)InstallOperationStatus.InProgress);
        command.Parameters.AddWithValue("$phase", (int)InstallOperationPhase.Created);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Version-switch target package is missing.");
    }

    internal async Task<VersionSwitchOperationRecord?>
        LoadVersionSwitchOperationAsync(
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
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var operationType = reader.GetString(2);
        if (!VersionSwitchOperationType.TryParse(
                operationType,
                out var targetPackageId,
                out var versionOperation))
        {
            return null;
        }

        return new(
            new InstallOperationRecord
            {
                OperationId = Guid.ParseExact(reader.GetString(0), "N"),
                PackageId = new PackageId(reader.GetString(1)),
                OperationType = operationType,
                StartedAtUtc = ParseUtc(reader.GetString(3)),
                CompletedAtUtc = reader.IsDBNull(4)
                    ? null
                    : ParseUtc(reader.GetString(4)),
                Status = (InstallOperationStatus)reader.GetInt32(5),
                CurrentPhase = (InstallOperationPhase)reader.GetInt32(6),
                ErrorMessage = GetNullableString(reader, 7),
                AppliedFileCount = reader.GetInt32(8)
            },
            targetPackageId,
            versionOperation);
    }

    internal async Task<VersionSwitchOperationRecord?>
        LoadLatestVersionSwitchOperationAsync(
            PackageId sourcePackageId,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT OperationId
            FROM InstallOperations
            WHERE PackageId = $sourcePackageId
              AND OperationType LIKE 'VersionSwitch|%'
            ORDER BY StartedAtUtc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sourcePackageId", sourcePackageId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string operationId
            ? await LoadVersionSwitchOperationAsync(
                Guid.ParseExact(operationId, "N"),
                cancellationToken)
            : null;
    }

    internal async Task MarkPackageRecoveryRequiredAsync(
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
            SET InstallationState = $state
            WHERE PackageId = $packageId;

            UPDATE InstalledMods
            SET InstallationState = $state
            WHERE PackageId = $packageId;
            """;
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        command.Parameters.AddWithValue(
            "$state",
            (int)PackageInstallationState.PartiallyInstalled);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
