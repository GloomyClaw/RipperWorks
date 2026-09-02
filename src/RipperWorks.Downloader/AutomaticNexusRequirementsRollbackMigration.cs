using Microsoft.Data.Sqlite;

namespace RipperWorks.Downloader;

public sealed record AutomaticNexusRequirementsRollbackResult(
    int CatalogRequirementsRemoved,
    int CatalogSyncRowsRemoved);

public static class AutomaticNexusRequirementsRollbackMigration
{
    public static async Task<AutomaticNexusRequirementsRollbackResult>
        RemoveCatalogStorageAsync(
            string databasePath,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException(
                "Catalog database directory is missing."));
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var requirements = await CountIfPresentAsync(
            connection,
            "CatalogPackageRequirements",
            cancellationToken);
        var syncRows = await CountIfPresentAsync(
            connection,
            "CatalogRequirementSync",
            cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            DROP TABLE IF EXISTS CatalogPackageRequirements;
            DROP TABLE IF EXISTS CatalogRequirementSync;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(requirements, syncRows);
    }

    private static async Task<int> CountIfPresentAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = $table;
            """;
        exists.Parameters.AddWithValue("$table", table);
        if (Convert.ToInt32(
                await exists.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            return 0;
        }
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken));
    }
}
