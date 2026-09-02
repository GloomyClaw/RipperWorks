using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RipperWorks.Downloader;

internal static class DownloaderDatabaseMigrationSafety
{
    internal static async Task<string?> CreateVerifiedBackupIfNeededAsync(
        SqliteConnection connection,
        string databasePath,
        int actualVersion,
        int targetVersion,
        CancellationToken cancellationToken)
    {
        if (actualVersion < 0 || actualVersion >= targetVersion)
        {
            return null;
        }

        var databaseDir = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("Database directory is missing.");
        var backupDir = Path.Combine(databaseDir, "backups", "downloader");
        Directory.CreateDirectory(backupDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        var randomSuffix = RandomNumberGenerator.GetHexString(4).ToLowerInvariant();
        var finalFileName = $"downloader-v{actualVersion}-before-v{targetVersion}-{timestamp}-{randomSuffix}.db";
        var finalBackupPath = Path.Combine(backupDir, finalFileName);
        var tempBackupPath = Path.Combine(backupDir, $"{finalFileName}.tmp");

        try
        {
            var destConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = tempBackupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            await using (var destConnection = new SqliteConnection(destConnectionString))
            {
                await destConnection.OpenAsync(cancellationToken);
                connection.BackupDatabase(destConnection);
                await destConnection.CloseAsync();
            }

            var inspectConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = tempBackupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            await using (var inspectConnection = new SqliteConnection(inspectConnectionString))
            {
                await inspectConnection.OpenAsync(cancellationToken);
                await CheckIntegrityAsync(inspectConnection, "backup", cancellationToken);

                await using var versionCmd = inspectConnection.CreateCommand();
                versionCmd.CommandText = "PRAGMA user_version;";
                var backupVersion = Convert.ToInt32(
                    await versionCmd.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);

                if (backupVersion != actualVersion)
                {
                    throw new InvalidDataException(
                        $"Backup database user_version {backupVersion} does not match source version {actualVersion}.");
                }
            }

            File.Move(tempBackupPath, finalBackupPath, overwrite: false);
            return finalBackupPath;
        }
        catch
        {
            if (File.Exists(tempBackupPath))
            {
                try
                {
                    File.Delete(tempBackupPath);
                }
                catch
                {
                    // Best-effort cleanup of temporary backup artifact
                }
            }

            throw;
        }
    }

    internal static async Task CheckIntegrityAsync(
        SqliteConnection connection,
        string stageDescription,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var results = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    results.Add(reader.GetString(0));
                }
            }

            if (results.Count != 1 || !string.Equals(results[0], "ok", StringComparison.OrdinalIgnoreCase))
            {
                var summary = results.Count > 0 ? results[0] : "empty result";
                throw new InvalidDataException(
                    $"Downloader database {stageDescription} integrity check failed: {summary}");
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException(
                $"Downloader database {stageDescription} integrity check failed due to SQLite error.",
                ex);
        }
    }
}
