using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Schema create/open validation for journal v2 (FormatVersion=1).
/// Fail-closed: foreign/corrupt DBs never receive CREATE IF NOT EXISTS patching.
/// </summary>
internal static class SqliteGameOperationJournalSchema
{
    private static readonly string[] RequiredTables =
    [
        "JournalMeta",
        "Operations",
        "OperationSteps"
    ];

    public static async Task<IReadOnlyList<string>> ListUserTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            tables.Add(reader.GetString(0));
        return tables;
    }

    public static async Task ValidateV1SchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = await ListUserTablesAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var set = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);

        // Exact required tables (no missing V1 tables). Extra tables are not
        // auto-patched away, but required set must be present.
        foreach (var required in RequiredTables)
        {
            if (!set.Contains(required))
                throw new InvalidOperationException("CorruptedJournalSchema");
        }

        await AssertTableShapeAsync(
                connection,
                "JournalMeta",
                [
                    new ColumnSpec("Key", "TEXT", NotNull: true, Pk: true),
                    new ColumnSpec("Value", "TEXT", NotNull: true, Pk: false)
                ],
                expectedPkColumns: ["Key"],
                cancellationToken)
            .ConfigureAwait(false);

        await AssertTableShapeAsync(
                connection,
                "Operations",
                [
                    new ColumnSpec("OperationId", "TEXT", NotNull: true, Pk: true),
                    new ColumnSpec("CanonicalProfileKey", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("IdempotencyKey", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("OperationKind", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("PlanVersion", "INTEGER", NotNull: true, Pk: false),
                    new ColumnSpec("PlanHash", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("SerializedPlan", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("State", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("Phase", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("CreatedAtUtc", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("StartedAtUtc", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("CompletedAtUtc", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("CancellationRequested", "INTEGER", NotNull: true, Pk: false),
                    new ColumnSpec("ErrorCode", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("RedactedErrorDetail", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("LastSequence", "INTEGER", NotNull: true, Pk: false),
                    new ColumnSpec("JournalFormatVersion", "INTEGER", NotNull: true, Pk: false)
                ],
                expectedPkColumns: ["OperationId"],
                cancellationToken)
            .ConfigureAwait(false);

        await AssertTableShapeAsync(
                connection,
                "OperationSteps",
                [
                    new ColumnSpec("OperationId", "TEXT", NotNull: true, Pk: true),
                    new ColumnSpec("Sequence", "INTEGER", NotNull: true, Pk: true),
                    new ColumnSpec("StepKey", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("StepKind", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("State", "TEXT", NotNull: true, Pk: false),
                    new ColumnSpec("IntentRecordedAtUtc", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("AppliedAtUtc", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("VerifiedAtUtc", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("ExpectedBeforeIdentity", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("ExpectedAfterIdentity", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("ErrorCode", "TEXT", NotNull: false, Pk: false),
                    new ColumnSpec("RedactedErrorDetail", "TEXT", NotNull: false, Pk: false)
                ],
                expectedPkColumns: ["OperationId", "Sequence"],
                cancellationToken)
            .ConfigureAwait(false);

        await AssertUniqueIndexAsync(
                connection,
                "Operations",
                ["CanonicalProfileKey", "IdempotencyKey"],
                cancellationToken)
            .ConfigureAwait(false);

        await AssertForeignKeyAsync(
                connection,
                "OperationSteps",
                fromColumn: "OperationId",
                toTable: "Operations",
                toColumn: "OperationId",
                onDelete: "CASCADE",
                cancellationToken)
            .ConfigureAwait(false);

        // JournalMeta PRIMARY KEY + FormatVersion metadata must be present and
        // parseable as the known V1 format (open already gated on version==1).
        await AssertJournalMetaFormatVersionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        // sqlite_master CREATE statements must declare expected PK shapes
        // (catches hand-built tables that only coincidentally match PRAGMA).
        await AssertCreateSqlContainsAsync(
                connection,
                "JournalMeta",
                "PRIMARY KEY",
                cancellationToken)
            .ConfigureAwait(false);
        await AssertCreateSqlContainsAsync(
                connection,
                "Operations",
                "PRIMARY KEY",
                cancellationToken)
            .ConfigureAwait(false);
        await AssertCreateSqlContainsAsync(
                connection,
                "OperationSteps",
                "PRIMARY KEY",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task AssertJournalMetaFormatVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Value FROM JournalMeta WHERE Key = 'FormatVersion';";
        var existing = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is null or DBNull)
            throw new InvalidOperationException("CorruptedJournalSchema");
        var text = Convert.ToString(existing, CultureInfo.InvariantCulture);
        if (!int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var version) ||
            version != GameOperationJournalVersions.FormatVersion)
        {
            throw new InvalidOperationException("CorruptedJournalSchema");
        }
    }

    private static async Task AssertCreateSqlContainsAsync(
        SqliteConnection connection,
        string table,
        string requiredFragment,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sql FROM sqlite_master
            WHERE type = 'table' AND name = $name;
            """;
        command.Parameters.AddWithValue("$name", table);
        var sql = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(sql) ||
            sql.IndexOf(requiredFragment, StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("CorruptedJournalSchema");
        }
    }

    private readonly record struct ColumnSpec(
        string Name,
        string Type,
        bool NotNull,
        bool Pk);

    private static async Task AssertTableShapeAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyList<ColumnSpec> required,
        IReadOnlyList<string> expectedPkColumns,
        CancellationToken cancellationToken)
    {
        var byName = new Dictionary<string, (string Type, bool NotNull, int Pk)>(
            StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(" + table + ");";
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        // cid, name, type, notnull, dflt_value, pk
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var notNull = reader.GetInt32(3) != 0;
            var pk = reader.GetInt32(5);
            byName[name] = (type, notNull, pk);
        }

        foreach (var column in required)
        {
            if (!byName.TryGetValue(column.Name, out var actual))
                throw new InvalidOperationException("CorruptedJournalSchema");

            if (!string.Equals(
                    NormalizeSqliteType(actual.Type),
                    NormalizeSqliteType(column.Type),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("CorruptedJournalSchema");
            }

            if (actual.NotNull != column.NotNull)
                throw new InvalidOperationException("CorruptedJournalSchema");
        }

        var pkOrdered = byName
            .Where(kv => kv.Value.Pk > 0)
            .OrderBy(kv => kv.Value.Pk)
            .Select(kv => kv.Key)
            .ToList();
        if (pkOrdered.Count != expectedPkColumns.Count)
            throw new InvalidOperationException("CorruptedJournalSchema");
        for (var i = 0; i < expectedPkColumns.Count; i++)
        {
            if (!string.Equals(
                    pkOrdered[i],
                    expectedPkColumns[i],
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("CorruptedJournalSchema");
            }
        }
    }

    private static string NormalizeSqliteType(string type)
    {
        var t = (type ?? string.Empty).Trim().ToUpperInvariant();
        // SQLite affinity families we care about for V1.
        if (t.Contains("INT", StringComparison.Ordinal))
            return "INTEGER";
        if (t.Contains("CHAR", StringComparison.Ordinal) ||
            t.Contains("CLOB", StringComparison.Ordinal) ||
            t.Contains("TEXT", StringComparison.Ordinal))
        {
            return "TEXT";
        }

        return t;
    }

    private static async Task AssertUniqueIndexAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        await using var list = connection.CreateCommand();
        list.CommandText = "PRAGMA index_list(" + table + ");";
        await using var listReader =
            await list.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        // seq, name, unique, origin, partial
        var uniqueIndexes = new List<string>();
        while (await listReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var unique = listReader.GetInt32(2) != 0;
            if (!unique)
                continue;
            uniqueIndexes.Add(listReader.GetString(1));
        }

        foreach (var indexName in uniqueIndexes)
        {
            var indexColumns = new List<string>();
            await using var info = connection.CreateCommand();
            info.CommandText = "PRAGMA index_info(" + indexName + ");";
            await using var infoReader =
                await info.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            // seqno, cid, name
            while (await infoReader.ReadAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                if (!infoReader.IsDBNull(2))
                    indexColumns.Add(infoReader.GetString(2));
            }

            if (indexColumns.Count == columns.Count &&
                indexColumns
                    .Zip(columns, (a, b) =>
                        string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    .All(x => x))
            {
                return;
            }
        }

        throw new InvalidOperationException("CorruptedJournalSchema");
    }

    private static async Task AssertForeignKeyAsync(
        SqliteConnection connection,
        string table,
        string fromColumn,
        string toTable,
        string toColumn,
        string onDelete,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_list(" + table + ");";
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        // id, seq, table, from, to, on_update, on_delete, match
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var refTable = reader.GetString(2);
            var from = reader.GetString(3);
            var to = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            var deleteAction = reader.IsDBNull(6)
                ? string.Empty
                : reader.GetString(6);
            if (string.Equals(refTable, toTable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(from, fromColumn, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(to, toColumn, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    deleteAction,
                    onDelete,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidOperationException("CorruptedJournalSchema");
    }

    public static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS JournalMeta (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Operations (
                OperationId TEXT NOT NULL PRIMARY KEY,
                CanonicalProfileKey TEXT NOT NULL,
                IdempotencyKey TEXT NOT NULL,
                OperationKind TEXT NOT NULL,
                PlanVersion INTEGER NOT NULL,
                PlanHash TEXT NOT NULL,
                SerializedPlan TEXT NOT NULL,
                State TEXT NOT NULL,
                Phase TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                StartedAtUtc TEXT NULL,
                CompletedAtUtc TEXT NULL,
                CancellationRequested INTEGER NOT NULL DEFAULT 0,
                ErrorCode TEXT NULL,
                RedactedErrorDetail TEXT NULL,
                LastSequence INTEGER NOT NULL DEFAULT 0,
                JournalFormatVersion INTEGER NOT NULL,
                UNIQUE (CanonicalProfileKey, IdempotencyKey)
            );

            CREATE TABLE IF NOT EXISTS OperationSteps (
                OperationId TEXT NOT NULL,
                Sequence INTEGER NOT NULL,
                StepKey TEXT NOT NULL,
                StepKind TEXT NOT NULL,
                State TEXT NOT NULL,
                IntentRecordedAtUtc TEXT NULL,
                AppliedAtUtc TEXT NULL,
                VerifiedAtUtc TEXT NULL,
                ExpectedBeforeIdentity TEXT NULL,
                ExpectedAfterIdentity TEXT NULL,
                ErrorCode TEXT NULL,
                RedactedErrorDetail TEXT NULL,
                PRIMARY KEY (OperationId, Sequence),
                FOREIGN KEY (OperationId)
                    REFERENCES Operations(OperationId)
                    ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<int?> TryReadFormatVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var select = connection.CreateCommand();
            select.CommandText =
                "SELECT Value FROM JournalMeta WHERE Key = 'FormatVersion';";
            var existing = await select.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (existing is null or DBNull)
                return null;
            var text = Convert.ToString(existing, CultureInfo.InvariantCulture);
            if (!int.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var version))
            {
                throw new InvalidOperationException("UnsupportedJournalVersion");
            }

            return version;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public static async Task EnsureFormatVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int expected,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            "SELECT Value FROM JournalMeta WHERE Key = 'FormatVersion';";
        var existing = await select.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is null or DBNull)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO JournalMeta (Key, Value)
                VALUES ('FormatVersion', $v);
                """;
            insert.Parameters.AddWithValue(
                "$v",
                expected.ToString(CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var text = Convert.ToString(existing, CultureInfo.InvariantCulture);
        if (!int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var version) ||
            version != expected)
        {
            throw new InvalidOperationException("UnsupportedJournalVersion");
        }
    }
}
