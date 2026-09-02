using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class OrganizerRepository
{

    private async Task SynchronizeLibraryModsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlySet<PackageId> newlyAdded,
        CancellationToken cancellationToken)
    {
        var archives = await LoadArchiveParentsAsync(
            connection,
            transaction,
            cancellationToken);
        var existing = await LoadParentSnapshotsAsync(
            connection,
            transaction,
            cancellationToken);
        var grouped = archives
            .GroupBy(archive => ParentIdentity(archive))
            .ToArray();
        var now = DateTime.UtcNow;

        foreach (var group in grouped)
        {
            var candidates = group
                .OrderByDescending(archive => archive.IsInstalled)
                .ThenByDescending(archive => archive.DownloadedAtUtc)
                .ThenByDescending(archive => archive.LastIndexedAtUtc)
                .ThenByDescending(archive => archive.LastWriteUtc)
                .ToArray();
            var libraryModId = CreateLibraryModId(group.Key);
            existing.TryGetValue(libraryModId, out var saved);
            var newest = candidates[0];
            var installed = candidates.FirstOrDefault(archive =>
                archive.IsInstalled);
            var customNameSource = candidates.FirstOrDefault(archive =>
                !string.IsNullOrWhiteSpace(archive.CustomDisplayName));
            var displaySource = installed ?? newest;
            var groupIds = candidates
                .Select(archive => archive.GroupId)
                .Where(groupId => groupId is not null)
                .Distinct()
                .ToArray();
            if (groupIds.Length > 1 && saved is null)
            {
                _technicalWarning?.Invoke(
                    "LibraryMod migration resolved conflicting groups for " +
                    $"{libraryModId.Value}: " +
                    string.Join(
                        ", ",
                        candidates.Select(archive =>
                            $"{archive.PackageId.Value}:{archive.GroupId}")));
            }

            var selectedGroup = saved?.GroupId ??
                installed?.GroupId ??
                newest.GroupId;
            var archiveIds = candidates
                .Select(archive => archive.PackageId)
                .ToHashSet();
            var preferred = saved?.PreferredArchiveId is { } savedPreferred &&
                            archiveIds.Contains(savedPreferred)
                ? savedPreferred
                : (installed ?? newest).PackageId;
            var added = candidates
                .Where(archive => newlyAdded.Contains(archive.PackageId))
                .OrderByDescending(archive => archive.DownloadedAtUtc)
                .ThenByDescending(archive => archive.LastWriteUtc)
                .FirstOrDefault();
            if (added is not null)
                preferred = added.PackageId;

            await UpsertLibraryModAsync(
                connection,
                transaction,
                new ParentSnapshot(
                    libraryModId,
                    displaySource.IndexedDisplayName,
                    saved?.CustomDisplayName ??
                        customNameSource?.CustomDisplayName,
                    newest.Source,
                    newest.GameDomain,
                    newest.NexusModId,
                    FirstNonBlank(
                        installed?.Author,
                        candidates.Select(archive => archive.Author)),
                    FirstNonBlank(
                        installed?.Category,
                        candidates.Select(archive => archive.Category)),
                    FirstNonBlank(
                        installed?.NexusUrl,
                        candidates.Select(archive => archive.NexusUrl)),
                    selectedGroup,
                    preferred,
                    saved?.CreatedAtUtc ?? now,
                    now),
                cancellationToken);

            foreach (var archive in candidates)
            {
                var familyKey = !string.IsNullOrWhiteSpace(
                    archive.ArchiveFamilyKey)
                    ? archive.ArchiveFamilyKey
                    : !string.IsNullOrWhiteSpace(archive.NexusFileUuid)
                        ? $"nexus-chain:{archive.NexusFileUuid}"
                        : $"archive:{archive.PackageId.Value}";
                await using var assign = connection.CreateCommand();
                assign.Transaction = transaction;
                assign.CommandText =
                    """
                    UPDATE Packages
                    SET LibraryModId = $libraryModId,
                        ArchiveFamilyKey = $familyKey,
                        DownloadedAtUtc = COALESCE(
                            DownloadedAtUtc,
                            LastIndexedAtUtc,
                            LastWriteUtc)
                    WHERE PackageId = $packageId;
                    """;
                assign.Parameters.AddWithValue(
                    "$libraryModId",
                    libraryModId.Value);
                assign.Parameters.AddWithValue("$familyKey", familyKey);
                assign.Parameters.AddWithValue(
                    "$packageId",
                    archive.PackageId.Value);
                await assign.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var migrateRelations = connection.CreateCommand())
        {
            migrateRelations.Transaction = transaction;
            migrateRelations.CommandText =
                """
                INSERT OR IGNORE INTO LibraryModRelations (
                    FromLibraryModId, ToLibraryModId, RelationType, Source,
                    IsConfirmed, CreatedAtUtc)
                SELECT fp.LibraryModId, tp.LibraryModId, r.RelationType,
                       r.Source, r.IsConfirmed, r.CreatedAtUtc
                FROM PackageRelations r
                JOIN Packages fp ON fp.PackageId = r.FromPackageId
                JOIN Packages tp ON tp.PackageId = r.ToPackageId
                WHERE fp.LibraryModId IS NOT NULL
                  AND tp.LibraryModId IS NOT NULL
                  AND fp.LibraryModId <> tp.LibraryModId;

                DELETE FROM LibraryMods
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM Packages p
                    WHERE p.LibraryModId = LibraryMods.LibraryModId);
                """;
            await migrateRelations.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<LibraryModMigrationResult>
        SynchronizeLibraryModsAsync(
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await SynchronizeLibraryModsAsync(
            connection,
            (SqliteTransaction)transaction,
            new HashSet<PackageId>(),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var mods = await LoadLibraryModsAsync(cancellationToken);
        var archiveCount = mods.Sum(mod => mod.Archives.Count);
        return new(
            archiveCount,
            mods.Count,
            archiveCount - mods.Count);
    }

    public async Task<IReadOnlyList<LibraryModRecord>> LoadLibraryModsAsync(
        CancellationToken cancellationToken = default)
    {
        var packages = await LoadPackagesAsync(
            presentOnly: false,
            cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT LibraryModId, IndexedDisplayName, CustomDisplayName,
                   Source, GameDomain, NexusModId, Author, Category,
                   NexusUrl, GroupId, PreferredArchiveId
            FROM LibraryMods
            ORDER BY IndexedDisplayName COLLATE NOCASE;
            """;
        var links = await LoadArchiveFamilyLinksAsync(cancellationToken);
        var packagesByModId = packages
            .Where(p => p.Package.LibraryModId is not null)
            .ToLookup(p => p.Package.LibraryModId!.Value);
        var linksByModId = links
            .ToLookup(l => l.LibraryModId);
        var result = new List<LibraryModRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = new LibraryModId(reader.GetString(0));
            var modArchives = packagesByModId[id]
                .OrderByDescending(package =>
                    package.Package.DownloadedAtUtc ??
                    package.LastIndexedAtUtc ??
                    package.Package.LastWriteUtc)
                .ToArray();
            var modLinks = linksByModId[id].ToArray();
            result.Add(new LibraryModRecord
            {
                LibraryModId = id,
                DisplayName = reader.GetString(1),
                CustomDisplayName = GetNullableString(reader, 2),
                Source = (PackageSource)reader.GetInt32(3),
                GameDomain = GetNullableString(reader, 4),
                NexusModId = GetNullableInt64(reader, 5),
                Author = GetNullableString(reader, 6),
                Category = GetNullableString(reader, 7),
                NexusUrl = GetNullableString(reader, 8),
                GroupId = GetNullableInt64(reader, 9),
                PreferredArchiveId = reader.IsDBNull(10)
                    ? null
                    : new PackageId(reader.GetString(10)),
                Archives = modArchives,
                ArchiveLinks = modLinks
            });
        }
        return result;
    }

    public async Task<ArchiveFamilyReconciliationResult>
        ReconcileArchiveFamiliesAsync(
            IEnumerable<ConfirmedArchiveUpdatePair> confirmedPairs,
            CancellationToken cancellationToken = default)
    {
        var incomingPairs = confirmedPairs
            .Where(pair =>
                pair.CurrentFileId != pair.AvailableFileId &&
                !string.IsNullOrWhiteSpace(pair.GameDomain))
            .DistinctBy(pair => (
                pair.GameDomain.Trim().ToLowerInvariant(),
                pair.NexusModId,
                pair.CurrentFileId,
                pair.AvailableFileId))
            .ToArray();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        foreach (var pair in incomingPairs)
        {
            await using var remember = connection.CreateCommand();
            remember.Transaction = (SqliteTransaction)transaction;
            remember.CommandText =
                """
                INSERT OR IGNORE INTO ConfirmedArchiveUpdatePairs (
                    GameDomain, NexusModId, CurrentFileId,
                    AvailableFileId, Source, ConfirmedAtUtc)
                VALUES ($game, $modId, $currentFileId,
                        $availableFileId, $source, $confirmed);
                """;
            remember.Parameters.AddWithValue(
                "$game",
                pair.GameDomain.Trim());
            remember.Parameters.AddWithValue("$modId", pair.NexusModId);
            remember.Parameters.AddWithValue(
                "$currentFileId",
                pair.CurrentFileId);
            remember.Parameters.AddWithValue(
                "$availableFileId",
                pair.AvailableFileId);
            remember.Parameters.AddWithValue("$source", pair.Source);
            remember.Parameters.AddWithValue(
                "$confirmed",
                FormatUtc(DateTime.UtcNow));
            await remember.ExecuteNonQueryAsync(cancellationToken);
        }
        var pairs = new List<ConfirmedArchiveUpdatePair>();
        await using (var loadPairs = connection.CreateCommand())
        {
            loadPairs.Transaction = (SqliteTransaction)transaction;
            loadPairs.CommandText =
                """
                SELECT GameDomain, NexusModId, CurrentFileId,
                       AvailableFileId, Source
                FROM ConfirmedArchiveUpdatePairs;
                """;
            await using var pairReader =
                await loadPairs.ExecuteReaderAsync(cancellationToken);
            while (await pairReader.ReadAsync(cancellationToken))
            {
                pairs.Add(new(
                    pairReader.GetString(0),
                    pairReader.GetInt64(1),
                    pairReader.GetInt64(2),
                    pairReader.GetInt64(3),
                    pairReader.GetString(4)));
            }
        }
        var applied = 0;
        var updatedArchives = new HashSet<PackageId>();
        foreach (var pair in pairs)
        {
            await using var find = connection.CreateCommand();
            find.Transaction = (SqliteTransaction)transaction;
            find.CommandText =
                """
                SELECT current.PackageId, available.PackageId,
                       current.LibraryModId,
                       current.ArchiveFamilyKey,
                       available.ArchiveFamilyKey
                FROM Packages current
                JOIN Packages available
                  ON available.LibraryModId = current.LibraryModId
                WHERE current.Source = $nexus
                  AND current.GameDomain = $game COLLATE NOCASE
                  AND current.NexusModId = $modId
                  AND current.NexusFileId = $currentFileId
                  AND available.NexusFileId = $availableFileId
                LIMIT 1;
                """;
            find.Parameters.AddWithValue(
                "$nexus",
                (int)PackageSource.Nexus);
            find.Parameters.AddWithValue(
                "$game",
                pair.GameDomain.Trim());
            find.Parameters.AddWithValue("$modId", pair.NexusModId);
            find.Parameters.AddWithValue(
                "$currentFileId",
                pair.CurrentFileId);
            find.Parameters.AddWithValue(
                "$availableFileId",
                pair.AvailableFileId);
            await using var reader =
                await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                continue;
            var currentId = new PackageId(reader.GetString(0));
            var availableId = new PackageId(reader.GetString(1));
            var libraryModId = new LibraryModId(reader.GetString(2));
            var currentFamily = GetNullableString(reader, 3);
            var availableFamily = GetNullableString(reader, 4);
            if (HasConflictingStableFamilies(
                    currentFamily,
                    availableFamily))
            {
                throw new InvalidOperationException(
                    "Confirmed archive update pair has conflicting stable " +
                    "family keys.");
            }
            var stableFamily = StableFamily(
                pair,
                currentFamily,
                availableFamily);
            await reader.DisposeAsync();

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText =
                    """
                    UPDATE Packages
                    SET ArchiveFamilyKey = $family
                    WHERE PackageId IN ($currentId, $availableId)
                      AND (ArchiveFamilyKey IS NULL OR
                           ArchiveFamilyKey <> $family);
                    """;
                update.Parameters.AddWithValue("$family", stableFamily);
                update.Parameters.AddWithValue(
                    "$currentId",
                    currentId.Value);
                update.Parameters.AddWithValue(
                    "$availableId",
                    availableId.Value);
                if (await update.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    updatedArchives.Add(currentId);
                    updatedArchives.Add(availableId);
                }
            }
            await using (var link = connection.CreateCommand())
            {
                link.Transaction = (SqliteTransaction)transaction;
                link.CommandText =
                    """
                    INSERT OR IGNORE INTO ArchiveFamilyLinks (
                        LibraryModId, FromArchiveId, ToArchiveId,
                        Source, CreatedAtUtc)
                    VALUES ($mod, $from, $to, $source, $created);
                    """;
                link.Parameters.AddWithValue(
                    "$mod",
                    libraryModId.Value);
                link.Parameters.AddWithValue("$from", currentId.Value);
                link.Parameters.AddWithValue("$to", availableId.Value);
                link.Parameters.AddWithValue("$source", pair.Source);
                link.Parameters.AddWithValue(
                    "$created",
                    FormatUtc(DateTime.UtcNow));
                await link.ExecuteNonQueryAsync(cancellationToken);
            }
            applied++;
        }
        await transaction.CommitAsync(cancellationToken);
        return new(applied, updatedArchives.Count);
    }

    public async Task<ArchiveVersionOperation> GetArchiveOperationAsync(
        PackageId installedArchiveId,
        PackageId targetArchiveId,
        CancellationToken cancellationToken = default)
    {
        var mod = (await LoadLibraryModsAsync(cancellationToken))
            .FirstOrDefault(candidate =>
                candidate.Archives.Any(archive =>
                    archive.Package.PackageId == installedArchiveId) &&
                candidate.Archives.Any(archive =>
                    archive.Package.PackageId == targetArchiveId));
        return mod?.GetArchiveOperation(
                   installedArchiveId,
                   targetArchiveId) ??
               ArchiveVersionOperation.Install;
    }

    private async Task<IReadOnlyList<ArchiveFamilyLinkRecord>>
        LoadArchiveFamilyLinksAsync(
            CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT LibraryModId, FromArchiveId, ToArchiveId, Source
            FROM ArchiveFamilyLinks;
            """;
        var values = new List<ArchiveFamilyLinkRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new(
                new LibraryModId(reader.GetString(0)),
                new PackageId(reader.GetString(1)),
                new PackageId(reader.GetString(2)),
                reader.GetString(3)));
        }
        return values;
    }

    private static string StableFamily(
        ConfirmedArchiveUpdatePair pair,
        string? currentFamily,
        string? availableFamily)
    {
        var stable = StableFamilies(currentFamily, availableFamily)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(stable))
            return stable;
        var identity =
            $"{pair.GameDomain.Trim().ToLowerInvariant()}|" +
            $"{pair.NexusModId}|{pair.CurrentFileId}|" +
            $"{pair.AvailableFileId}";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..20];
        return $"nexus-chain:{pair.GameDomain.Trim().ToLowerInvariant()}:" +
               $"{pair.NexusModId}:{hash}";
    }

    private static bool HasConflictingStableFamilies(
        string? currentFamily,
        string? availableFamily) =>
        StableFamilies(currentFamily, availableFamily)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .Any();

    private static IEnumerable<string> StableFamilies(
        string? currentFamily,
        string? availableFamily) =>
        new[] { currentFamily, availableFamily }
            .Where(value =>
                !string.IsNullOrWhiteSpace(value) &&
                !value.StartsWith(
                    "archive:",
                    StringComparison.OrdinalIgnoreCase))
            .Select(value => value!);

    public async Task SetPreferredArchiveAsync(
        LibraryModId libraryModId,
        PackageId archiveId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE LibraryMods
            SET PreferredArchiveId = $archiveId,
                UpdatedAtUtc = $updated
            WHERE LibraryModId = $libraryModId
              AND EXISTS (
                  SELECT 1 FROM Packages
                  WHERE PackageId = $archiveId
                    AND LibraryModId = $libraryModId);
            """;
        command.Parameters.AddWithValue("$archiveId", archiveId.Value);
        command.Parameters.AddWithValue("$libraryModId", libraryModId.Value);
        command.Parameters.AddWithValue("$updated", FormatUtc(DateTime.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException(
                "The archive does not belong to this library mod.");
    }

    public async Task UpdateLibraryModDetailsAsync(
        LibraryModId libraryModId,
        string? displayName,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                UPDATE LibraryMods
                SET CustomDisplayName = $displayName,
                    GroupId = $groupId,
                    UpdatedAtUtc = $updated
                WHERE LibraryModId = $libraryModId;
                """;
            command.Parameters.AddWithValue(
                "$displayName",
                DbValue(displayName));
            command.Parameters.AddWithValue("$groupId", DbValue(groupId));
            command.Parameters.AddWithValue(
                "$updated",
                FormatUtc(DateTime.UtcNow));
            command.Parameters.AddWithValue(
                "$libraryModId",
                libraryModId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var packages = connection.CreateCommand())
        {
            packages.Transaction = (SqliteTransaction)transaction;
            packages.CommandText =
                """
                UPDATE Packages
                SET GroupId = $groupId
                WHERE LibraryModId = $libraryModId;
                """;
            packages.Parameters.AddWithValue("$groupId", DbValue(groupId));
            packages.Parameters.AddWithValue(
                "$libraryModId",
                libraryModId.Value);
            await packages.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ArchiveRecordDeletionResult>
        DeleteLibraryArchiveRecordAsync(
            PackageId archiveId,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        string? libraryModId;
        int installationState;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText =
                """
                SELECT LibraryModId, InstallationState
                FROM Packages
                WHERE PackageId = $archiveId;
                """;
            select.Parameters.AddWithValue(
                "$archiveId",
                archiveId.Value);
            await using var reader =
                await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, false, false);
            }
            libraryModId = GetNullableString(reader, 0);
            installationState = reader.GetInt32(1);
        }
        if (installationState !=
            (int)PackageInstallationState.NotInstalled)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, false, false);
        }

        var parentHadRelations = false;
        if (!string.IsNullOrWhiteSpace(libraryModId))
        {
            await using var relationCheck = connection.CreateCommand();
            relationCheck.Transaction = (SqliteTransaction)transaction;
            relationCheck.CommandText =
                """
                SELECT EXISTS(
                    SELECT 1 FROM LibraryModRelations
                    WHERE FromLibraryModId = $id
                       OR ToLibraryModId = $id);
                """;
            relationCheck.Parameters.AddWithValue("$id", libraryModId);
            parentHadRelations = Convert.ToInt64(
                await relationCheck.ExecuteScalarAsync(cancellationToken)) != 0;
        }

        await using (var operations = connection.CreateCommand())
        {
            operations.Transaction = (SqliteTransaction)transaction;
            operations.CommandText =
                "DELETE FROM InstallOperations WHERE PackageId = $archiveId;";
            operations.Parameters.AddWithValue(
                "$archiveId",
                archiveId.Value);
            await operations.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var package = connection.CreateCommand())
        {
            package.Transaction = (SqliteTransaction)transaction;
            package.CommandText =
                """
                DELETE FROM Packages
                WHERE PackageId = $archiveId
                  AND InstallationState = $notInstalled
                  AND NOT EXISTS (
                      SELECT 1 FROM InstalledMods
                      WHERE PackageId = $archiveId);
                """;
            package.Parameters.AddWithValue(
                "$archiveId",
                archiveId.Value);
            package.Parameters.AddWithValue(
                "$notInstalled",
                (int)PackageInstallationState.NotInstalled);
            if (await package.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, false, parentHadRelations);
            }
        }

        var parentRemoved = false;
        if (!string.IsNullOrWhiteSpace(libraryModId))
        {
            await using (var preferred = connection.CreateCommand())
            {
                preferred.Transaction = (SqliteTransaction)transaction;
                preferred.CommandText =
                    """
                    UPDATE LibraryMods
                    SET PreferredArchiveId = (
                            SELECT PackageId
                            FROM Packages
                            WHERE LibraryModId = $id
                            ORDER BY
                                CASE WHEN InstallationState = $installed
                                     THEN 0 ELSE 1 END,
                                COALESCE(
                                    DownloadedAtUtc,
                                    LastIndexedAtUtc,
                                    LastWriteUtc) DESC
                            LIMIT 1),
                        UpdatedAtUtc = $updated
                    WHERE LibraryModId = $id
                      AND PreferredArchiveId = $archiveId;
                    """;
                preferred.Parameters.AddWithValue("$id", libraryModId);
                preferred.Parameters.AddWithValue(
                    "$archiveId",
                    archiveId.Value);
                preferred.Parameters.AddWithValue(
                    "$installed",
                    (int)PackageInstallationState.Installed);
                preferred.Parameters.AddWithValue(
                    "$updated",
                    FormatUtc(DateTime.UtcNow));
                await preferred.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var parent = connection.CreateCommand();
            parent.Transaction = (SqliteTransaction)transaction;
            parent.CommandText =
                """
                DELETE FROM LibraryMods
                WHERE LibraryModId = $id
                  AND NOT EXISTS (
                      SELECT 1 FROM Packages
                      WHERE LibraryModId = $id);
                """;
            parent.Parameters.AddWithValue("$id", libraryModId);
            parentRemoved =
                await parent.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        await transaction.CommitAsync(cancellationToken);
        return new(true, parentRemoved, parentHadRelations);
    }

    private async Task<List<ArchiveParentSnapshot>> LoadArchiveParentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT PackageId, Source, GameDomain, NexusModId, NexusUrl,
                   IndexedDisplayName, CustomDisplayName, Author, Category,
                   GroupId, InstallationState, LastIndexedAtUtc,
                   LastWriteUtc, LibraryModId, ArchiveFamilyKey,
                   NexusFileUuid, DownloadedAtUtc
            FROM Packages;
            """;
        var result = new List<ArchiveParentSnapshot>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                new PackageId(reader.GetString(0)),
                (PackageSource)reader.GetInt32(1),
                GetNullableString(reader, 2),
                GetNullableInt64(reader, 3),
                GetNullableString(reader, 4),
                reader.GetString(5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                GetNullableInt64(reader, 9),
                (PackageInstallationState)reader.GetInt32(10) ==
                PackageInstallationState.Installed,
                reader.IsDBNull(11)
                    ? null
                    : ParseUtc(reader.GetString(11)),
                ParseUtc(reader.GetString(12)),
                GetNullableString(reader, 13),
                GetNullableString(reader, 14),
                GetNullableString(reader, 15),
                reader.IsDBNull(16)
                    ? null
                    : ParseUtc(reader.GetString(16))));
        }
        return result;
    }

    private static async Task<Dictionary<LibraryModId, ParentSnapshot>>
        LoadParentSnapshotsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT LibraryModId, IndexedDisplayName, CustomDisplayName,
                   Source, GameDomain, NexusModId, Author, Category,
                   NexusUrl, GroupId, PreferredArchiveId, CreatedAtUtc,
                   UpdatedAtUtc
            FROM LibraryMods;
            """;
        var result = new Dictionary<LibraryModId, ParentSnapshot>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var snapshot = new ParentSnapshot(
                new LibraryModId(reader.GetString(0)),
                reader.GetString(1),
                GetNullableString(reader, 2),
                (PackageSource)reader.GetInt32(3),
                GetNullableString(reader, 4),
                GetNullableInt64(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                GetNullableInt64(reader, 9),
                reader.IsDBNull(10)
                    ? null
                    : new PackageId(reader.GetString(10)),
                ParseUtc(reader.GetString(11)),
                ParseUtc(reader.GetString(12)));
            result[snapshot.LibraryModId] = snapshot;
        }
        return result;
    }

    private static async Task UpsertLibraryModAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParentSnapshot parent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO LibraryMods (
                LibraryModId, IndexedDisplayName, CustomDisplayName, Source,
                GameDomain, NexusModId, Author, Category, NexusUrl, GroupId,
                PreferredArchiveId, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                $id, $name, $customName, $source, $game, $mod, $author,
                $category, $url, $group, $preferred, $created, $updated)
            ON CONFLICT(LibraryModId) DO UPDATE SET
                IndexedDisplayName = excluded.IndexedDisplayName,
                Source = excluded.Source,
                GameDomain = excluded.GameDomain,
                NexusModId = excluded.NexusModId,
                Author = excluded.Author,
                Category = excluded.Category,
                NexusUrl = excluded.NexusUrl,
                PreferredArchiveId = excluded.PreferredArchiveId,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$id", parent.LibraryModId.Value);
        command.Parameters.AddWithValue("$name", parent.IndexedDisplayName);
        command.Parameters.AddWithValue(
            "$customName",
            DbValue(parent.CustomDisplayName));
        command.Parameters.AddWithValue("$source", (int)parent.Source);
        command.Parameters.AddWithValue("$game", DbValue(parent.GameDomain));
        command.Parameters.AddWithValue("$mod", DbValue(parent.NexusModId));
        command.Parameters.AddWithValue("$author", DbValue(parent.Author));
        command.Parameters.AddWithValue("$category", DbValue(parent.Category));
        command.Parameters.AddWithValue("$url", DbValue(parent.NexusUrl));
        command.Parameters.AddWithValue("$group", DbValue(parent.GroupId));
        command.Parameters.AddWithValue(
            "$preferred",
            DbValue(parent.PreferredArchiveId?.Value));
        command.Parameters.AddWithValue(
            "$created",
            FormatUtc(parent.CreatedAtUtc));
        command.Parameters.AddWithValue(
            "$updated",
            FormatUtc(parent.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ParentIdentity(ArchiveParentSnapshot archive)
    {
        if (archive.Source == PackageSource.Nexus &&
            archive.NexusModId is { } modId)
        {
            var domain = string.IsNullOrWhiteSpace(archive.GameDomain)
                ? TryGetNexusGameDomain(archive.NexusUrl)
                : archive.GameDomain.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(domain))
                return $"nexus|{(int)archive.Source}|{domain}|{modId}";
        }
        return $"archive|{archive.PackageId.Value}";
    }

    private static LibraryModId CreateLibraryModId(string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new LibraryModId(
            "mod-" + Convert.ToHexString(hash).ToLowerInvariant()[..24]);
    }

    private sealed record ArchiveParentSnapshot(
        PackageId PackageId,
        PackageSource Source,
        string? GameDomain,
        long? NexusModId,
        string? NexusUrl,
        string IndexedDisplayName,
        string? CustomDisplayName,
        string? Author,
        string? Category,
        long? GroupId,
        bool IsInstalled,
        DateTime? LastIndexedAtUtc,
        DateTime LastWriteUtc,
        string? ExistingLibraryModId,
        string? ArchiveFamilyKey,
        string? NexusFileUuid,
        DateTime? DownloadedAtUtc);

    private sealed record ParentSnapshot(
        LibraryModId LibraryModId,
        string IndexedDisplayName,
        string? CustomDisplayName,
        PackageSource Source,
        string? GameDomain,
        long? NexusModId,
        string? Author,
        string? Category,
        string? NexusUrl,
        long? GroupId,
        PackageId? PreferredArchiveId,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);
}
