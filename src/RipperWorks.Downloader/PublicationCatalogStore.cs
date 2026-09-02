using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

internal sealed class PublicationCatalogStore(string catalogDatabasePath)
{
    private readonly string _databasePath =
        Path.GetFullPath(catalogDatabasePath);

    internal async Task<string> EnsureRegisteredAsync(
        DownloaderEntry entry,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await CatalogDatabaseSchema.InitializeAsync(
            connection,
            _databasePath,
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        var intendedRecordId = intent.PublicationId;
        var candidates = await LoadCandidatesAsync(
            connection,
            transaction,
            entry,
            intent,
            intendedRecordId,
            cancellationToken).ConfigureAwait(false);
        if (candidates.Count > 1)
        {
            throw new PublicationConflictException(
                "PublicationCatalogAmbiguous",
                "Multiple catalog rows claim the publication identity.");
        }
        if (candidates.Count == 1)
        {
            var candidate = candidates[0];
            VerifyCandidate(entry, intent, candidate);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return candidate.RecordId;
        }

        await InsertAsync(
            connection,
            transaction,
            entry,
            intent,
            intendedRecordId,
            cancellationToken).ConfigureAwait(false);
        var inserted = await LoadByRecordIdAsync(
            connection,
            transaction,
            intendedRecordId,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException(
                "Catalog registration was not persisted.");
        VerifyCandidate(entry, intent, inserted);
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
        return intendedRecordId;
    }

    internal async Task VerifyRegisteredAsync(
        DownloaderEntry entry,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await CatalogDatabaseSchema.ValidateSupportedVersionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        var recordId = string.IsNullOrWhiteSpace(entry.CatalogId)
            ? intent.PublicationId
            : entry.CatalogId;
        var candidate = await LoadByRecordIdAsync(
            connection,
            null,
            recordId,
            cancellationToken).ConfigureAwait(false) ??
            throw new PublicationConflictException(
                "PublicationCatalogMissing",
                "The publication catalog row is missing.");
        VerifyCandidate(entry, intent, candidate);
    }

    private static async Task<List<CatalogRow>> LoadCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloaderEntry entry,
        PublicationIntent intent,
        string recordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT RecordId, ArchivePath, GameDomain, NexusModId,
                   NexusFileId, FileSize, Fingerprint
            FROM Packages
            WHERE RecordId = $id
               OR ArchivePath = $path COLLATE NOCASE
               OR (
                    $mod IS NOT NULL
                AND lower(GameDomain) = lower($game)
                AND NexusModId = $mod
                AND (
                     ($file IS NOT NULL AND NexusFileId = $file)
                     OR
                     ($file IS NULL AND NexusFileId IS NULL
                      AND upper(Fingerprint) = upper($sha))
                )
               );
            """;
        command.Parameters.AddWithValue("$id", recordId);
        command.Parameters.AddWithValue("$path", intent.ArchivePath);
        command.Parameters.AddWithValue("$game", entry.GameDomain);
        command.Parameters.AddWithValue(
            "$mod",
            entry.NexusModId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$file",
            entry.NexusFileId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$sha", intent.Sha256);
        var rows = new List<CatalogRow>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(Read(reader));
        return rows;
    }

    private static async Task<CatalogRow?> LoadByRecordIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string recordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT RecordId, ArchivePath, GameDomain, NexusModId,
                   NexusFileId, FileSize, Fingerprint
            FROM Packages
            WHERE RecordId = $id;
            """;
        command.Parameters.AddWithValue("$id", recordId);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloaderEntry entry,
        PublicationIntent intent,
        string recordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO Packages (
                RecordId, ArchivePath, DisplayName, Source, Author,
                Category, Version, GameDomain, NexusModId, NexusFileId,
                NexusUrl, FileSize, Fingerprint, UpdatedAt, RegisteredAt)
            VALUES (
                $id, $path, $name, $source, $author, $category, $version,
                $game, $mod, $file, $url, $size, $sha, $updated, $registered);
            """;
        command.Parameters.AddWithValue("$id", recordId);
        command.Parameters.AddWithValue("$path", intent.ArchivePath);
        command.Parameters.AddWithValue("$name", entry.Name);
        command.Parameters.AddWithValue("$source", entry.Source.ToString());
        command.Parameters.AddWithValue("$author", entry.Author);
        command.Parameters.AddWithValue("$category", entry.Category);
        command.Parameters.AddWithValue("$version", entry.Version);
        command.Parameters.AddWithValue("$game", entry.GameDomain);
        command.Parameters.AddWithValue(
            "$mod",
            entry.NexusModId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$file",
            entry.NexusFileId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$url", entry.Url);
        command.Parameters.AddWithValue("$size", intent.Size);
        command.Parameters.AddWithValue("$sha", intent.Sha256);
        command.Parameters.AddWithValue(
            "$updated",
            entry.UpdatedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$registered",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void VerifyCandidate(
        DownloaderEntry entry,
        PublicationIntent intent,
        CatalogRow row)
    {
        if (!string.Equals(
                row.ArchivePath,
                intent.ArchivePath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                row.GameDomain,
                entry.GameDomain,
                StringComparison.OrdinalIgnoreCase) ||
            row.NexusModId != entry.NexusModId ||
            row.NexusFileId != entry.NexusFileId ||
            row.FileSize != intent.Size ||
            !string.Equals(
                row.Fingerprint,
                intent.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PublicationConflictException(
                "PublicationCatalogMismatch",
                "The catalog row contradicts the verified final archive.");
        }
    }

    private static CatalogRow Read(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetString(6));

    private sealed record CatalogRow(
        string RecordId,
        string ArchivePath,
        string GameDomain,
        long? NexusModId,
        long? NexusFileId,
        long FileSize,
        string Fingerprint);
}
