using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RipperWorks.Downloader;

internal static class CatalogDatabaseMigrationSafety
{
    public static async Task CreateVerifiedBackupIfNeededAsync(
        SqliteConnection sourceConnection,
        string databasePath,
        long actualVersion,
        bool isUnversionedExisting,
        CancellationToken cancellationToken = default)
    {
        if (actualVersion >= CatalogDatabaseSchema.CurrentVersion && !isUnversionedExisting)
            return;

        await CheckIntegrityAsync(sourceConnection, cancellationToken).ConfigureAwait(false);

        var databaseDir = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("Catalog database directory path is invalid.");
        var backupDir = Path.Combine(databaseDir, "backups", "catalog");
        Directory.CreateDirectory(backupDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssff");
        var suffix = RandomNumberGenerator.GetHexString(4).ToLowerInvariant();
        var backupFileName = $"catalog-v{actualVersion}-before-v{CatalogDatabaseSchema.CurrentVersion}-{timestamp}-{suffix}.db";
        var finalBackupPath = Path.Combine(backupDir, backupFileName);
        var tempBackupPath = Path.Combine(backupDir, $"{backupFileName}.tmp");

        try
        {
            if (File.Exists(tempBackupPath))
                File.Delete(tempBackupPath);

            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = tempBackupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            await using (var destinationConnection = new SqliteConnection(destinationConnectionString))
            {
                await destinationConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                sourceConnection.BackupDatabase(destinationConnection);
            }

            await VerifyBackupFileAsync(tempBackupPath, actualVersion, cancellationToken).ConfigureAwait(false);
            File.Move(tempBackupPath, finalBackupPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempBackupPath))
            {
                try { File.Delete(tempBackupPath); } catch { }
            }
        }
    }

    public static async Task CheckIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var status = result is string s ? s : Convert.ToString(result);
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Catalog database integrity check failed: {status}");
        }
    }

    private static async Task VerifyBackupFileAsync(
        string backupPath,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await CheckIntegrityAsync(conn, cancellationToken).ConfigureAwait(false);

        await using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = "PRAGMA user_version;";
        var versionResult = await versionCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var backupVersion = Convert.ToInt64(versionResult);

        if (backupVersion != expectedVersion)
        {
            throw new InvalidDataException(
                $"Catalog backup version verification failed: expected {expectedVersion}, found {backupVersion}.");
        }
    }
}
