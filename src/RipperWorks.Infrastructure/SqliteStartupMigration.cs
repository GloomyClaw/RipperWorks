using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RipperWorks.Infrastructure;

internal sealed record StartupMigrationInspection(
    int SchemaVersion,
    string InputFingerprint,
    int Inserts,
    int Updates,
    int Deletes,
    int Ambiguities,
    IReadOnlyList<string> Warnings,
    bool Compatible);

internal interface IStartupMigrationStep
{
    string Id { get; }

    Task<StartupMigrationInspection> InspectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int schemaVersion,
        CancellationToken cancellationToken);

    Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedUtc,
        CancellationToken cancellationToken);
}

internal static class SqliteStartupMigration
{
    public const string LedgerTable = "RipperWorksMigrationLedger";

    public static string ConnectionString(
        string databasePath,
        SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = mode,
            Pooling = false,
            Cache = SqliteCacheMode.Private
        }.ToString();

    public static async Task ConfigureAsync(
        SqliteConnection connection,
        bool readOnly,
        int busyTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA busy_timeout={busyTimeoutMilliseconds};" +
            (readOnly ? " PRAGMA query_only=ON;" : string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> UserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    public static async Task<string> QuickCheckAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return Convert.ToString(
                   await command.ExecuteScalarAsync(cancellationToken))
               ?? string.Empty;
    }

    public static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM sqlite_master
                WHERE type='table' AND name=$table);
            """;
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public static async Task<IReadOnlyDictionary<string, string>>
        ColumnsAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string table,
            CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"PRAGMA table_info({QuoteIdentifier(table)});";
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(1), reader.GetString(2));
        return columns;
    }

    public static async Task RequireColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyCollection<string> required,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(
                connection,
                transaction,
                table,
                cancellationToken))
        {
            throw new StartupMigrationBlockedException(
                $"Required {table} table is missing.");
        }
        var columns = await ColumnsAsync(
            connection,
            transaction,
            table,
            cancellationToken);
        var missing = required
            .Where(column => !columns.ContainsKey(column))
            .OrderBy(column => column, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new StartupMigrationBlockedException(
                $"{table} has an unsupported shape; missing " +
                string.Join(", ", missing) + ".");
        }
    }

    public static string Hash(IEnumerable<string> values)
    {
        var canonical = string.Join("\n", values);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) +
        "\"";
}
