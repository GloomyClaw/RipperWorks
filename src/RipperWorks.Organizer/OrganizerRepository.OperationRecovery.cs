using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class OrganizerRepository
{
    private const string DefaultInterruptedDiagnosticMessage =
        "The operation was still InProgress when RipperWorks started. " +
        "Automatic state recovery was not attempted; explicit recovery action is required.";

    public async Task<int> ClassifyInterruptedInstallOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            UPDATE InstallOperations
            SET Status = $recoveryRequired,
                ErrorMessage = CASE
                    WHEN ErrorMessage IS NULL OR TRIM(ErrorMessage) = ''
                        THEN $defaultMessage
                    ELSE ErrorMessage
                END
            WHERE Status = $inProgress
              AND CompletedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue(
            "$recoveryRequired",
            (int)InstallOperationStatus.RecoveryRequired);
        command.Parameters.AddWithValue(
            "$inProgress",
            (int)InstallOperationStatus.InProgress);
        command.Parameters.AddWithValue(
            "$defaultMessage",
            DefaultInterruptedDiagnosticMessage);

        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<bool> HasRecoveryRequiredOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ExecuteScalarLongAsync(
            connection,
            $"SELECT EXISTS(SELECT 1 FROM InstallOperations WHERE Status = {(int)InstallOperationStatus.RecoveryRequired});",
            cancellationToken) != 0;
    }
}
