using System.Data;
using Microsoft.Data.Sqlite;

namespace RipperWorks.Organizer;

internal static partial class OrganizerDatabaseSchema
{
    internal sealed record ColumnFingerprint(
        int Cid,
        string Name,
        string Type,
        bool IsNotNull,
        string? DefaultValue,
        int PkPosition);

    internal sealed record IndexFingerprint(
        string Name,
        bool IsUnique,
        string Origin,
        bool IsPartial,
        List<string> Columns);

    internal sealed record ForeignKeyFingerprint(
        int Id,
        int Seq,
        string FromColumn,
        string ToTable,
        string ToColumn,
        string OnUpdate,
        string OnDelete);

    internal sealed class TableFingerprint
    {
        public string TableName { get; }
        public string Sql { get; }
        public List<ColumnFingerprint> Columns { get; }
        public List<IndexFingerprint> Indexes { get; }
        public List<ForeignKeyFingerprint> ForeignKeys { get; }

        public TableFingerprint(
            string tableName,
            string sql,
            List<ColumnFingerprint> columns,
            List<IndexFingerprint> indexes,
            List<ForeignKeyFingerprint> foreignKeys)
        {
            TableName = tableName;
            Sql = sql;
            Columns = columns;
            Indexes = indexes;
            ForeignKeys = foreignKeys;
        }
    }

    internal static bool AreColumnsEqual(ColumnFingerprint actual, ColumnFingerprint expected)
    {
        if (actual.Cid != expected.Cid) return false;
        if (!string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(actual.Type, expected.Type, StringComparison.OrdinalIgnoreCase)) return false;
        if (actual.IsNotNull != expected.IsNotNull) return false;
        if (actual.PkPosition != expected.PkPosition) return false;

        var actualDef = NormalizeDefaultValue(actual.DefaultValue);
        var expectedDef = NormalizeDefaultValue(expected.DefaultValue);

        return string.Equals(actualDef, expectedDef, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? NormalizeDefaultValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim('(', ')', '\'', '"', ' ');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static async Task<TableFingerprint?> InspectTableFingerprintAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var cmdSql = connection.CreateCommand();
        cmdSql.Transaction = transaction;
        cmdSql.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name = $name;";
        cmdSql.Parameters.AddWithValue("$name", tableName);
        var sqlObj = await cmdSql.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (sqlObj is null || sqlObj == DBNull.Value)
            return null;

        var tableSql = sqlObj.ToString()!;

        // 1. PRAGMA table_info
        var columns = new List<ColumnFingerprint>();
        await using (var cmdInfo = connection.CreateCommand())
        {
            cmdInfo.Transaction = transaction;
            cmdInfo.CommandText = $"PRAGMA table_info('{tableName}');";
            await using var reader = await cmdInfo.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var cid = reader.GetInt32(0);
                var name = reader.GetString(1);
                var type = reader.GetString(2).ToUpperInvariant();
                var notNull = reader.GetInt32(3) != 0;
                var defVal = reader.IsDBNull(4) ? null : reader.GetString(4);
                var pkPosition = reader.GetInt32(5);
                columns.Add(new ColumnFingerprint(cid, name, type, notNull, defVal, pkPosition));
            }
        }

        // 2. PRAGMA index_list & index_info
        var indexes = new List<IndexFingerprint>();
        await using (var cmdIdxList = connection.CreateCommand())
        {
            cmdIdxList.Transaction = transaction;
            cmdIdxList.CommandText = $"PRAGMA index_list('{tableName}');";
            await using var reader = await cmdIdxList.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var indexMeta = new List<(string Name, bool IsUnique, string Origin, bool IsPartial)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var idxName = reader.GetString(1);
                var isUnique = reader.GetInt32(2) != 0;
                var origin = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var isPartial = reader.IsDBNull(4) ? false : reader.GetInt32(4) != 0;
                if (origin != "pk") // exclude PK internal index
                {
                    indexMeta.Add((idxName, isUnique, origin, isPartial));
                }
            }

            foreach (var meta in indexMeta)
            {
                await using var cmdIdxInfo = connection.CreateCommand();
                cmdIdxInfo.Transaction = transaction;
                cmdIdxInfo.CommandText = $"PRAGMA index_info('{meta.Name}');";
                await using var idxReader = await cmdIdxInfo.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var idxCols = new List<string>();
                while (await idxReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    idxCols.Add(idxReader.GetString(2));
                }
                indexes.Add(new IndexFingerprint(meta.Name, meta.IsUnique, meta.Origin, meta.IsPartial, idxCols));
            }
        }

        // 3. PRAGMA foreign_key_list
        var foreignKeys = new List<ForeignKeyFingerprint>();
        await using (var cmdFk = connection.CreateCommand())
        {
            cmdFk.Transaction = transaction;
            cmdFk.CommandText = $"PRAGMA foreign_key_list('{tableName}');";
            await using var reader = await cmdFk.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt32(0);
                var seq = reader.GetInt32(1);
                var toTable = reader.GetString(2);
                var fromCol = reader.GetString(3);
                var toCol = reader.GetString(4);
                var onUpdate = reader.GetString(5).ToUpperInvariant();
                var onDelete = reader.GetString(6).ToUpperInvariant();
                foreignKeys.Add(new ForeignKeyFingerprint(id, seq, fromCol, toTable, toCol, onUpdate, onDelete));
            }
        }

        return new TableFingerprint(tableName, tableSql, columns, indexes, foreignKeys);
    }
}
