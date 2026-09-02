using System.Globalization;
using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class OrganizerRepository
{
    public const int CurrentSchemaVersion = OrganizerDatabaseSchema.CurrentVersion;

    private readonly string _databasePath;
    private readonly Action<string>? _technicalWarning;

    public OrganizerRepository(
        string databasePath,
        Action<string>? technicalWarning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _technicalWarning = technicalWarning;
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("Database directory is missing."));
        await using var connection = await OpenAsync(cancellationToken);
        await OrganizerDatabaseSchema.InitializeAsync(connection, _databasePath, cancellationToken);
    }

    public async Task<int> GetUserVersionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return checked((int)await OrganizerDatabaseSchema.ReadUserVersionAsync(connection, cancellationToken));
    }

    public async Task<IReadOnlyList<string>> GetTableNamesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    // Historical RF-02 note: RemoveAutomaticNexusRelationsAsync was replaced by versioned migration RemoveAutomaticNexusRelationsMigration.

    public async Task<LibraryReconciliationResult> SynchronizePackagesAsync(
        IReadOnlyList<PackageRecord> packages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var indexedAtUtc = DateTime.UtcNow;
        var existingRows = await LoadCatalogRowsAsync(
            connection,
            (SqliteTransaction)transaction,
            cancellationToken);
        var addedCount = 0;
        var updatedCount = 0;
        var addedPackageIds = new HashSet<PackageId>();

        await using (var markMissing = connection.CreateCommand())
        {
            markMissing.Transaction = (SqliteTransaction)transaction;
            markMissing.CommandText =
                """
                UPDATE Packages
                SET IsPresent = 0,
                    LastIndexedAtUtc = $indexedAtUtc;
                """;
            markMissing.Parameters.AddWithValue(
                "$indexedAtUtc",
                FormatUtc(indexedAtUtc));
            await markMissing.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archivePath = Path.GetFullPath(package.ArchivePath);
            var identity = BuildArchiveIdentity(
                package.Source,
                package.GameDomain,
                package.NexusModId,
                package.NexusFileId,
                package.NexusUrl,
                package.Sha256,
                archivePath);
            var match = existingRows.FirstOrDefault(row =>
                string.Equals(
                    row.ArchivePath,
                    archivePath,
                    StringComparison.OrdinalIgnoreCase));
            match ??= identity is not { IsFullyConfirmed: true }
                ? null
                : existingRows
                    .Where(row => row.Identity == identity)
                    .OrderByDescending(row => row.IsInstalled)
                    .ThenByDescending(row => row.IsPresent)
                    .ThenByDescending(MetadataScore)
                    .ThenByDescending(row => row.LastIndexedAtUtc)
                    .FirstOrDefault();

            if (match is null)
            {
                await InsertPackageAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    package,
                    indexedAtUtc,
                    cancellationToken);
                addedCount++;
                addedPackageIds.Add(package.PackageId);
                existingRows.Add(CatalogRow.FromPackage(
                    package,
                    indexedAtUtc));
                continue;
            }

            var pathOwner = existingRows.FirstOrDefault(row =>
                row.PackageId != match.PackageId &&
                string.Equals(
                    row.ArchivePath,
                    archivePath,
                    StringComparison.OrdinalIgnoreCase));
            if (pathOwner is not null)
                match = pathOwner;

            await UpdateIndexedPackageAsync(
                connection,
                (SqliteTransaction)transaction,
                match.PackageId,
                package,
                indexedAtUtc,
                cancellationToken);
            updatedCount++;
            existingRows.Remove(match);
            existingRows.Add(CatalogRow.FromPackage(
                package,
                indexedAtUtc,
                match.PackageId,
                match.IsInstalled,
                match.CustomDisplayName,
                match.CustomVersion,
                match.Note,
                match.GroupId,
                match.IndexedVersion));
        }

        var mergedDuplicateCount = await MergeExactArchiveDuplicatesAsync(
            connection,
            (SqliteTransaction)transaction,
            cancellationToken);
        var removedMissingCount = await PruneMissingUninstalledAsync(
            connection,
            (SqliteTransaction)transaction,
            cancellationToken);
        await SynchronizeLibraryModsAsync(
            connection,
            (SqliteTransaction)transaction,
            addedPackageIds,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            packages.Count,
            addedCount,
            updatedCount,
            removedMissingCount,
            mergedDuplicateCount);
    }

    public async Task<bool> DeleteMissingUninstalledPackageAsync(
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var deleted = await DeleteMissingUninstalledPackageAsync(
            connection,
            (SqliteTransaction)transaction,
            packageId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<IReadOnlyList<OrganizerPackageRecord>> LoadPackagesAsync(
        bool presentOnly = true,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT PackageId, ArchivePath, IndexedDisplayName, CustomDisplayName,
                   IndexedVersion, CustomVersion, Note, GroupId, Source,
                   ArchiveFileName, ArchiveFormat, FileSize, LastWriteUtc,
                   Author, Category, GameDomain, NexusModId, NexusFileId,
                   NexusUrl, ArchiveSha256, MetadataSource,
                   MetadataSchemaVersion, HasMetadata, IsPresent,
                   LastIndexedAtUtc, InstallationState, LibraryModId,
                   ArchiveFamilyKey, NexusFileUuid, DownloadedAtUtc
            FROM Packages
            WHERE ($presentOnly = 0 OR IsPresent = 1)
            ORDER BY IndexedDisplayName COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$presentOnly", presentOnly ? 1 : 0);
        var packages = new List<OrganizerPackageRecord>();
        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var packageId = new PackageId(reader.GetString(0));
                packages.Add(new OrganizerPackageRecord
                {
                    Package = new PackageRecord
                    {
                        PackageId = packageId,
                        ArchivePath = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        ArchiveFileName = reader.GetString(9),
                        ArchiveFormat = reader.GetString(10),
                        FileSize = reader.GetInt64(11),
                        LastWriteUtc = ParseUtc(reader.GetString(12)),
                        Source = (PackageSource)reader.GetInt32(8),
                        LibraryModId = reader.IsDBNull(26)
                            ? null
                            : new LibraryModId(reader.GetString(26)),
                        Version = GetNullableString(reader, 4),
                        Author = GetNullableString(reader, 13),
                        Category = GetNullableString(reader, 14),
                        GameDomain = GetNullableString(reader, 15),
                        NexusModId = GetNullableInt64(reader, 16),
                        NexusFileId = GetNullableInt64(reader, 17),
                        NexusUrl = GetNullableString(reader, 18),
                        ArchiveFamilyKey = GetNullableString(reader, 27),
                        NexusFileUuid = GetNullableString(reader, 28),
                        Sha256 = GetNullableString(reader, 19),
                        DownloadedAtUtc = reader.IsDBNull(29)
                            ? null
                            : ParseUtc(reader.GetString(29)),
                        MetadataSource = reader.GetString(20),
                        MetadataSchemaVersion = reader.GetInt32(21),
                        HasMetadata = reader.GetInt32(22) != 0,
                        InstallationState =
                            (PackageInstallationState)reader.GetInt32(25)
                    },
                    CustomDisplayName = GetNullableString(reader, 3),
                    CustomVersion = GetNullableString(reader, 5),
                    Note = reader.GetString(6),
                    GroupId = GetNullableInt64(reader, 7),
                    IsPresent = reader.GetInt32(23) != 0,
                    LastIndexedAtUtc = reader.IsDBNull(24)
                        ? null
                        : ParseUtc(reader.GetString(24))
                });
            }
        }

        var result = new List<OrganizerPackageRecord>(packages.Count);
        foreach (var package in packages)
        {
            result.Add(package with
            {
                Analysis = await LoadAnalysisAsync(
                    connection,
                    package.Package.PackageId,
                    cancellationToken)
            });
        }
        return result;
    }

    public async Task<GameProfileRecord?> LoadGameProfileAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT GameRoot, ExecutablePath, InitializedAtUtc,
                   LastValidatedAtUtc, ValidationState
            FROM GameProfile
            WHERE Id = 1;
            """;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new GameProfileRecord
        {
            GameRoot = reader.GetString(0),
            ExecutablePath = reader.GetString(1),
            InitializedAtUtc = ParseUtc(reader.GetString(2)),
            LastValidatedAtUtc = ParseUtc(reader.GetString(3)),
            ValidationState =
                (GameProfileValidationState)reader.GetInt32(4)
        };
    }

    public async Task<GameProfileRecord> SaveGameProfileAsync(
        GameProfileValidationResult validation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                "Only a successfully validated game profile can be saved.");
        var now = DateTime.UtcNow;
        var existing = await LoadGameProfileAsync(cancellationToken);
        var initializedAtUtc =
            existing is not null &&
            string.Equals(
                existing.GameRoot,
                validation.GameRoot,
                StringComparison.OrdinalIgnoreCase)
                ? existing.InitializedAtUtc
                : now;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO GameProfile (
                Id, GameRoot, ExecutablePath, InitializedAtUtc,
                LastValidatedAtUtc, ValidationState)
            VALUES (
                1, $gameRoot, $executablePath, $initializedAtUtc,
                $lastValidatedAtUtc, $validationState)
            ON CONFLICT(Id) DO UPDATE SET
                GameRoot = excluded.GameRoot,
                ExecutablePath = excluded.ExecutablePath,
                InitializedAtUtc = excluded.InitializedAtUtc,
                LastValidatedAtUtc = excluded.LastValidatedAtUtc,
                ValidationState = excluded.ValidationState;
            """;
        command.Parameters.AddWithValue(
            "$gameRoot",
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(validation.GameRoot)));
        command.Parameters.AddWithValue(
            "$executablePath",
            Path.GetFullPath(validation.ExecutablePath));
        command.Parameters.AddWithValue(
            "$initializedAtUtc",
            FormatUtc(initializedAtUtc));
        command.Parameters.AddWithValue(
            "$lastValidatedAtUtc",
            FormatUtc(now));
        command.Parameters.AddWithValue(
            "$validationState",
            (int)GameProfileValidationState.Valid);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await LoadGameProfileAsync(cancellationToken))!;
    }

    public async Task UpdatePackageDetailsAsync(
        PackageId packageId,
        string? displayName,
        string? version,
        string? note,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Packages
            SET CustomDisplayName = $displayName,
                CustomVersion = $version,
                Note = $note,
                GroupId = $groupId
            WHERE PackageId = $packageId;
            """;
        command.Parameters.AddWithValue("$displayName", DbValue(displayName));
        command.Parameters.AddWithValue("$version", DbValue(version));
        command.Parameters.AddWithValue("$note", note ?? string.Empty);
        command.Parameters.AddWithValue("$groupId", DbValue(groupId));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryGroupRecord>> LoadGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, Name, SortOrder, IsExpanded, CreatedAtUtc, IsSelected
            FROM Groups
            ORDER BY SortOrder, Name COLLATE NOCASE;
            """;
        var groups = new List<LibraryGroupRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3) != 0,
                ParseUtc(reader.GetString(4)),
                reader.GetInt32(5) != 0));
        }
        return groups;
    }

    public async Task<DateTime?> GetLastIndexedAtUtcAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT MAX(LastIndexedAtUtc) FROM Packages;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : ParseUtc(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    public async Task SetSelectedGroupAsync(
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "UPDATE Groups SET IsSelected = 0;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        if (groupId is not null)
        {
            await using var select = connection.CreateCommand();
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText =
                "UPDATE Groups SET IsSelected = 1 WHERE Id = $id;";
            select.Parameters.AddWithValue("$id", groupId.Value);
            await select.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<LibraryGroupRecord> CreateGroupAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeGroupName(name);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Groups (Name, SortOrder, IsExpanded, CreatedAtUtc)
            VALUES (
                $name,
                COALESCE((SELECT MAX(SortOrder) + 1 FROM Groups), 0),
                1,
                $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        var createdAtUtc = DateTime.UtcNow;
        command.Parameters.AddWithValue("$name", normalized);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            FormatUtc(createdAtUtc));
        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        var groups = await LoadGroupsAsync(cancellationToken);
        return groups.Single(group => group.Id == id);
    }

    public async Task RenameGroupAsync(
        long groupId,
        string name,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE Groups SET Name = $name WHERE Id = $id;";
        command.Parameters.AddWithValue("$name", NormalizeGroupName(name));
        command.Parameters.AddWithValue("$id", groupId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteGroupIfEmptyAsync(
        long groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM Groups
            WHERE Id = $id
              AND NOT EXISTS (
                  SELECT 1 FROM Packages WHERE GroupId = $id);
            """;
        command.Parameters.AddWithValue("$id", groupId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task AssignPackageToGroupAsync(
        PackageId packageId,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE Packages SET GroupId = $groupId WHERE PackageId = $packageId;";
        command.Parameters.AddWithValue("$groupId", DbValue(groupId));
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetGroupExpandedAsync(
        long groupId,
        bool isExpanded,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE Groups SET IsExpanded = $value WHERE Id = $id;";
        command.Parameters.AddWithValue("$value", isExpanded ? 1 : 0);
        command.Parameters.AddWithValue("$id", groupId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReorderGroupsAsync(
        IReadOnlyList<long> orderedGroupIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedGroupIds);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        for (var index = 0; index < orderedGroupIds.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                "UPDATE Groups SET SortOrder = $order WHERE Id = $id;";
            command.Parameters.AddWithValue("$order", index);
            command.Parameters.AddWithValue("$id", orderedGroupIds[index]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Test-only fault injection for SaveAnalysisAsync transaction boundaries.
    /// </summary>
    internal AnalysisSaveFaultPhase TestOnlySaveAnalysisFaultPhase { get; set; }

    /// <summary>
    /// Test-only: immediately before transaction.CommitAsync.
    /// Cancellation after this hook (and the following ThrowIfCancellationRequested)
    /// is the last user-cancellable point; the transaction still rolls back.
    /// </summary>
    internal Func<CancellationToken, Task>? TestOnlyBeforeCommit { get; set; }

    /// <summary>
    /// Test-only: after successful CommitAsync, before loading/returning the
    /// committed record. Cancellation here must not convert commit into failure.
    /// </summary>
    internal Func<CancellationToken, Task>? TestOnlyAfterCommitBeforeReturn { get; set; }

    public async Task<PackageAnalysisRecord> SaveAnalysisAsync(
        ArchiveAnalysisDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText =
                "DELETE FROM PackageAnalyses WHERE PackageId = $packageId;";
            delete.Parameters.AddWithValue(
                "$packageId",
                draft.Package.PackageId.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        ThrowSaveFaultIf(
            AnalysisSaveFaultPhase.AfterDeleteOldBeforeInsert,
            cancellationToken);

        long analysisId;
        var analyzedAtUtc = DateTime.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO PackageAnalyses (
                    PackageId, AnalyzerVersion, State, DetectedRoot,
                    SelectedRoot, EntryCount, InstallableFileCount,
                    WarningCount, AnalyzedAtUtc, ResultCode, ResultMessage,
                    ArchiveFileSize, ArchiveLastWriteUtc, ArchivePath,
                    Fingerprint)
                VALUES (
                    $packageId, $analyzerVersion, $state, $detectedRoot,
                    $selectedRoot, $entryCount, $installableFileCount,
                    $warningCount, $analyzedAtUtc, $resultCode, $resultMessage,
                    $archiveFileSize, $archiveLastWriteUtc, $archivePath,
                    $fingerprint);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue(
                "$packageId",
                draft.Package.PackageId.Value);
            command.Parameters.AddWithValue(
                "$analyzerVersion",
                draft.AnalyzerVersion);
            command.Parameters.AddWithValue("$state", (int)draft.State);
            command.Parameters.AddWithValue(
                "$detectedRoot",
                DbValue(draft.DetectedRoot));
            command.Parameters.AddWithValue(
                "$selectedRoot",
                DbValue(draft.SelectedRoot));
            command.Parameters.AddWithValue("$entryCount", draft.Entries.Count);
            command.Parameters.AddWithValue(
                "$installableFileCount",
                draft.InstallableFileCount);
            command.Parameters.AddWithValue(
                "$warningCount",
                draft.WarningCount);
            command.Parameters.AddWithValue(
                "$analyzedAtUtc",
                FormatUtc(analyzedAtUtc));
            command.Parameters.AddWithValue(
                "$resultCode",
                DbValue(draft.ResultCode));
            command.Parameters.AddWithValue(
                "$resultMessage",
                DbValue(draft.ResultMessage));
            command.Parameters.AddWithValue(
                "$archiveFileSize",
                draft.Package.FileSize);
            command.Parameters.AddWithValue(
                "$archiveLastWriteUtc",
                FormatUtc(draft.Package.LastWriteUtc));
            command.Parameters.AddWithValue(
                "$archivePath",
                draft.Package.ArchivePath);
            command.Parameters.AddWithValue(
                "$fingerprint",
                draft.Fingerprint);
            analysisId = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        foreach (var entry in draft.Entries)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO PackageAnalysisEntries (
                    AnalysisId, OriginalPath, NormalizedPath,
                    RelativeInstallPath, EntrySize, IsDirectory,
                    IsInstallable, WarningCode)
                VALUES (
                    $analysisId, $originalPath, $normalizedPath,
                    $relativeInstallPath, $entrySize, $isDirectory,
                    $isInstallable, $warningCode);
                """;
            command.Parameters.AddWithValue("$analysisId", analysisId);
            command.Parameters.AddWithValue(
                "$originalPath",
                entry.OriginalPath);
            command.Parameters.AddWithValue(
                "$normalizedPath",
                entry.NormalizedPath);
            command.Parameters.AddWithValue(
                "$relativeInstallPath",
                DbValue(entry.RelativeInstallPath));
            command.Parameters.AddWithValue("$entrySize", entry.EntrySize);
            command.Parameters.AddWithValue(
                "$isDirectory",
                entry.IsDirectory ? 1 : 0);
            command.Parameters.AddWithValue(
                "$isInstallable",
                entry.IsInstallable ? 1 : 0);
            command.Parameters.AddWithValue(
                "$warningCode",
                DbValue(entry.WarningCode));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var root in draft.DetectedRoots.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO PackageAnalysisRoots (AnalysisId, DetectedRoot)
                VALUES ($analysisId, $root);
                """;
            command.Parameters.AddWithValue("$analysisId", analysisId);
            command.Parameters.AddWithValue("$root", root);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        ThrowSaveFaultIf(
            AnalysisSaveFaultPhase.AfterInsertBeforePackageSha,
            cancellationToken);

        // RF-05: keep package ArchiveSha256 aligned with analyzed content identity.
        var contentSha = ArchiveTrustPolicy.CanonicalizeSha256(
            draft.ArchiveSha256);
        if (contentSha is not null)
        {
            await using var updateSha = connection.CreateCommand();
            updateSha.Transaction = (SqliteTransaction)transaction;
            updateSha.CommandText =
                """
                UPDATE Packages
                SET ArchiveSha256 = $sha
                WHERE PackageId = $packageId;
                """;
            updateSha.Parameters.AddWithValue("$sha", contentSha);
            updateSha.Parameters.AddWithValue(
                "$packageId",
                draft.Package.PackageId.Value);
            await updateSha.ExecuteNonQueryAsync(cancellationToken);
        }

        ThrowSaveFaultIf(
            AnalysisSaveFaultPhase.AfterPackageShaBeforeCommit,
            cancellationToken);

        // Last user-cancellable boundary: after this check, commit + load
        // complete without honoring user cancellation (avoids post-commit OCE).
        if (TestOnlyBeforeCommit is not null)
        {
            await TestOnlyBeforeCommit(cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        await transaction.CommitAsync(CancellationToken.None)
            .ConfigureAwait(false);

        if (TestOnlyAfterCommitBeforeReturn is not null)
        {
            try
            {
                await TestOnlyAfterCommitBeforeReturn(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Commit already succeeded; still return the committed record.
            }
        }

        return await LoadAnalysisAsync(
                   connection,
                   draft.Package.PackageId,
                   CancellationToken.None)
               ?? throw new InvalidOperationException(
                   "The saved analysis could not be loaded.");
    }

    private void ThrowSaveFaultIf(
        AnalysisSaveFaultPhase phase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TestOnlySaveAnalysisFaultPhase == phase)
        {
            throw new IOException(
                $"Test-only analysis save fault at {phase}.");
        }
    }

    public async Task<PackageAnalysisRecord?> LoadAnalysisAsync(
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadAnalysisAsync(
            connection,
            packageId,
            cancellationToken);
    }

    public async Task SelectRootAsync(
        PackageId packageId,
        string selectedRoot,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedRoot);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE PackageAnalyses
            SET SelectedRoot = $root,
                State = $ready,
                Fingerprint = $fingerprint
            WHERE PackageId = $packageId
              AND EXISTS (
                  SELECT 1
                  FROM PackageAnalysisRoots
                  WHERE AnalysisId = PackageAnalyses.Id
                    AND DetectedRoot = $root COLLATE NOCASE);
            """;
        command.Parameters.AddWithValue("$root", selectedRoot);
        command.Parameters.AddWithValue(
            "$ready",
            (int)PackageAnalysisState.Ready);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException(
                "The selected root is not available for the current analysis.");
    }

    private static async Task InsertPackageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PackageRecord package,
        DateTime indexedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO Packages (
                PackageId, ArchivePath, IndexedDisplayName, CustomDisplayName,
                IndexedVersion, CustomVersion, Note, GroupId, Source,
                ArchiveFileName, ArchiveFormat, FileSize, LastWriteUtc,
                Author, Category, GameDomain, NexusModId, NexusFileId,
                NexusUrl, ArchiveSha256, MetadataSource,
                MetadataSchemaVersion, HasMetadata, IsPresent, LastIndexedAtUtc,
                ArchiveFamilyKey, NexusFileUuid, DownloadedAtUtc)
            VALUES (
                $packageId, $archivePath, $displayName, NULL, $version, NULL,
                '', NULL, $source, $archiveFileName, $archiveFormat,
                $fileSize, $lastWriteUtc, $author, $category, $gameDomain,
                $nexusModId, $nexusFileId, $nexusUrl, $archiveSha256,
                $metadataSource, $metadataSchemaVersion, $hasMetadata, 1,
                $indexedAtUtc, $archiveFamilyKey, $nexusFileUuid,
                $downloadedAtUtc);
            """;
        AddPackageParameters(command, package);
        command.Parameters.AddWithValue(
            "$indexedAtUtc",
            FormatUtc(indexedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateIndexedPackageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PackageId packageId,
        PackageRecord package,
        DateTime indexedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE Packages
            SET ArchivePath = $archivePath,
                IndexedDisplayName = $displayName,
                IndexedVersion = CASE
                    WHEN length(trim(COALESCE($version, ''))) > 0
                    THEN $version
                    ELSE IndexedVersion
                END,
                Source = $source,
                ArchiveFileName = $archiveFileName,
                ArchiveFormat = $archiveFormat,
                FileSize = $fileSize,
                LastWriteUtc = $lastWriteUtc,
                Author = $author,
                Category = $category,
                GameDomain = $gameDomain,
                NexusModId = $nexusModId,
                NexusFileId = $nexusFileId,
                NexusUrl = $nexusUrl,
                ArchiveFamilyKey = CASE
                    WHEN length(trim(COALESCE($archiveFamilyKey, ''))) > 0
                    THEN $archiveFamilyKey
                    ELSE ArchiveFamilyKey
                END,
                NexusFileUuid = CASE
                    WHEN length(trim(COALESCE($nexusFileUuid, ''))) > 0
                    THEN $nexusFileUuid
                    ELSE NexusFileUuid
                END,
                DownloadedAtUtc = COALESCE($downloadedAtUtc, DownloadedAtUtc),
                ArchiveSha256 = CASE
                    WHEN length(trim(COALESCE($archiveSha256, ''))) > 0
                    THEN $archiveSha256
                    ELSE ArchiveSha256
                END,
                MetadataSource = $metadataSource,
                MetadataSchemaVersion = $metadataSchemaVersion,
                HasMetadata = $hasMetadata,
                IsPresent = 1,
                LastIndexedAtUtc = $indexedAtUtc
            WHERE PackageId = $existingPackageId;
            """;
        AddPackageParameters(command, package);
        command.Parameters.AddWithValue(
            "$existingPackageId",
            packageId.Value);
        command.Parameters.AddWithValue(
            "$indexedAtUtc",
            FormatUtc(indexedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<CatalogRow>> LoadCatalogRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT p.PackageId, p.ArchivePath, p.IndexedDisplayName,
                   p.CustomDisplayName, p.IndexedVersion, p.CustomVersion,
                   p.Note, p.GroupId, p.Source, p.ArchiveFileName,
                   p.ArchiveFormat, p.FileSize, p.LastWriteUtc, p.Author,
                   p.Category, p.GameDomain, p.NexusModId, p.NexusFileId,
                   p.NexusUrl, p.ArchiveSha256, p.MetadataSource,
                   p.MetadataSchemaVersion, p.HasMetadata, p.IsPresent,
                   p.LastIndexedAtUtc, p.InstallationState,
                   a.Fingerprint,
                   EXISTS (
                       SELECT 1 FROM InstalledMods i
                       WHERE i.PackageId = p.PackageId),
                   COALESCE((
                       SELECT group_concat(managed.Value, '|')
                       FROM (
                           SELECT f.RelativeGamePath || ':' ||
                                  f.InstalledContentHash AS Value
                           FROM InstalledFiles f
                           WHERE f.PackageId = p.PackageId
                           ORDER BY f.RelativeGamePath COLLATE NOCASE
                       ) managed
                   ), '')
            FROM Packages p
            LEFT JOIN PackageAnalyses a ON a.PackageId = p.PackageId;
            """;
        var rows = new List<CatalogRow>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CatalogRow(
                new PackageId(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                reader.GetString(6),
                GetNullableInt64(reader, 7),
                (PackageSource)reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt64(11),
                ParseUtc(reader.GetString(12)),
                GetNullableString(reader, 13),
                GetNullableString(reader, 14),
                GetNullableString(reader, 15),
                GetNullableInt64(reader, 16),
                GetNullableInt64(reader, 17),
                GetNullableString(reader, 18),
                GetNullableString(reader, 19),
                reader.GetString(20),
                reader.GetInt32(21),
                reader.GetInt32(22) != 0,
                reader.GetInt32(23) != 0,
                reader.IsDBNull(24)
                    ? null
                    : ParseUtc(reader.GetString(24)),
                (PackageInstallationState)reader.GetInt32(25),
                GetNullableString(reader, 26),
                reader.GetInt32(27) != 0,
                reader.GetString(28)));
        }
        return rows;
    }

    private async Task<int> MergeExactArchiveDuplicatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = await LoadCatalogRowsAsync(
            connection,
            transaction,
            cancellationToken);
        var mergedCount = 0;
        var groups = rows
            .Where(row => row.Identity is { IsFullyConfirmed: true })
            .GroupBy(row => row.Identity!)
            .Where(group => group.Count() > 1)
            .ToArray();

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = group.ToArray();
            var installedStates = candidates
                .Select(row => row.IsInstalled)
                .Distinct()
                .Count();
            var managedFileSets = candidates
                .Select(row => row.ManagedFilesSignature)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (installedStates > 1 || managedFileSets > 1)
            {
                _technicalWarning?.Invoke(
                    "Exact archive duplicate was not merged because " +
                    "installation state or managed files differ: " +
                    string.Join(
                        ", ",
                        candidates.Select(row => row.PackageId.Value)));
                continue;
            }
            var canonical = candidates
                .OrderByDescending(row => row.IsInstalled)
                .ThenByDescending(row => row.IsPresent)
                .ThenByDescending(MetadataScore)
                .ThenByDescending(row => row.LastIndexedAtUtc)
                .ThenByDescending(row => row.LastWriteUtc)
                .First();
            var duplicates = candidates
                .Where(row =>
                    row.PackageId != canonical.PackageId &&
                    !row.IsInstalled)
                .ToArray();
            if (duplicates.Length == 0)
                continue;

            var removableIds = duplicates
                .Select(row => row.PackageId)
                .ToHashSet();
            var archiveSource = candidates
                .Where(row =>
                    row.PackageId == canonical.PackageId ||
                    removableIds.Contains(row.PackageId))
                .OrderByDescending(row => row.IsPresent)
                .ThenByDescending(MetadataScore)
                .ThenByDescending(row => row.LastIndexedAtUtc)
                .ThenByDescending(row => row.LastWriteUtc)
                .First();
            var metadataSource = candidates
                .OrderByDescending(MetadataScore)
                .ThenByDescending(row => row.IsPresent)
                .ThenByDescending(row => row.LastIndexedAtUtc)
                .First();
            var analysisSource = archiveSource.HasAnalysis
                ? archiveSource
                : candidates.FirstOrDefault(row => row.HasAnalysis);

            if (analysisSource is not null &&
                analysisSource.PackageId != canonical.PackageId &&
                removableIds.Contains(analysisSource.PackageId))
            {
                await using var moveAnalysis = connection.CreateCommand();
                moveAnalysis.Transaction = transaction;
                moveAnalysis.CommandText =
                    """
                    DELETE FROM PackageAnalyses
                    WHERE PackageId = $canonical;
                    UPDATE PackageAnalyses
                    SET PackageId = $canonical
                    WHERE PackageId = $source;
                    """;
                moveAnalysis.Parameters.AddWithValue(
                    "$canonical",
                    canonical.PackageId.Value);
                moveAnalysis.Parameters.AddWithValue(
                    "$source",
                    analysisSource.PackageId.Value);
                await moveAnalysis.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var duplicate in duplicates)
            {
                await RewirePackageReferencesAsync(
                    connection,
                    transaction,
                    duplicate.PackageId,
                    canonical.PackageId,
                    cancellationToken);
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText =
                    """
                    DELETE FROM Packages
                    WHERE PackageId = $packageId
                      AND InstallationState = $notInstalled
                      AND NOT EXISTS (
                          SELECT 1 FROM InstalledMods
                          WHERE PackageId = $packageId);
                    """;
                delete.Parameters.AddWithValue(
                    "$packageId",
                    duplicate.PackageId.Value);
                delete.Parameters.AddWithValue(
                    "$notInstalled",
                    (int)PackageInstallationState.NotInstalled);
                mergedCount +=
                    await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await UpdateMergedCanonicalAsync(
                connection,
                transaction,
                canonical,
                archiveSource,
                metadataSource,
                candidates,
                cancellationToken);
        }

        return mergedCount;
    }

    private static async Task UpdateMergedCanonicalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CatalogRow canonical,
        CatalogRow archiveSource,
        CatalogRow metadataSource,
        IReadOnlyList<CatalogRow> candidates,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE Packages
            SET ArchivePath = $archivePath,
                IndexedDisplayName = $indexedDisplayName,
                IndexedVersion = $indexedVersion,
                CustomDisplayName = $customDisplayName,
                CustomVersion = $customVersion,
                Note = $note,
                GroupId = $groupId,
                Source = $source,
                ArchiveFileName = $archiveFileName,
                ArchiveFormat = $archiveFormat,
                FileSize = $fileSize,
                LastWriteUtc = $lastWriteUtc,
                Author = $author,
                Category = $category,
                GameDomain = $gameDomain,
                NexusModId = $nexusModId,
                NexusFileId = $nexusFileId,
                NexusUrl = $nexusUrl,
                ArchiveSha256 = $archiveSha256,
                MetadataSource = $metadataSource,
                MetadataSchemaVersion = $metadataSchemaVersion,
                HasMetadata = $hasMetadata,
                IsPresent = $isPresent,
                LastIndexedAtUtc = $lastIndexedAtUtc
            WHERE PackageId = $packageId;
            """;
        update.Parameters.AddWithValue(
            "$packageId",
            canonical.PackageId.Value);
        update.Parameters.AddWithValue(
            "$archivePath",
            archiveSource.ArchivePath);
        update.Parameters.AddWithValue(
            "$indexedDisplayName",
            metadataSource.IndexedDisplayName);
        update.Parameters.AddWithValue(
            "$indexedVersion",
            DbValue(FirstNonBlank(
                canonical.IndexedVersion,
                candidates.Select(row => row.IndexedVersion))));
        update.Parameters.AddWithValue(
            "$customDisplayName",
            DbValue(FirstNonBlank(
                canonical.CustomDisplayName,
                candidates.Select(row => row.CustomDisplayName))));
        update.Parameters.AddWithValue(
            "$customVersion",
            DbValue(FirstNonBlank(
                canonical.CustomVersion,
                candidates.Select(row => row.CustomVersion))));
        update.Parameters.AddWithValue(
            "$note",
            FirstNonBlank(
                canonical.Note,
                candidates.Select(row => row.Note)) ?? string.Empty);
        update.Parameters.AddWithValue(
            "$groupId",
            DbValue(canonical.GroupId ??
                candidates.Select(row => row.GroupId)
                    .FirstOrDefault(value => value is not null)));
        update.Parameters.AddWithValue("$source", (int)metadataSource.Source);
        update.Parameters.AddWithValue(
            "$archiveFileName",
            archiveSource.ArchiveFileName);
        update.Parameters.AddWithValue(
            "$archiveFormat",
            archiveSource.ArchiveFormat);
        update.Parameters.AddWithValue("$fileSize", archiveSource.FileSize);
        update.Parameters.AddWithValue(
            "$lastWriteUtc",
            FormatUtc(archiveSource.LastWriteUtc));
        update.Parameters.AddWithValue("$author", DbValue(metadataSource.Author));
        update.Parameters.AddWithValue(
            "$category",
            DbValue(metadataSource.Category));
        update.Parameters.AddWithValue(
            "$gameDomain",
            DbValue(metadataSource.GameDomain));
        update.Parameters.AddWithValue(
            "$nexusModId",
            DbValue(metadataSource.NexusModId));
        update.Parameters.AddWithValue(
            "$nexusFileId",
            DbValue(metadataSource.NexusFileId));
        update.Parameters.AddWithValue(
            "$nexusUrl",
            DbValue(metadataSource.NexusUrl));
        update.Parameters.AddWithValue(
            "$archiveSha256",
            DbValue(archiveSource.ArchiveSha256 ??
                archiveSource.AnalysisFingerprint));
        update.Parameters.AddWithValue(
            "$metadataSource",
            metadataSource.MetadataSource);
        update.Parameters.AddWithValue(
            "$metadataSchemaVersion",
            metadataSource.MetadataSchemaVersion);
        update.Parameters.AddWithValue(
            "$hasMetadata",
            metadataSource.HasMetadata ? 1 : 0);
        update.Parameters.AddWithValue(
            "$isPresent",
            archiveSource.IsPresent ? 1 : 0);
        update.Parameters.AddWithValue(
            "$lastIndexedAtUtc",
            DbValue(archiveSource.LastIndexedAtUtc is null
                ? null
                : FormatUtc(archiveSource.LastIndexedAtUtc.Value)));
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RewirePackageReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PackageId source,
        PackageId target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO PackageRelations (
                FromPackageId, ToPackageId, RelationType, Source,
                IsConfirmed, CreatedAtUtc)
            SELECT
                CASE WHEN FromPackageId = $source
                     THEN $target ELSE FromPackageId END,
                CASE WHEN ToPackageId = $source
                     THEN $target ELSE ToPackageId END,
                RelationType, Source, IsConfirmed, CreatedAtUtc
            FROM PackageRelations
            WHERE (FromPackageId = $source OR ToPackageId = $source)
              AND (CASE WHEN FromPackageId = $source
                        THEN $target ELSE FromPackageId END) <>
                  (CASE WHEN ToPackageId = $source
                        THEN $target ELSE ToPackageId END);

            DELETE FROM PackageRelations
            WHERE FromPackageId = $source OR ToPackageId = $source;

            UPDATE InstallOperations
            SET PackageId = $target
            WHERE PackageId = $source;
            """;
        command.Parameters.AddWithValue("$source", source.Value);
        command.Parameters.AddWithValue("$target", target.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> PruneMissingUninstalledAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT PackageId
            FROM Packages
            WHERE IsPresent = 0
              AND InstallationState = $notInstalled
              AND NOT EXISTS (
                  SELECT 1 FROM InstalledMods
                  WHERE InstalledMods.PackageId = Packages.PackageId);
            """;
        select.Parameters.AddWithValue(
            "$notInstalled",
            (int)PackageInstallationState.NotInstalled);
        var packageIds = new List<PackageId>();
        await using (var reader =
                     await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                packageIds.Add(new PackageId(reader.GetString(0)));
        }

        var removed = 0;
        foreach (var packageId in packageIds)
        {
            if (await DeleteMissingUninstalledPackageAsync(
                    connection,
                    transaction,
                    packageId,
                    cancellationToken))
            {
                removed++;
            }
        }
        return removed;
    }

    private static async Task<bool> DeleteMissingUninstalledPackageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PackageId packageId,
        CancellationToken cancellationToken)
    {
        await using (var operations = connection.CreateCommand())
        {
            operations.Transaction = transaction;
            operations.CommandText =
                """
                DELETE FROM InstallOperations
                WHERE PackageId = $packageId
                  AND EXISTS (
                      SELECT 1
                      FROM Packages
                      WHERE PackageId = $packageId
                        AND IsPresent = 0
                        AND InstallationState = $notInstalled)
                  AND NOT EXISTS (
                      SELECT 1 FROM InstalledMods
                      WHERE PackageId = $packageId);
                """;
            operations.Parameters.AddWithValue(
                "$packageId",
                packageId.Value);
            operations.Parameters.AddWithValue(
                "$notInstalled",
                (int)PackageInstallationState.NotInstalled);
            await operations.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var package = connection.CreateCommand();
        package.Transaction = transaction;
        package.CommandText =
            """
            DELETE FROM Packages
            WHERE PackageId = $packageId
              AND IsPresent = 0
              AND InstallationState = $notInstalled
              AND NOT EXISTS (
                  SELECT 1 FROM InstalledMods
                  WHERE PackageId = $packageId);
            """;
        package.Parameters.AddWithValue("$packageId", packageId.Value);
        package.Parameters.AddWithValue(
            "$notInstalled",
            (int)PackageInstallationState.NotInstalled);
        return await package.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static int MetadataScore(CatalogRow row)
    {
        var score = row.HasMetadata ? 8 : 0;
        if (!string.IsNullOrWhiteSpace(row.MetadataSource) &&
            !string.Equals(
                row.MetadataSource,
                PackageMetadataSources.None,
                StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }
        if (!string.IsNullOrWhiteSpace(row.NexusUrl))
            score += 2;
        if (!string.IsNullOrWhiteSpace(row.Author))
            score++;
        if (!string.IsNullOrWhiteSpace(row.Category))
            score++;
        if (!string.IsNullOrWhiteSpace(row.IndexedVersion))
            score++;
        if (!string.IsNullOrWhiteSpace(row.ArchiveSha256))
            score++;
        return score;
    }

    private static ArchiveCatalogIdentity? BuildArchiveIdentity(
        PackageSource source,
        string? gameDomain,
        long? modId,
        long? fileId,
        string? nexusUrl,
        string? sha256,
        string archivePath)
    {
        var normalizedPath = NormalizeArchivePath(archivePath);
        var normalizedHash = string.IsNullOrWhiteSpace(sha256)
            ? null
            : sha256.Trim().ToUpperInvariant();
        if (source == PackageSource.Nexus &&
            modId is not null &&
            fileId is not null)
        {
            var normalizedDomain = string.IsNullOrWhiteSpace(gameDomain)
                ? TryGetNexusGameDomain(nexusUrl)
                : gameDomain.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedDomain))
                return null;
            return new(
                "Nexus",
                normalizedDomain,
                source,
                modId,
                fileId,
                normalizedHash,
                normalizedHash is null ? normalizedPath : null,
                normalizedHash is not null);
        }
        if (source == PackageSource.Local && normalizedHash is not null)
        {
            return new(
                "Local",
                null,
                source,
                null,
                null,
                normalizedHash,
                normalizedPath,
                true);
        }
        return null;
    }

    private static string NormalizeArchivePath(string archivePath) =>
        Path.GetFullPath(archivePath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            .Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar)
            .ToUpperInvariant();

    private static string? TryGetNexusGameDomain(string? nexusUrl)
    {
        if (!Uri.TryCreate(nexusUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith(
                "nexusmods.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var modsIndex = Array.FindIndex(
            segments,
            segment => string.Equals(
                segment,
                "mods",
                StringComparison.OrdinalIgnoreCase));
        return modsIndex > 0
            ? segments[modsIndex - 1].Trim().ToLowerInvariant()
            : null;
    }

    private static string? FirstNonBlank(
        string? preferred,
        IEnumerable<string?> candidates)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;
        return candidates.FirstOrDefault(
            value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record ArchiveCatalogIdentity(
        string Kind,
        string? GameDomain,
        PackageSource Source,
        long? ModId,
        long? FileId,
        string? Sha256,
        string? NormalizedPath,
        bool IsFullyConfirmed);

    private sealed record CatalogRow(
        PackageId PackageId,
        string ArchivePath,
        string IndexedDisplayName,
        string? CustomDisplayName,
        string? IndexedVersion,
        string? CustomVersion,
        string Note,
        long? GroupId,
        PackageSource Source,
        string ArchiveFileName,
        string ArchiveFormat,
        long FileSize,
        DateTime LastWriteUtc,
        string? Author,
        string? Category,
        string? GameDomain,
        long? NexusModId,
        long? NexusFileId,
        string? NexusUrl,
        string? ArchiveSha256,
        string MetadataSource,
        int MetadataSchemaVersion,
        bool HasMetadata,
        bool IsPresent,
        DateTime? LastIndexedAtUtc,
        PackageInstallationState InstallationState,
        string? AnalysisFingerprint,
        bool HasInstalledManifest,
        string ManagedFilesSignature)
    {
        public bool IsInstalled =>
            HasInstalledManifest ||
            InstallationState != PackageInstallationState.NotInstalled;

        public bool HasAnalysis =>
            !string.IsNullOrWhiteSpace(AnalysisFingerprint);

        public ArchiveCatalogIdentity? Identity => BuildArchiveIdentity(
            Source,
            GameDomain,
            NexusModId,
            NexusFileId,
            NexusUrl,
            ArchiveSha256 ?? AnalysisFingerprint,
            ArchivePath);

        public static CatalogRow FromPackage(
            PackageRecord package,
            DateTime indexedAtUtc,
            PackageId? packageId = null,
            bool isInstalled = false,
            string? customDisplayName = null,
            string? customVersion = null,
            string? note = null,
            long? groupId = null,
            string? indexedVersion = null) =>
            new(
                packageId ?? package.PackageId,
                Path.GetFullPath(package.ArchivePath),
                package.DisplayName,
                customDisplayName,
                string.IsNullOrWhiteSpace(package.Version)
                    ? indexedVersion
                    : package.Version,
                customVersion,
                note ?? string.Empty,
                groupId,
                package.Source,
                package.ArchiveFileName,
                package.ArchiveFormat,
                package.FileSize,
                package.LastWriteUtc,
                package.Author,
                package.Category,
                package.GameDomain,
                package.NexusModId,
                package.NexusFileId,
                package.NexusUrl,
                package.Sha256,
                package.MetadataSource,
                package.MetadataSchemaVersion,
                package.HasMetadata,
                true,
                indexedAtUtc,
                isInstalled
                    ? PackageInstallationState.Installed
                    : package.InstallationState,
                null,
                isInstalled,
                string.Empty);
    }

    private async Task<PackageAnalysisRecord?> LoadAnalysisAsync(
        SqliteConnection connection,
        PackageId packageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, AnalyzerVersion, State, DetectedRoot, SelectedRoot,
                   EntryCount, InstallableFileCount, WarningCount,
                   AnalyzedAtUtc, ResultCode, ResultMessage, ArchiveFileSize,
                   ArchiveLastWriteUtc, ArchivePath, Fingerprint
            FROM PackageAnalyses
            WHERE PackageId = $packageId;
            """;
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var id = reader.GetInt64(0);
        var basic = new PackageAnalysisRecord
        {
            Id = id,
            PackageId = packageId,
            AnalyzerVersion = reader.GetInt32(1),
            State = (PackageAnalysisState)reader.GetInt32(2),
            DetectedRoot = GetNullableString(reader, 3),
            SelectedRoot = GetNullableString(reader, 4),
            EntryCount = reader.GetInt32(5),
            InstallableFileCount = reader.GetInt32(6),
            WarningCount = reader.GetInt32(7),
            AnalyzedAtUtc = ParseUtc(reader.GetString(8)),
            ResultCode = GetNullableString(reader, 9),
            ResultMessage = GetNullableString(reader, 10),
            ArchiveFileSize = reader.GetInt64(11),
            ArchiveLastWriteUtc = ParseUtc(reader.GetString(12)),
            ArchivePath = reader.GetString(13),
            Fingerprint = reader.GetString(14)
        };
        await reader.DisposeAsync();

        var roots = new List<string>();
        await using (var rootsCommand = connection.CreateCommand())
        {
            rootsCommand.CommandText =
                """
                SELECT DetectedRoot
                FROM PackageAnalysisRoots
                WHERE AnalysisId = $analysisId
                ORDER BY DetectedRoot COLLATE NOCASE;
                """;
            rootsCommand.Parameters.AddWithValue("$analysisId", id);
            await using var rootsReader =
                await rootsCommand.ExecuteReaderAsync(cancellationToken);
            while (await rootsReader.ReadAsync(cancellationToken))
                roots.Add(rootsReader.GetString(0));
        }

        var entries = new List<ArchiveAnalysisEntry>();
        await using (var entriesCommand = connection.CreateCommand())
        {
            entriesCommand.CommandText =
                """
                SELECT Id, OriginalPath, NormalizedPath, RelativeInstallPath,
                       EntrySize, IsDirectory, IsInstallable, WarningCode
                FROM PackageAnalysisEntries
                WHERE AnalysisId = $analysisId
                ORDER BY Id;
                """;
            entriesCommand.Parameters.AddWithValue("$analysisId", id);
            await using var entriesReader =
                await entriesCommand.ExecuteReaderAsync(cancellationToken);
            while (await entriesReader.ReadAsync(cancellationToken))
            {
                entries.Add(new ArchiveAnalysisEntry
                {
                    Id = entriesReader.GetInt64(0),
                    OriginalPath = entriesReader.GetString(1),
                    NormalizedPath = entriesReader.GetString(2),
                    RelativeInstallPath = GetNullableString(entriesReader, 3),
                    EntrySize = entriesReader.GetInt64(4),
                    IsDirectory = entriesReader.GetInt32(5) != 0,
                    IsInstallable = entriesReader.GetInt32(6) != 0,
                    WarningCode = GetNullableString(entriesReader, 7)
                });
            }
        }

        return basic with
        {
            DetectedRoots = roots,
            Entries = entries
        };
    }

    private async Task<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static void AddPackageParameters(
        SqliteCommand command,
        PackageRecord package)
    {
        command.Parameters.AddWithValue(
            "$packageId",
            package.PackageId.Value);
        command.Parameters.AddWithValue(
            "$archivePath",
            Path.GetFullPath(package.ArchivePath));
        command.Parameters.AddWithValue("$displayName", package.DisplayName);
        command.Parameters.AddWithValue("$version", DbValue(package.Version));
        command.Parameters.AddWithValue("$source", (int)package.Source);
        command.Parameters.AddWithValue(
            "$archiveFileName",
            package.ArchiveFileName);
        command.Parameters.AddWithValue(
            "$archiveFormat",
            package.ArchiveFormat);
        command.Parameters.AddWithValue("$fileSize", package.FileSize);
        command.Parameters.AddWithValue(
            "$lastWriteUtc",
            FormatUtc(package.LastWriteUtc));
        command.Parameters.AddWithValue("$author", DbValue(package.Author));
        command.Parameters.AddWithValue("$category", DbValue(package.Category));
        command.Parameters.AddWithValue(
            "$gameDomain",
            DbValue(package.GameDomain));
        command.Parameters.AddWithValue(
            "$nexusModId",
            DbValue(package.NexusModId));
        command.Parameters.AddWithValue(
            "$nexusFileId",
            DbValue(package.NexusFileId));
        command.Parameters.AddWithValue("$nexusUrl", DbValue(package.NexusUrl));
        command.Parameters.AddWithValue(
            "$archiveFamilyKey",
            DbValue(package.ArchiveFamilyKey));
        command.Parameters.AddWithValue(
            "$nexusFileUuid",
            DbValue(package.NexusFileUuid));
        command.Parameters.AddWithValue(
            "$downloadedAtUtc",
            DbValue(package.DownloadedAtUtc is null
                ? null
                : FormatUtc(package.DownloadedAtUtc.Value)));
        command.Parameters.AddWithValue(
            "$archiveSha256",
            DbValue(package.Sha256));
        command.Parameters.AddWithValue(
            "$metadataSource",
            package.MetadataSource);
        command.Parameters.AddWithValue(
            "$metadataSchemaVersion",
            package.MetadataSchemaVersion);
        command.Parameters.AddWithValue(
            "$hasMetadata",
            package.HasMetadata ? 1 : 0);
    }

    private static string NormalizeGroupName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > 100)
            throw new ArgumentOutOfRangeException(
                nameof(name),
                "Group name cannot exceed 100 characters.");
        return normalized;
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static string? GetNullableString(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string FormatUtc(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
