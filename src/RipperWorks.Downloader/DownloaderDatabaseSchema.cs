using Microsoft.Data.Sqlite;

namespace RipperWorks.Downloader;

internal static class DownloaderDatabaseSchema
{
    internal const int CurrentVersion = 6;

    internal static async Task InitializeAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        int userVersion;
        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = "PRAGMA user_version;";
            userVersion = Convert.ToInt32(
                await checkCommand.ExecuteScalarAsync(cancellationToken));
        }

        if (userVersion > CurrentVersion)
        {
            throw new NotSupportedException(
                $"Downloader database schema {userVersion} is newer than supported schema {CurrentVersion}.");
        }

        await DownloaderDatabaseMigrationSafety.CheckIntegrityAsync(
            connection,
            "pre-migration",
            cancellationToken);

        bool hasUserTables;
        await using (var tablesCommand = connection.CreateCommand())
        {
            tablesCommand.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            hasUserTables = Convert.ToInt32(
                await tablesCommand.ExecuteScalarAsync(cancellationToken)) > 0;
        }

        var isExistingOlderDatabase = userVersion < CurrentVersion && (userVersion > 0 || hasUserTables);

        if (isExistingOlderDatabase)
        {
            await DownloaderDatabaseMigrationSafety.CreateVerifiedBackupIfNeededAsync(
                connection,
                databasePath,
                userVersion,
                CurrentVersion,
                cancellationToken);
        }

        await using (var modeCmd = connection.CreateCommand())
        {
            modeCmd.CommandText =
                """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=OFF;
                """;
            await modeCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await ExecuteSchemaMutationsAsync(connection, transaction, cancellationToken);

                await using var setVersionCmd = connection.CreateCommand();
                setVersionCmd.Transaction = transaction;
                setVersionCmd.CommandText = $"PRAGMA user_version={CurrentVersion};";
                await setVersionCmd.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // Best-effort rollback using CancellationToken.None to preserve original exception
                }
                throw;
            }
        }

        await using (var fkCmd = connection.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_keys=ON;";
            await fkCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await DownloaderDatabaseMigrationSafety.CheckIntegrityAsync(
            connection,
            "post-migration",
            cancellationToken);
    }

    private static async Task ExecuteSchemaMutationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var createEntriesCmd = connection.CreateCommand())
        {
            createEntriesCmd.Transaction = transaction;
            createEntriesCmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS DownloaderEntries (
                    EntryId TEXT PRIMARY KEY,
                    Number INTEGER NOT NULL,
                    IsSelected INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    Source INTEGER NOT NULL,
                    Author TEXT NOT NULL,
                    Url TEXT NOT NULL,
                    GameDomain TEXT NOT NULL,
                    NexusModId INTEGER NULL,
                    NexusFileId INTEGER NULL,
                    NexusFileUuid TEXT NOT NULL DEFAULT '',
                    Version TEXT NOT NULL,
                    Status INTEGER NOT NULL,
                    Progress REAL NOT NULL,
                    BytesPerSecond REAL NOT NULL,
                    Size INTEGER NOT NULL,
                    LocalArchivePath TEXT NOT NULL,
                    TemporaryPath TEXT NOT NULL,
                    Error TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    UpdatedAt TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    AdditionalUrl TEXT NOT NULL DEFAULT '',
                    DownloadOrder INTEGER NULL,
                    CatalogId TEXT NOT NULL DEFAULT '',
                    DecisionGroup TEXT NOT NULL DEFAULT '',
                    Sha256 TEXT NOT NULL DEFAULT '',
                    ArchiveFileName TEXT NOT NULL DEFAULT '',
                    UpdateCheckStatus INTEGER NOT NULL DEFAULT 0,
                    AvailableVersion TEXT NOT NULL DEFAULT '',
                    AvailableFileId INTEGER NULL,
                    AvailableFileUuid TEXT NOT NULL DEFAULT '',
                    LastUpdateCheckUtc TEXT NULL,
                    UpdateCheckMessage TEXT NOT NULL DEFAULT ''
                );
                """;
            await createEntriesCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureColumnAsync(
            connection,
            transaction,
            "NexusModId",
            "INTEGER NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "NexusFileId",
            "INTEGER NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "UpdatedAt",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "NexusFileUuid",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "AdditionalUrl",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "DownloadOrder",
            "INTEGER NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "CatalogId",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "DecisionGroup",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "Sha256",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "ArchiveFileName",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "UpdateCheckStatus",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "AvailableVersion",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "AvailableFileId",
            "INTEGER NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "AvailableFileUuid",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "LastUpdateCheckUtc",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            transaction,
            "UpdateCheckMessage",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);

        await RemoveLegacyRequirementsStorageAsync(
            connection,
            transaction,
            cancellationToken);

        await using var finalSchema = connection.CreateCommand();
        finalSchema.Transaction = transaction;
        finalSchema.CommandText =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS
                UX_DownloaderEntries_NexusFile
                ON DownloaderEntries(GameDomain, NexusModId, NexusFileId)
                WHERE NexusModId IS NOT NULL AND NexusFileId IS NOT NULL;
            CREATE TABLE IF NOT EXISTS DownloaderDownloads (
                DownloadId TEXT PRIMARY KEY,
                EntryId TEXT NOT NULL UNIQUE,
                SourceUrl TEXT NOT NULL,
                TemporaryPath TEXT NOT NULL,
                BytesDownloaded INTEGER NOT NULL,
                TotalBytes INTEGER NULL,
                Status INTEGER NOT NULL,
                Error TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY (EntryId)
                    REFERENCES DownloaderEntries(EntryId)
                    ON DELETE CASCADE
            );
            """;
        await finalSchema.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RemoveLegacyRequirementsStorageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.Transaction = transaction;
            inspect.CommandText = "PRAGMA table_info(DownloaderEntries);";
            await using var reader =
                await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        await using (var dropDependencies = connection.CreateCommand())
        {
            dropDependencies.Transaction = transaction;
            dropDependencies.CommandText =
                "DROP TABLE IF EXISTS UnresolvedNexusDependencies;";
            await dropDependencies.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columns.Contains("RequiredNexusModIds") &&
            !columns.Contains("PendingNexusPageRequirements") &&
            !columns.Contains("NexusPageRequirementsCaptured"))
        {
            return;
        }

        await using var rebuild = connection.CreateCommand();
        rebuild.Transaction = transaction;
        rebuild.CommandText =
            """
            DROP TABLE IF EXISTS DownloaderEntries_Rollback;
            CREATE TABLE DownloaderEntries_Rollback (
                EntryId TEXT PRIMARY KEY,
                Number INTEGER NOT NULL,
                IsSelected INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Category TEXT NOT NULL,
                Source INTEGER NOT NULL,
                Author TEXT NOT NULL,
                Url TEXT NOT NULL,
                GameDomain TEXT NOT NULL,
                NexusModId INTEGER NULL,
                NexusFileId INTEGER NULL,
                NexusFileUuid TEXT NOT NULL DEFAULT '',
                Version TEXT NOT NULL,
                Status INTEGER NOT NULL,
                Progress REAL NOT NULL,
                BytesPerSecond REAL NOT NULL,
                Size INTEGER NOT NULL,
                LocalArchivePath TEXT NOT NULL,
                TemporaryPath TEXT NOT NULL,
                Error TEXT NOT NULL,
                Description TEXT NOT NULL,
                UpdatedAt TEXT NULL,
                CreatedAt TEXT NOT NULL,
                AdditionalUrl TEXT NOT NULL DEFAULT '',
                DownloadOrder INTEGER NULL,
                CatalogId TEXT NOT NULL DEFAULT '',
                DecisionGroup TEXT NOT NULL DEFAULT '',
                Sha256 TEXT NOT NULL DEFAULT '',
                ArchiveFileName TEXT NOT NULL DEFAULT '',
                UpdateCheckStatus INTEGER NOT NULL DEFAULT 0,
                AvailableVersion TEXT NOT NULL DEFAULT '',
                AvailableFileId INTEGER NULL,
                AvailableFileUuid TEXT NOT NULL DEFAULT '',
                LastUpdateCheckUtc TEXT NULL,
                UpdateCheckMessage TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO DownloaderEntries_Rollback (
                EntryId, Number, IsSelected, Name, Category, Source,
                Author, Url, GameDomain, NexusModId, NexusFileId,
                NexusFileUuid,
                Version, Status, Progress, BytesPerSecond, Size,
                LocalArchivePath, TemporaryPath, Error, Description,
                UpdatedAt, CreatedAt, AdditionalUrl, DownloadOrder,
                CatalogId, DecisionGroup, Sha256, ArchiveFileName,
                UpdateCheckStatus, AvailableVersion, AvailableFileId,
                AvailableFileUuid, LastUpdateCheckUtc, UpdateCheckMessage)
            SELECT
                EntryId, Number, IsSelected, Name, Category, Source,
                Author, Url, GameDomain, NexusModId, NexusFileId,
                NexusFileUuid,
                Version, Status, Progress, BytesPerSecond, Size,
                LocalArchivePath, TemporaryPath, Error, Description,
                UpdatedAt, CreatedAt, AdditionalUrl, DownloadOrder,
                CatalogId, DecisionGroup, Sha256, ArchiveFileName,
                UpdateCheckStatus, AvailableVersion, AvailableFileId,
                AvailableFileUuid, LastUpdateCheckUtc, UpdateCheckMessage
            FROM DownloaderEntries;
            DROP TABLE DownloaderEntries;
            ALTER TABLE DownloaderEntries_Rollback
                RENAME TO DownloaderEntries;
            """;
        await rebuild.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string columnName,
        string declaration,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = "PRAGMA table_info(DownloaderEntries);";
        var exists = false;
        await using (var reader =
                     await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(
                        reader.GetString(1),
                        columnName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists)
            return;
        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText =
            $"ALTER TABLE DownloaderEntries ADD COLUMN {columnName} {declaration};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
