using Microsoft.Data.Sqlite;

namespace RipperWorks.Organizer;

internal static partial class OrganizerDatabaseSchema
{
    public const int CurrentVersion = 9;

    public static async Task InitializeAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var version = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (version > CurrentVersion)
        {
            throw new NotSupportedException(
                $"Organizer database schema {version} is newer than supported schema {CurrentVersion}.");
        }

        var isUnversionedExisting = false;
        var tableCount = await GetTableCountAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version == 0 && tableCount > 0)
        {
            isUnversionedExisting = true;
        }

        var isBrandNewEmpty = version == 0 && !isUnversionedExisting;

        if (!isBrandNewEmpty && version < CurrentVersion)
        {
            await OrganizerDatabaseMigrationSafety.CreateVerifiedBackupIfNeededAsync(
                connection, databasePath, version, isUnversionedExisting, cancellationToken).ConfigureAwait(false);
        }

        if (version < CurrentVersion || isUnversionedExisting)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (isBrandNewEmpty)
                {
                    await CreateSchemaVersion9Async(connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var startVersion = isUnversionedExisting ? 1 : version;
                    await MigrateToVersion8IncrementalAsync(connection, (SqliteTransaction)transaction, startVersion, cancellationToken).ConfigureAwait(false);
                    await MigrateFromVersion8To9Async(connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);
                }

                await SetUserVersionAsync(connection, (SqliteTransaction)transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                throw;
            }
        }

        await OrganizerDatabaseMigrationSafety.CheckIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<long> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task SetUserVersionAsync(SqliteConnection connection, SqliteTransaction transaction, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> GetTableCountAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task CreateSchemaVersion9Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE Groups (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                SortOrder INTEGER NOT NULL, IsExpanded INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, IsSelected INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE Packages (
                PackageId TEXT PRIMARY KEY, ArchivePath TEXT NOT NULL UNIQUE COLLATE NOCASE, IndexedDisplayName TEXT NOT NULL,
                CustomDisplayName TEXT NULL, IndexedVersion TEXT NULL, CustomVersion TEXT NULL, Note TEXT NOT NULL DEFAULT '',
                GroupId INTEGER NULL, Source INTEGER NOT NULL, ArchiveFileName TEXT NOT NULL, ArchiveFormat TEXT NOT NULL,
                FileSize INTEGER NOT NULL, LastWriteUtc TEXT NOT NULL, Author TEXT NULL, Category TEXT NULL, GameDomain TEXT NULL,
                NexusModId INTEGER NULL, NexusFileId INTEGER NULL, NexusUrl TEXT NULL, ArchiveSha256 TEXT NULL, MetadataSource TEXT NOT NULL,
                MetadataSchemaVersion INTEGER NOT NULL, HasMetadata INTEGER NOT NULL, IsPresent INTEGER NOT NULL, LastIndexedAtUtc TEXT NULL,
                InstallationState INTEGER NOT NULL DEFAULT 0, LibraryModId TEXT NULL, ArchiveFamilyKey TEXT NULL, NexusFileUuid TEXT NULL, DownloadedAtUtc TEXT NULL,
                FOREIGN KEY (GroupId) REFERENCES Groups(Id) ON DELETE SET NULL);
            CREATE TABLE PackageAnalyses (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PackageId TEXT NOT NULL UNIQUE, AnalyzerVersion INTEGER NOT NULL, State INTEGER NOT NULL,
                DetectedRoot TEXT NULL, SelectedRoot TEXT NULL, EntryCount INTEGER NOT NULL, InstallableFileCount INTEGER NOT NULL, WarningCount INTEGER NOT NULL,
                AnalyzedAtUtc TEXT NOT NULL, ResultCode TEXT NULL, ResultMessage TEXT NULL, ArchiveFileSize INTEGER NOT NULL, ArchiveLastWriteUtc TEXT NOT NULL,
                ArchivePath TEXT NOT NULL, Fingerprint TEXT NOT NULL, FOREIGN KEY (PackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE CASCADE);
            CREATE TABLE PackageAnalysisEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, AnalysisId INTEGER NOT NULL, OriginalPath TEXT NOT NULL, NormalizedPath TEXT NOT NULL,
                RelativeInstallPath TEXT NULL, EntrySize INTEGER NOT NULL, IsDirectory INTEGER NOT NULL, IsInstallable INTEGER NOT NULL, WarningCode TEXT NULL,
                FOREIGN KEY (AnalysisId) REFERENCES PackageAnalyses(Id) ON DELETE CASCADE);
            CREATE TABLE PackageAnalysisRoots (
                AnalysisId INTEGER NOT NULL, DetectedRoot TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY (AnalysisId, DetectedRoot),
                FOREIGN KEY (AnalysisId) REFERENCES PackageAnalyses(Id) ON DELETE CASCADE);
            CREATE TABLE GameProfile (
                Id INTEGER PRIMARY KEY CHECK (Id = 1), GameRoot TEXT NOT NULL, ExecutablePath TEXT NOT NULL, InitializedAtUtc TEXT NOT NULL,
                LastValidatedAtUtc TEXT NOT NULL, ValidationState INTEGER NOT NULL);
            CREATE TABLE InstalledMods (
                PackageId TEXT PRIMARY KEY, ArchivePath TEXT NOT NULL, ArchiveFingerprint TEXT NOT NULL, AnalysisFingerprint TEXT NOT NULL,
                SelectedRoot TEXT NULL, InstalledAtUtc TEXT NULL, InstallationState INTEGER NOT NULL,
                FOREIGN KEY (PackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE RESTRICT);
            CREATE TABLE InstalledFiles (
                PackageId TEXT NOT NULL, RelativeGamePath TEXT NOT NULL COLLATE NOCASE, InstalledContentHash TEXT NOT NULL,
                PreviousContentHash TEXT NULL, PreviousFileExisted INTEGER NOT NULL, Sequence INTEGER NOT NULL,
                PRIMARY KEY (PackageId, RelativeGamePath), FOREIGN KEY (PackageId) REFERENCES InstalledMods(PackageId) ON UPDATE CASCADE ON DELETE CASCADE);
            CREATE TABLE InstallOperationFiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, OperationId TEXT NOT NULL, RelativeGamePath TEXT NOT NULL COLLATE NOCASE,
                NewContentHash TEXT NOT NULL, PreviousContentHash TEXT NULL, PreviousFileExisted INTEGER NOT NULL, Sequence INTEGER NOT NULL,
                Applied INTEGER NOT NULL DEFAULT 0, UNIQUE(OperationId, RelativeGamePath), UNIQUE(OperationId, Sequence),
                FOREIGN KEY (OperationId) REFERENCES InstallOperations(OperationId) ON DELETE CASCADE);
            CREATE TABLE ManagedPaths (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, RelativePath TEXT NOT NULL UNIQUE COLLATE NOCASE, BaseFileExisted INTEGER NOT NULL DEFAULT 0, BaseContentHash TEXT NULL);
            CREATE TABLE ManagedPathLayers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, RelativePath TEXT NOT NULL COLLATE NOCASE, LayerOrder INTEGER NOT NULL, PackageId TEXT NOT NULL, ContentHash TEXT NOT NULL, OperationId TEXT NULL, InstalledAtUtc TEXT NOT NULL, UNIQUE (RelativePath, LayerOrder), UNIQUE (RelativePath, PackageId), FOREIGN KEY (PackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE CASCADE, FOREIGN KEY (RelativePath) REFERENCES ManagedPaths(RelativePath) ON UPDATE CASCADE ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_ManagedPathLayers_PackageId ON ManagedPathLayers(PackageId);
            CREATE INDEX IF NOT EXISTS IX_ManagedPathLayers_RelativePath_LayerOrder ON ManagedPathLayers(RelativePath, LayerOrder);
            CREATE TABLE LibraryMods (
                LibraryModId TEXT PRIMARY KEY, IndexedDisplayName TEXT NOT NULL, CustomDisplayName TEXT NULL, Source INTEGER NOT NULL, GameDomain TEXT NULL,
                NexusModId INTEGER NULL, Author TEXT NULL, Category TEXT NULL, NexusUrl TEXT NULL, GroupId INTEGER NULL, PreferredArchiveId TEXT NULL,
                CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, FOREIGN KEY (GroupId) REFERENCES Groups(Id) ON DELETE SET NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_LibraryMods_NexusIdentity ON LibraryMods(Source, GameDomain, NexusModId) WHERE Source = 1 AND NexusModId IS NOT NULL;
            CREATE INDEX IF NOT EXISTS IX_Packages_LibraryModId ON Packages(LibraryModId);
            CREATE TABLE LibraryModRelations (
                RelationId INTEGER PRIMARY KEY AUTOINCREMENT, FromLibraryModId TEXT NOT NULL, ToLibraryModId TEXT NOT NULL, RelationType INTEGER NOT NULL,
                Source INTEGER NOT NULL, IsConfirmed INTEGER NOT NULL, Note TEXT NOT NULL DEFAULT '', CreatedAtUtc TEXT NOT NULL, CHECK (FromLibraryModId <> ToLibraryModId),
                UNIQUE (FromLibraryModId, ToLibraryModId), FOREIGN KEY (FromLibraryModId) REFERENCES LibraryMods(LibraryModId) ON UPDATE CASCADE ON DELETE CASCADE,
                FOREIGN KEY (ToLibraryModId) REFERENCES LibraryMods(LibraryModId) ON UPDATE CASCADE ON DELETE CASCADE);
            CREATE TABLE PackageRelations (
                RelationId INTEGER PRIMARY KEY AUTOINCREMENT, FromPackageId TEXT NOT NULL, ToPackageId TEXT NOT NULL, RelationType INTEGER NOT NULL,
                Source INTEGER NOT NULL, IsConfirmed INTEGER NOT NULL, Note TEXT NOT NULL DEFAULT '', CreatedAtUtc TEXT NOT NULL, CHECK (FromPackageId <> ToPackageId),
                UNIQUE (FromPackageId, ToPackageId), FOREIGN KEY (FromPackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE CASCADE,
                FOREIGN KEY (ToPackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE CASCADE);
            CREATE TABLE ArchiveFamilyLinks (
                LibraryModId TEXT NOT NULL, FromArchiveId TEXT NOT NULL, ToArchiveId TEXT NOT NULL, Source TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (FromArchiveId, ToArchiveId), CHECK (FromArchiveId <> ToArchiveId), FOREIGN KEY (LibraryModId) REFERENCES LibraryMods(LibraryModId) ON DELETE CASCADE,
                FOREIGN KEY (FromArchiveId) REFERENCES Packages(PackageId) ON DELETE CASCADE, FOREIGN KEY (ToArchiveId) REFERENCES Packages(PackageId) ON DELETE CASCADE);
            CREATE TABLE ConfirmedArchiveUpdatePairs (
                GameDomain TEXT NOT NULL COLLATE NOCASE, NexusModId INTEGER NOT NULL, CurrentFileId INTEGER NOT NULL, AvailableFileId INTEGER NOT NULL,
                Source TEXT NOT NULL, ConfirmedAtUtc TEXT NOT NULL, PRIMARY KEY (GameDomain, NexusModId, CurrentFileId, AvailableFileId), CHECK (CurrentFileId <> AvailableFileId));
            CREATE TABLE GameProfiles (
                ProfileId TEXT PRIMARY KEY, Name TEXT NOT NULL UNIQUE COLLATE NOCASE, TargetType INTEGER NOT NULL, GameRootDirectory TEXT NOT NULL,
                GameExecutablePath TEXT NOT NULL, UserDocumentDirectory TEXT NULL, AppDataDirectory TEXT NULL, IsActive INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
            CREATE TABLE InstallOperations (
                OperationId TEXT PRIMARY KEY, PackageId TEXT NOT NULL, OperationType TEXT NOT NULL, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NULL, Status INTEGER NOT NULL, CurrentPhase INTEGER NOT NULL, ErrorMessage TEXT NULL, AppliedFileCount INTEGER NOT NULL DEFAULT 0, FOREIGN KEY (PackageId) REFERENCES Packages(PackageId) ON DELETE CASCADE);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateToVersion8IncrementalAsync(SqliteConnection connection, SqliteTransaction transaction, long startVersion, CancellationToken cancellationToken)
    {
        var v = startVersion;
        if (v == 0)
        {
            await ExecuteScriptAsync(connection, transaction,
                """
                CREATE TABLE Groups (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE COLLATE NOCASE, SortOrder INTEGER NOT NULL, IsExpanded INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL);
                CREATE TABLE Packages (PackageId TEXT PRIMARY KEY, ArchivePath TEXT NOT NULL UNIQUE COLLATE NOCASE, IndexedDisplayName TEXT NOT NULL, CustomDisplayName TEXT NULL, IndexedVersion TEXT NULL, CustomVersion TEXT NULL, Note TEXT NOT NULL DEFAULT '', GroupId INTEGER NULL, Source INTEGER NOT NULL, ArchiveFileName TEXT NOT NULL, ArchiveFormat TEXT NOT NULL, FileSize INTEGER NOT NULL, LastWriteUtc TEXT NOT NULL, Author TEXT NULL, Category TEXT NULL, GameDomain TEXT NULL, NexusModId INTEGER NULL, NexusFileId INTEGER NULL, NexusUrl TEXT NULL, ArchiveSha256 TEXT NULL, MetadataSource TEXT NOT NULL, MetadataSchemaVersion INTEGER NOT NULL, HasMetadata INTEGER NOT NULL, IsPresent INTEGER NOT NULL, FOREIGN KEY (GroupId) REFERENCES Groups(Id) ON DELETE SET NULL);
                CREATE TABLE PackageAnalyses (Id INTEGER PRIMARY KEY AUTOINCREMENT, PackageId TEXT NOT NULL UNIQUE, AnalyzerVersion INTEGER NOT NULL, State INTEGER NOT NULL, DetectedRoot TEXT NULL, SelectedRoot TEXT NULL, EntryCount INTEGER NOT NULL, InstallableFileCount INTEGER NOT NULL, WarningCount INTEGER NOT NULL, AnalyzedAtUtc TEXT NOT NULL, ResultCode TEXT NULL, ResultMessage TEXT NULL, ArchiveFileSize INTEGER NOT NULL, ArchiveLastWriteUtc TEXT NOT NULL, ArchivePath TEXT NOT NULL, Fingerprint TEXT NOT NULL, FOREIGN KEY (PackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE CASCADE);
                CREATE TABLE PackageAnalysisEntries (Id INTEGER PRIMARY KEY AUTOINCREMENT, AnalysisId INTEGER NOT NULL, OriginalPath TEXT NOT NULL, NormalizedPath TEXT NOT NULL, RelativeInstallPath TEXT NULL, EntrySize INTEGER NOT NULL, IsDirectory INTEGER NOT NULL, IsInstallable INTEGER NOT NULL, WarningCode TEXT NULL, FOREIGN KEY (AnalysisId) REFERENCES PackageAnalyses(Id) ON DELETE CASCADE);
                CREATE TABLE PackageAnalysisRoots (AnalysisId INTEGER NOT NULL, DetectedRoot TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY (AnalysisId, DetectedRoot), FOREIGN KEY (AnalysisId) REFERENCES PackageAnalyses(Id) ON DELETE CASCADE);
                """, cancellationToken).ConfigureAwait(false);
            v = 1;
        }
        if (v < 2)
        {
            await EnsureColumnAsync(connection, transaction, "Groups", "IsSelected", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, transaction, "Packages", "LastIndexedAtUtc", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            v = 2;
        }
        if (v < 3)
        {
            await EnsureColumnAsync(connection, transaction, "Packages", "InstallationState", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await ExecuteScriptAsync(connection, transaction,
                "CREATE TABLE IF NOT EXISTS GameProfile (Id INTEGER PRIMARY KEY CHECK (Id = 1), GameRoot TEXT NOT NULL, ExecutablePath TEXT NOT NULL, InitializedAtUtc TEXT NOT NULL, LastValidatedAtUtc TEXT NOT NULL, ValidationState INTEGER NOT NULL);",
                cancellationToken).ConfigureAwait(false);
            v = 3;
        }
        if (v < 4)
        {
            await ExecuteScriptAsync(connection, transaction,
                """
                CREATE TABLE IF NOT EXISTS InstalledMods (PackageId TEXT PRIMARY KEY, ArchivePath TEXT NOT NULL, ArchiveFingerprint TEXT NOT NULL, AnalysisFingerprint TEXT NOT NULL, SelectedRoot TEXT NULL, InstalledAtUtc TEXT NULL, InstallationState INTEGER NOT NULL, FOREIGN KEY (PackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE RESTRICT);
                CREATE TABLE IF NOT EXISTS InstalledFiles (PackageId TEXT NOT NULL, RelativeGamePath TEXT NOT NULL COLLATE NOCASE, InstalledContentHash TEXT NOT NULL, PreviousContentHash TEXT NULL, PreviousFileExisted INTEGER NOT NULL, Sequence INTEGER NOT NULL, PRIMARY KEY (PackageId, RelativeGamePath), FOREIGN KEY (PackageId) REFERENCES InstalledMods(PackageId) ON UPDATE CASCADE ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS InstallOperationFiles (Id INTEGER PRIMARY KEY AUTOINCREMENT, OperationId TEXT NOT NULL, RelativeGamePath TEXT NOT NULL COLLATE NOCASE, NewContentHash TEXT NOT NULL, PreviousContentHash TEXT NULL, PreviousFileExisted INTEGER NOT NULL, Sequence INTEGER NOT NULL, Applied INTEGER NOT NULL DEFAULT 0, UNIQUE(OperationId, RelativeGamePath), UNIQUE(OperationId, Sequence), FOREIGN KEY (OperationId) REFERENCES InstallOperations(OperationId) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS ManagedPaths (Id INTEGER PRIMARY KEY AUTOINCREMENT, RelativePath TEXT NOT NULL UNIQUE COLLATE NOCASE, AddedAtUtc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS ManagedPathLayers (Id INTEGER PRIMARY KEY AUTOINCREMENT, LayerName TEXT NOT NULL UNIQUE COLLATE NOCASE, SortOrder INTEGER NOT NULL, IsEnabled INTEGER NOT NULL, AddedAtUtc TEXT NOT NULL);
                """, cancellationToken).ConfigureAwait(false);
            v = 4;
        }
        if (v < 5) v = 5;
        if (v < 6)
        {
            await EnsureColumnAsync(connection, transaction, "Packages", "IndexedVersion", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, transaction, "Packages", "GameDomain", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, transaction, "Packages", "ArchiveSha256", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            v = 6;
        }
        if (v < 7)
        {
            await ExecuteScriptAsync(connection, transaction,
                """
                CREATE TABLE IF NOT EXISTS LibraryMods (LibraryModId TEXT PRIMARY KEY, IndexedDisplayName TEXT NOT NULL, CustomDisplayName TEXT NULL, Source INTEGER NOT NULL, GameDomain TEXT NULL, NexusModId INTEGER NULL, Author TEXT NULL, Category TEXT NULL, NexusUrl TEXT NULL, GroupId INTEGER NULL, PreferredArchiveId TEXT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, FOREIGN KEY (GroupId) REFERENCES Groups(Id) ON DELETE SET NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_LibraryMods_NexusIdentity ON LibraryMods(Source, GameDomain, NexusModId) WHERE Source = 1 AND NexusModId IS NOT NULL;
                CREATE TABLE IF NOT EXISTS LibraryModRelations (RelationId INTEGER PRIMARY KEY AUTOINCREMENT, FromLibraryModId TEXT NOT NULL, ToLibraryModId TEXT NOT NULL, RelationType INTEGER NOT NULL, Source INTEGER NOT NULL, IsConfirmed INTEGER NOT NULL, Note TEXT NOT NULL DEFAULT '', CreatedAtUtc TEXT NOT NULL, CHECK (FromLibraryModId <> ToLibraryModId), UNIQUE (FromLibraryModId, ToLibraryModId), FOREIGN KEY (FromLibraryModId) REFERENCES LibraryMods(LibraryModId) ON UPDATE CASCADE ON DELETE CASCADE, FOREIGN KEY (ToLibraryModId) REFERENCES LibraryMods(LibraryModId) ON UPDATE CASCADE ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS PackageRelations (RelationId INTEGER PRIMARY KEY AUTOINCREMENT, FromPackageId TEXT NOT NULL, ToPackageId TEXT NOT NULL, RelationType INTEGER NOT NULL, Source INTEGER NOT NULL, IsConfirmed INTEGER NOT NULL, Note TEXT NOT NULL DEFAULT '', CreatedAtUtc TEXT NOT NULL, CHECK (FromPackageId <> ToPackageId), UNIQUE (FromPackageId, ToPackageId), FOREIGN KEY (FromPackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE CASCADE, FOREIGN KEY (ToPackageId) REFERENCES Packages(PackageId) ON UPDATE CASCADE ON DELETE CASCADE);
                """, cancellationToken).ConfigureAwait(false);

            await EnsureColumnAsync(connection, transaction, "Packages", "LibraryModId", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, transaction, "Packages", "ArchiveFamilyKey", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, transaction, "Packages", "NexusFileUuid", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, transaction, "Packages", "DownloadedAtUtc", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            v = 7;
        }
        if (v < 8)
        {
            await ExecuteScriptAsync(connection, transaction,
                """
                CREATE TABLE ArchiveFamilyLinks (LibraryModId TEXT NOT NULL, FromArchiveId TEXT NOT NULL, ToArchiveId TEXT NOT NULL, Source TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, PRIMARY KEY (FromArchiveId, ToArchiveId), CHECK (FromArchiveId <> ToArchiveId), FOREIGN KEY (LibraryModId) REFERENCES LibraryMods(LibraryModId) ON DELETE CASCADE, FOREIGN KEY (FromArchiveId) REFERENCES Packages(PackageId) ON DELETE CASCADE, FOREIGN KEY (ToArchiveId) REFERENCES Packages(PackageId) ON DELETE CASCADE);
                CREATE TABLE ConfirmedArchiveUpdatePairs (GameDomain TEXT NOT NULL COLLATE NOCASE, NexusModId INTEGER NOT NULL, CurrentFileId INTEGER NOT NULL, AvailableFileId INTEGER NOT NULL, Source TEXT NOT NULL, ConfirmedAtUtc TEXT NOT NULL, PRIMARY KEY (GameDomain, NexusModId, CurrentFileId, AvailableFileId), CHECK (CurrentFileId <> AvailableFileId));
                CREATE TABLE IF NOT EXISTS GameProfiles (ProfileId TEXT PRIMARY KEY, Name TEXT NOT NULL UNIQUE COLLATE NOCASE, TargetType INTEGER NOT NULL, GameRootDirectory TEXT NOT NULL, GameExecutablePath TEXT NOT NULL, UserDocumentDirectory TEXT NULL, AppDataDirectory TEXT NULL, IsActive INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS InstallOperations (OperationId TEXT PRIMARY KEY, ProfileId TEXT NOT NULL, PackageId TEXT NOT NULL, State INTEGER NOT NULL DEFAULT 0, Status INTEGER NOT NULL DEFAULT 0, CurrentPhase INTEGER NOT NULL DEFAULT 0, AppliedFileCount INTEGER NOT NULL DEFAULT 0, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NULL, ErrorMessage TEXT NULL, FOREIGN KEY (ProfileId) REFERENCES GameProfiles(ProfileId) ON DELETE CASCADE, FOREIGN KEY (PackageId) REFERENCES Packages(PackageId) ON DELETE CASCADE);
                """, cancellationToken).ConfigureAwait(false);

            await EnsureColumnAsync(connection, transaction, "InstallOperations", "State", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, transaction, "InstallOperations", "Status", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, transaction, "InstallOperations", "CurrentPhase", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, transaction, "InstallOperations", "AppliedFileCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string typeAndConstraints, CancellationToken cancellationToken)
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

    private static async Task ExecuteScriptAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
