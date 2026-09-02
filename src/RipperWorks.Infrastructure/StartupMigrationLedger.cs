using Microsoft.Data.Sqlite;

namespace RipperWorks.Infrastructure;

internal sealed record StartupMigrationLedgerEntry(
    string MigrationId,
    string AppliedUtc,
    string CodeVersion,
    string InputFingerprint,
    string ReportHash);

internal static class StartupMigrationLedger
{
    private static readonly string[] ExpectedColumns =
    [
        "MigrationId",
        "AppliedUtc",
        "CodeVersion",
        "InputFingerprint",
        "ReportHash"
    ];

    public static async Task<IReadOnlyDictionary<string,
        StartupMigrationLedgerEntry>> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (!await SqliteStartupMigration.TableExistsAsync(
                connection,
                transaction,
                SqliteStartupMigration.LedgerTable,
                cancellationToken))
        {
            return new Dictionary<string, StartupMigrationLedgerEntry>(
                StringComparer.Ordinal);
        }
        await ValidateSchemaAsync(
            connection,
            transaction,
            cancellationToken);
        var result = new Dictionary<string, StartupMigrationLedgerEntry>(
            StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT MigrationId, AppliedUtc, CodeVersion,
                    InputFingerprint, ReportHash
             FROM {SqliteStartupMigration.LedgerTable}
             ORDER BY MigrationId;
             """;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entry = new StartupMigrationLedgerEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4));
            result.Add(entry.MigrationId, entry);
        }
        return result;
    }

    public static async Task EnsureCreatedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             CREATE TABLE IF NOT EXISTS
                 {SqliteStartupMigration.LedgerTable} (
                 MigrationId TEXT PRIMARY KEY,
                 AppliedUtc TEXT NOT NULL,
                 CodeVersion TEXT NOT NULL,
                 InputFingerprint TEXT NOT NULL,
                 ReportHash TEXT NOT NULL
             );
             """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await ValidateSchemaAsync(
            connection,
            transaction,
            cancellationToken);
    }

    public static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StartupMigrationDryRun report,
        DateTimeOffset appliedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             INSERT INTO {SqliteStartupMigration.LedgerTable} (
                 MigrationId, AppliedUtc, CodeVersion,
                 InputFingerprint, ReportHash)
             VALUES (
                 $id, $applied, $code, $input, $report);
             """;
        command.Parameters.AddWithValue("$id", report.MigrationId);
        command.Parameters.AddWithValue(
            "$applied",
            appliedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$code", report.CodeVersion);
        command.Parameters.AddWithValue(
            "$input",
            report.InputFingerprint);
        command.Parameters.AddWithValue("$report", report.ReportHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var columns = await SqliteStartupMigration.ColumnsAsync(
            connection,
            transaction,
            SqliteStartupMigration.LedgerTable,
            cancellationToken);
        if (columns.Count != ExpectedColumns.Length ||
            ExpectedColumns.Any(column => !columns.ContainsKey(column)))
        {
            throw new StartupMigrationBlockedException(
                "Migration ledger has an unsupported schema.");
        }
    }
}
