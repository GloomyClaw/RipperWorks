using Microsoft.Data.Sqlite;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RipperWorks.App")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RipperWorks.Tests")]

namespace RipperWorks.Downloader;

public static class CatalogSchemaContract
{
    public const int CurrentVersion = CatalogDatabaseSchema.CurrentVersion;
}

internal static class CatalogDatabaseSchema
{
    public const int CurrentVersion = 4;

    internal static async Task<long> ReadAndValidateSupportedVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        var version = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version > CurrentVersion)
        {
            throw new NotSupportedException(
                $"Catalog database schema {version} is newer than supported schema {CurrentVersion}.");
        }
        return version;
    }

    public static async Task ValidateSupportedVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        await ReadAndValidateSupportedVersionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public static async Task InitializeAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var version = await ReadAndValidateSupportedVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        var isUnversionedExisting = false;
        var tableCount = await GetTableCountAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version == 0 && tableCount > 0)
        {
            isUnversionedExisting = true;
        }

        var isBrandNewEmpty = version == 0 && !isUnversionedExisting;

        if (!isBrandNewEmpty && version < CurrentVersion)
        {
            await CatalogDatabaseMigrationSafety.CreateVerifiedBackupIfNeededAsync(
                connection,
                databasePath,
                version,
                isUnversionedExisting,
                cancellationToken).ConfigureAwait(false);
        }

        if (version < CurrentVersion || isUnversionedExisting)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CreateOrMigratePackagesTableAsync(connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);
                await CreateNexusRequirementSnapshotSchemaAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    cancellationToken).ConfigureAwait(false);
                await SetUserVersionAsync(connection, (SqliteTransaction)transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve original exception
                }
                throw;
            }
        }

        await CatalogDatabaseMigrationSafety.CheckIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<long> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    private static async Task SetUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> GetTableCountAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    private static async Task CreateOrMigratePackagesTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText =
                """
                CREATE TABLE IF NOT EXISTS Packages (
                    RecordId TEXT PRIMARY KEY,
                    ArchivePath TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    DisplayName TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    Author TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    Version TEXT NOT NULL,
                    GameDomain TEXT NOT NULL,
                    NexusModId INTEGER NULL,
                    NexusFileId INTEGER NULL,
                    NexusUrl TEXT NOT NULL,
                    FileSize INTEGER NOT NULL,
                    Fingerprint TEXT NOT NULL DEFAULT '',
                    UpdatedAt TEXT NULL,
                    RegisteredAt TEXT NOT NULL
                );
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureColumnAsync(connection, transaction, "Packages", "Fingerprint", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string typeAndConstraints,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = $"PRAGMA table_info({table});";
        var exists = false;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (!exists)
        {
            await using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAndConstraints};";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CreateNexusRequirementSnapshotSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS NexusRequirementSnapshotStates (
                OwnerGameId INTEGER NOT NULL CHECK (OwnerGameId > 0),
                OwnerModId INTEGER NOT NULL CHECK (OwnerModId > 0),
                Traversal INTEGER NOT NULL CHECK (Traversal IN (0, 1)),
                SuccessfulGeneration INTEGER NOT NULL
                    CHECK (SuccessfulGeneration >= 0),
                LastSuccessfulAtUtc TEXT NULL,
                LastAttemptAtUtc TEXT NOT NULL,
                LatestFailureKind INTEGER NULL
                    CHECK (LatestFailureKind IS NULL OR
                           LatestFailureKind BETWEEN 0 AND 7),
                LatestFailureMessage TEXT NULL
                    CHECK (LatestFailureMessage IS NULL OR
                           length(LatestFailureMessage) <= 2048),
                PRIMARY KEY (OwnerGameId, OwnerModId, Traversal),
                CHECK ((SuccessfulGeneration = 0 AND
                        LastSuccessfulAtUtc IS NULL) OR
                       (SuccessfulGeneration > 0 AND
                        LastSuccessfulAtUtc IS NOT NULL)),
                CHECK ((LatestFailureKind IS NULL AND
                        LatestFailureMessage IS NULL) OR
                       LatestFailureKind IS NOT NULL)
            );

            CREATE TABLE IF NOT EXISTS NexusRequirementObservations (
                OwnerGameId INTEGER NOT NULL CHECK (OwnerGameId > 0),
                OwnerModId INTEGER NOT NULL CHECK (OwnerModId > 0),
                Traversal INTEGER NOT NULL CHECK (Traversal IN (0, 1)),
                ObservationOrdinal INTEGER NOT NULL
                    CHECK (ObservationOrdinal >= 0),
                SuccessfulGeneration INTEGER NOT NULL
                    CHECK (SuccessfulGeneration > 0),
                SourceGameId INTEGER NOT NULL CHECK (SourceGameId > 0),
                SourceModId INTEGER NOT NULL CHECK (SourceModId > 0),
                TargetKind INTEGER NOT NULL CHECK (TargetKind IN (0, 1)),
                TargetGameId INTEGER NULL,
                TargetModId INTEGER NULL,
                SourceDisplayName TEXT NULL
                    CHECK (SourceDisplayName IS NULL OR
                           length(SourceDisplayName) <= 1024),
                SourceProviderUrl TEXT NULL
                    CHECK (SourceProviderUrl IS NULL OR
                           length(SourceProviderUrl) <= 4096),
                SourceClickableUrl TEXT NULL
                    CHECK (SourceClickableUrl IS NULL OR
                           length(SourceClickableUrl) <= 4096),
                TargetDisplayName TEXT NULL
                    CHECK (TargetDisplayName IS NULL OR
                           length(TargetDisplayName) <= 1024),
                TargetProviderUrl TEXT NULL
                    CHECK (TargetProviderUrl IS NULL OR
                           length(TargetProviderUrl) <= 4096),
                TargetClickableUrl TEXT NULL
                    CHECK (TargetClickableUrl IS NULL OR
                           length(TargetClickableUrl) <= 4096),
                ProviderRequirementId TEXT NULL
                    CHECK (ProviderRequirementId IS NULL OR
                           length(ProviderRequirementId) <= 512),
                Notes TEXT NULL
                    CHECK (Notes IS NULL OR length(Notes) <= 8192),
                PRIMARY KEY (
                    OwnerGameId, OwnerModId, Traversal,
                    ObservationOrdinal),
                CHECK ((TargetKind = 0 AND
                        TargetGameId IS NOT NULL AND
                        TargetModId IS NOT NULL AND
                        TargetGameId > 0 AND TargetModId > 0) OR
                       (TargetKind = 1 AND TargetGameId IS NULL AND
                        TargetModId IS NULL))
            );

            CREATE UNIQUE INDEX IF NOT EXISTS
                UX_NexusRequirementObservations_NormalCanonical
            ON NexusRequirementObservations (
                OwnerGameId, OwnerModId, Traversal,
                SourceGameId, SourceModId, TargetGameId, TargetModId)
            WHERE TargetKind = 0;

            CREATE TABLE IF NOT EXISTS NexusRequirementDismissals (
                SourceGameId INTEGER NOT NULL CHECK (SourceGameId > 0),
                SourceModId INTEGER NOT NULL CHECK (SourceModId > 0),
                TargetGameId INTEGER NOT NULL CHECK (TargetGameId > 0),
                TargetModId INTEGER NOT NULL CHECK (TargetModId > 0),
                DismissedAtUtc TEXT NOT NULL,
                PRIMARY KEY (SourceGameId, SourceModId, TargetGameId, TargetModId)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
