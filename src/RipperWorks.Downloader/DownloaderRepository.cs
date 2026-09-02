using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed class DownloaderRepository : IDownloaderRepository
{
    public const int CurrentSchemaVersion = DownloaderDatabaseSchema.CurrentVersion;

    private const string EntryProjection =
        """
        EntryId, Number, IsSelected, Name, Category, Source, Author, Url,
        GameDomain, NexusModId, NexusFileId, NexusFileUuid, Version, Status, Progress,
        BytesPerSecond, Size, LocalArchivePath, TemporaryPath, Error,
        Description, UpdatedAt, CreatedAt, AdditionalUrl, DownloadOrder,
        CatalogId, DecisionGroup, Sha256, ArchiveFileName,
        UpdateCheckStatus, AvailableVersion, AvailableFileId,
        AvailableFileUuid, LastUpdateCheckUtc, UpdateCheckMessage
        """;

    private const string UpsertSql =
        """
        INSERT INTO DownloaderEntries (
            EntryId, Number, IsSelected, Name, Category, Source, Author, Url, GameDomain, NexusModId, NexusFileId,
            NexusFileUuid, Version, Status, Progress, BytesPerSecond, Size, LocalArchivePath, TemporaryPath, Error, Description,
            UpdatedAt, CreatedAt, AdditionalUrl, DownloadOrder, CatalogId, DecisionGroup, Sha256, ArchiveFileName,
            UpdateCheckStatus, AvailableVersion, AvailableFileId, AvailableFileUuid, LastUpdateCheckUtc, UpdateCheckMessage)
        VALUES (
            $id, $number, $selected, $name, $category, $source, $author, $url, $game, $mod, $file, $fileUuid, $version, $status,
            $progress, $speed, $size, $archive, $temporary, $error, $description, $updated, $created, $additionalUrl,
            $downloadOrder, $catalogId, $decisionGroup, $sha256, $archiveFileName, $updateCheckStatus, $availableVersion,
            $availableFileId, $availableFileUuid, $lastUpdateCheckUtc, $updateCheckMessage)
        ON CONFLICT(EntryId) DO UPDATE SET
            Number=$number, IsSelected=$selected, Name=$name, Category=$category, Source=$source, Author=$author,
            Url=$url, GameDomain=$game, NexusModId=$mod, NexusFileId=$file, NexusFileUuid=$fileUuid, Version=$version, Status=$status,
            Progress=$progress, BytesPerSecond=$speed, Size=$size, LocalArchivePath=$archive, TemporaryPath=$temporary,
            Error=$error, Description=$description, UpdatedAt=$updated, AdditionalUrl=$additionalUrl, DownloadOrder=$downloadOrder,
            CatalogId=$catalogId, DecisionGroup=$decisionGroup, Sha256=$sha256, ArchiveFileName=$archiveFileName,
            UpdateCheckStatus=$updateCheckStatus, AvailableVersion=$availableVersion, AvailableFileId=$availableFileId,
            AvailableFileUuid=$availableFileUuid, LastUpdateCheckUtc=$lastUpdateCheckUtc, UpdateCheckMessage=$updateCheckMessage;
        """;

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public DownloaderRepository(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath { get; }
    private string ConnectionString { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await WriteAsync(connection => DownloaderDatabaseSchema.InitializeAsync(connection, DatabasePath, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<DownloaderEntry>> LoadEntriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + EntryProjection + " FROM DownloaderEntries ORDER BY Number, CreatedAt;";
        var result = new List<DownloaderEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return result;
        var ordinals = new EntryOrdinals(reader);
        do { result.Add(ReadEntry(reader, in ordinals)); } while (await reader.ReadAsync(cancellationToken));
        return result;
    }

    public async Task<DownloaderEntry?> LoadEntryAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + EntryProjection + " FROM DownloaderEntries WHERE EntryId=$id;";
        command.Parameters.AddWithValue("$id", entryId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var ordinals = new EntryOrdinals(reader);
        return ReadEntry(reader, in ordinals);
    }

    public Task SaveEntryAsync(DownloaderEntry entry, CancellationToken cancellationToken = default) =>
        WriteAsync(connection => UpsertEntryAsync(connection, entry, null, cancellationToken), cancellationToken);

    public Task DeleteEntryAsync(Guid entryId, CancellationToken cancellationToken = default) =>
        WriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM DownloaderEntries WHERE EntryId=$id;";
            command.Parameters.AddWithValue("$id", entryId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task DeleteEntriesAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken cancellationToken = default) =>
        WriteAsync(async connection =>
        {
            if (entryIds.Count == 0) return;
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await DeleteEntriesCoreAsync(connection, (SqliteTransaction)transaction, entryIds, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

    public Task<DownloaderImportCommitResult> ApplyImportAsync(
        IReadOnlyList<DownloaderEntry> entries,
        bool replaceCatalog,
        CancellationToken cancellationToken = default) =>
        WriteResultAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var sqliteTx = (SqliteTransaction)transaction;
            var existing = await LoadEntriesAsync(connection, sqliteTx, cancellationToken);
            var byKey = existing.GroupBy(DownloaderImportIdentity.GetKey).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var touched = new HashSet<Guid>();
            var added = 0;
            var updated = 0;
            var unchanged = 0;

            await using var upsertCmd = connection.CreateCommand();
            upsertCmd.Transaction = sqliteTx;
            upsertCmd.CommandText = UpsertSql;
            var binder = new UpsertCommandBinder(upsertCmd);

            foreach (var candidate in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = DownloaderImportIdentity.GetKey(candidate);
                if (byKey.TryGetValue(key, out var current))
                {
                    touched.Add(current.Id);
                    if (Merge(current, candidate))
                    {
                        updated++;
                        binder.Bind(current);
                        await upsertCmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                    else { unchanged++; }
                    continue;
                }
                added++;
                touched.Add(candidate.Id);
                byKey[key] = candidate;
                binder.Bind(candidate);
                await upsertCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            var deleteIds = replaceCatalog ? existing.Where(entry => !touched.Contains(entry.Id)).Select(entry => entry.Id).ToArray() : [];
            await DeleteEntriesCoreAsync(connection, sqliteTx, deleteIds, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var finalTotal = replaceCatalog ? touched.Count : existing.Count + added;
            return new DownloaderImportCommitResult(added, updated, unchanged, deleteIds.Length, finalTotal);
        }, cancellationToken);

    public async Task<IReadOnlyList<DownloaderDownloadRecord>> LoadDownloadsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DownloaderDownloads ORDER BY CreatedAt;";
        var result = new List<DownloaderDownloadRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DownloaderDownloadRecord
            {
                DownloadId = Guid.ParseExact(reader.GetString(0), "N"),
                EntryId = Guid.ParseExact(reader.GetString(1), "N"),
                SourceUrl = reader.GetString(2),
                TemporaryPath = reader.GetString(3),
                BytesDownloaded = reader.GetInt64(4),
                TotalBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Status = (DownloaderStatus)reader.GetInt32(6),
                Error = reader.GetString(7),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(8))
            });
        }
        return result;
    }

    public Task SaveDownloadAsync(DownloaderDownloadRecord download, CancellationToken cancellationToken = default) =>
        WriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO DownloaderDownloads (
                    DownloadId, EntryId, SourceUrl, TemporaryPath,
                    BytesDownloaded, TotalBytes, Status, Error, CreatedAt)
                VALUES (
                    $id, $entry, $url, $path, $bytes, $total,
                    $status, $error, $created)
                ON CONFLICT(EntryId) DO UPDATE SET
                    SourceUrl=$url, TemporaryPath=$path,
                    BytesDownloaded=$bytes, TotalBytes=$total,
                    Status=$status, Error=$error;
                """;
            Add(command, "$id", download.DownloadId.ToString("N"));
            Add(command, "$entry", download.EntryId.ToString("N"));
            Add(command, "$url", download.SourceUrl);
            Add(command, "$path", download.TemporaryPath);
            Add(command, "$bytes", download.BytesDownloaded);
            Add(command, "$total", download.TotalBytes);
            Add(command, "$status", (int)download.Status);
            Add(command, "$error", download.Error);
            Add(command, "$created", download.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task WriteAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await operation(connection);
        }
        finally { _writeGate.Release(); }
    }

    private async Task<T> WriteResultAsync<T>(Func<SqliteConnection, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await operation(connection);
        }
        finally { _writeGate.Release(); }
    }

    private static DownloaderEntry ReadEntry(SqliteDataReader reader, in EntryOrdinals ord)
    {
        return new()
        {
            Id = Guid.ParseExact(reader.GetString(ord.EntryId), "N"),
            Number = reader.GetInt32(ord.Number),
            IsSelected = reader.GetInt32(ord.IsSelected) != 0,
            Name = reader.GetString(ord.Name),
            Category = reader.GetString(ord.Category),
            Source = (DownloaderSource)reader.GetInt32(ord.Source),
            Author = reader.GetString(ord.Author),
            Url = reader.GetString(ord.Url),
            GameDomain = reader.GetString(ord.GameDomain),
            NexusModId = reader.IsDBNull(ord.NexusModId) ? null : reader.GetInt64(ord.NexusModId),
            NexusFileId = reader.IsDBNull(ord.NexusFileId) ? null : reader.GetInt64(ord.NexusFileId),
            NexusFileUuid = reader.GetString(ord.NexusFileUuid),
            Version = reader.GetString(ord.Version),
            Status = (DownloaderStatus)reader.GetInt32(ord.Status),
            Progress = reader.GetDouble(ord.Progress),
            BytesPerSecond = reader.GetDouble(ord.BytesPerSecond),
            Size = reader.GetInt64(ord.Size),
            LocalArchivePath = reader.GetString(ord.LocalArchivePath),
            TemporaryPath = reader.GetString(ord.TemporaryPath),
            Error = reader.GetString(ord.Error),
            Description = reader.GetString(ord.Description),
            UpdatedAt = reader.IsDBNull(ord.UpdatedAt) ? null : DateTimeOffset.Parse(reader.GetString(ord.UpdatedAt)),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(ord.CreatedAt)),
            AdditionalUrl = reader.GetString(ord.AdditionalUrl),
            DownloadOrder = reader.IsDBNull(ord.DownloadOrder) ? null : reader.GetInt32(ord.DownloadOrder),
            CatalogId = reader.GetString(ord.CatalogId),
            DecisionGroup = reader.GetString(ord.DecisionGroup),
            Sha256 = reader.GetString(ord.Sha256),
            ArchiveFileName = reader.GetString(ord.ArchiveFileName),
            UpdateCheckStatus = (NexusUpdateCheckStatus)reader.GetInt32(ord.UpdateCheckStatus),
            AvailableVersion = reader.GetString(ord.AvailableVersion),
            AvailableFileId = reader.IsDBNull(ord.AvailableFileId) ? null : reader.GetInt64(ord.AvailableFileId),
            AvailableFileUuid = reader.GetString(ord.AvailableFileUuid),
            LastUpdateCheckUtc = reader.IsDBNull(ord.LastUpdateCheckUtc) ? null : DateTimeOffset.Parse(reader.GetString(ord.LastUpdateCheckUtc)),
            UpdateCheckMessage = reader.GetString(ord.UpdateCheckMessage)
        };
    }

    private static async Task UpsertEntryAsync(
        SqliteConnection connection,
        DownloaderEntry entry,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;
        var binder = new UpsertCommandBinder(command);
        binder.Bind(entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<DownloaderEntry>> LoadEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + EntryProjection + " FROM DownloaderEntries ORDER BY Number, CreatedAt;";
        var result = new List<DownloaderEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return result;
        var ordinals = new EntryOrdinals(reader);
        do { result.Add(ReadEntry(reader, in ordinals)); } while (await reader.ReadAsync(cancellationToken));
        return result;
    }

    private static async Task DeleteEntriesCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<Guid> entryIds,
        CancellationToken cancellationToken)
    {
        if (entryIds.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = entryIds.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText = $"DELETE FROM DownloaderEntries WHERE EntryId IN ({string.Join(",", names)});";
        var index = 0;
        foreach (var id in entryIds) command.Parameters.AddWithValue(names[index++], id.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool Merge(DownloaderEntry target, DownloaderEntry source)
    {
        var changed = false;
        changed |= SetIfPresent(target.Name, source.Name, value => target.Name = value);
        changed |= SetIfPresent(target.Category, source.Category, value => target.Category = value);
        changed |= SetIfPresent(target.Author, source.Author, value => target.Author = value);
        changed |= SetIfPresent(target.Version, source.Version, value => target.Version = value);
        changed |= SetIfPresent(target.AdditionalUrl, source.AdditionalUrl, value => target.AdditionalUrl = value);
        changed |= SetIfPresent(target.CatalogId, source.CatalogId, value => target.CatalogId = value);
        changed |= SetIfPresent(target.DecisionGroup, source.DecisionGroup, value => target.DecisionGroup = value);
        changed |= SetIfPresent(target.Sha256, source.Sha256, value => target.Sha256 = value);
        changed |= SetIfPresent(target.ArchiveFileName, source.ArchiveFileName, value => target.ArchiveFileName = value);

        if (source.DownloadOrder is not null && source.DownloadOrder != target.DownloadOrder)
        {
            target.DownloadOrder = source.DownloadOrder;
            changed = true;
        }
        if (target.Source != source.Source)
        {
            target.Source = source.Source;
            changed = true;
        }
        if (target.Status is not DownloaderStatus.Downloaded && target.Status != source.Status)
        {
            target.Status = source.Status;
            changed = true;
        }
        return changed;
    }

    private static bool SetIfPresent(string current, string candidate, Action<string> setter)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.Equals(current, candidate, StringComparison.Ordinal)) return false;
        setter(candidate);
        return true;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private readonly struct EntryOrdinals
    {
        public readonly int EntryId; public readonly int Number; public readonly int IsSelected; public readonly int Name;
        public readonly int Category; public readonly int Source; public readonly int Author; public readonly int Url;
        public readonly int GameDomain; public readonly int NexusModId; public readonly int NexusFileId; public readonly int NexusFileUuid;
        public readonly int Version; public readonly int Status; public readonly int Progress; public readonly int BytesPerSecond;
        public readonly int Size; public readonly int LocalArchivePath; public readonly int TemporaryPath; public readonly int Error;
        public readonly int Description; public readonly int UpdatedAt; public readonly int CreatedAt; public readonly int AdditionalUrl;
        public readonly int DownloadOrder; public readonly int CatalogId; public readonly int DecisionGroup; public readonly int Sha256;
        public readonly int ArchiveFileName; public readonly int UpdateCheckStatus; public readonly int AvailableVersion;
        public readonly int AvailableFileId; public readonly int AvailableFileUuid; public readonly int LastUpdateCheckUtc; public readonly int UpdateCheckMessage;

        public EntryOrdinals(SqliteDataReader reader)
        {
            EntryId = reader.GetOrdinal("EntryId"); Number = reader.GetOrdinal("Number"); IsSelected = reader.GetOrdinal("IsSelected"); Name = reader.GetOrdinal("Name");
            Category = reader.GetOrdinal("Category"); Source = reader.GetOrdinal("Source"); Author = reader.GetOrdinal("Author"); Url = reader.GetOrdinal("Url");
            GameDomain = reader.GetOrdinal("GameDomain"); NexusModId = reader.GetOrdinal("NexusModId"); NexusFileId = reader.GetOrdinal("NexusFileId"); NexusFileUuid = reader.GetOrdinal("NexusFileUuid");
            Version = reader.GetOrdinal("Version"); Status = reader.GetOrdinal("Status"); Progress = reader.GetOrdinal("Progress"); BytesPerSecond = reader.GetOrdinal("BytesPerSecond");
            Size = reader.GetOrdinal("Size"); LocalArchivePath = reader.GetOrdinal("LocalArchivePath"); TemporaryPath = reader.GetOrdinal("TemporaryPath"); Error = reader.GetOrdinal("Error");
            Description = reader.GetOrdinal("Description"); UpdatedAt = reader.GetOrdinal("UpdatedAt"); CreatedAt = reader.GetOrdinal("CreatedAt"); AdditionalUrl = reader.GetOrdinal("AdditionalUrl");
            DownloadOrder = reader.GetOrdinal("DownloadOrder"); CatalogId = reader.GetOrdinal("CatalogId"); DecisionGroup = reader.GetOrdinal("DecisionGroup"); Sha256 = reader.GetOrdinal("Sha256");
            ArchiveFileName = reader.GetOrdinal("ArchiveFileName"); UpdateCheckStatus = reader.GetOrdinal("UpdateCheckStatus"); AvailableVersion = reader.GetOrdinal("AvailableVersion");
            AvailableFileId = reader.GetOrdinal("AvailableFileId"); AvailableFileUuid = reader.GetOrdinal("AvailableFileUuid"); LastUpdateCheckUtc = reader.GetOrdinal("LastUpdateCheckUtc"); UpdateCheckMessage = reader.GetOrdinal("UpdateCheckMessage");
        }
    }

    private sealed class UpsertCommandBinder
    {
        private readonly SqliteParameter _pId, _pNumber, _pSelected, _pName, _pCategory, _pSource, _pAuthor, _pUrl, _pGame, _pMod, _pFile, _pFileUuid, _pVersion, _pStatus, _pProgress, _pSpeed, _pSize, _pArchive, _pTemporary, _pError, _pDescription, _pUpdated, _pCreated, _pAdditionalUrl, _pDownloadOrder, _pCatalogId, _pDecisionGroup, _pSha256, _pArchiveFileName, _pUpdateCheckStatus, _pAvailableVersion, _pAvailableFileId, _pAvailableFileUuid, _pLastUpdateCheckUtc, _pUpdateCheckMessage;

        public UpsertCommandBinder(SqliteCommand command)
        {
            _pId = command.Parameters.Add("$id", SqliteType.Text);
            _pNumber = command.Parameters.Add("$number", SqliteType.Integer);
            _pSelected = command.Parameters.Add("$selected", SqliteType.Integer);
            _pName = command.Parameters.Add("$name", SqliteType.Text);
            _pCategory = command.Parameters.Add("$category", SqliteType.Text);
            _pSource = command.Parameters.Add("$source", SqliteType.Integer);
            _pAuthor = command.Parameters.Add("$author", SqliteType.Text);
            _pUrl = command.Parameters.Add("$url", SqliteType.Text);
            _pGame = command.Parameters.Add("$game", SqliteType.Text);
            _pMod = command.Parameters.Add("$mod", SqliteType.Integer);
            _pFile = command.Parameters.Add("$file", SqliteType.Integer);
            _pFileUuid = command.Parameters.Add("$fileUuid", SqliteType.Text);
            _pVersion = command.Parameters.Add("$version", SqliteType.Text);
            _pStatus = command.Parameters.Add("$status", SqliteType.Integer);
            _pProgress = command.Parameters.Add("$progress", SqliteType.Real);
            _pSpeed = command.Parameters.Add("$speed", SqliteType.Real);
            _pSize = command.Parameters.Add("$size", SqliteType.Integer);
            _pArchive = command.Parameters.Add("$archive", SqliteType.Text);
            _pTemporary = command.Parameters.Add("$temporary", SqliteType.Text);
            _pError = command.Parameters.Add("$error", SqliteType.Text);
            _pDescription = command.Parameters.Add("$description", SqliteType.Text);
            _pUpdated = command.Parameters.Add("$updated", SqliteType.Text);
            _pCreated = command.Parameters.Add("$created", SqliteType.Text);
            _pAdditionalUrl = command.Parameters.Add("$additionalUrl", SqliteType.Text);
            _pDownloadOrder = command.Parameters.Add("$downloadOrder", SqliteType.Integer);
            _pCatalogId = command.Parameters.Add("$catalogId", SqliteType.Text);
            _pDecisionGroup = command.Parameters.Add("$decisionGroup", SqliteType.Text);
            _pSha256 = command.Parameters.Add("$sha256", SqliteType.Text);
            _pArchiveFileName = command.Parameters.Add("$archiveFileName", SqliteType.Text);
            _pUpdateCheckStatus = command.Parameters.Add("$updateCheckStatus", SqliteType.Integer);
            _pAvailableVersion = command.Parameters.Add("$availableVersion", SqliteType.Text);
            _pAvailableFileId = command.Parameters.Add("$availableFileId", SqliteType.Integer);
            _pAvailableFileUuid = command.Parameters.Add("$availableFileUuid", SqliteType.Text);
            _pLastUpdateCheckUtc = command.Parameters.Add("$lastUpdateCheckUtc", SqliteType.Text);
            _pUpdateCheckMessage = command.Parameters.Add("$updateCheckMessage", SqliteType.Text);
        }

        public void Bind(DownloaderEntry entry)
        {
            _pId.Value = entry.Id.ToString("N");
            _pNumber.Value = entry.Number;
            _pSelected.Value = entry.IsSelected ? 1 : 0;
            _pName.Value = entry.Name;
            _pCategory.Value = entry.Category;
            _pSource.Value = (int)entry.Source;
            _pAuthor.Value = entry.Author;
            _pUrl.Value = entry.Url;
            _pGame.Value = entry.GameDomain;
            _pMod.Value = (object?)entry.NexusModId ?? DBNull.Value;
            _pFile.Value = (object?)entry.NexusFileId ?? DBNull.Value;
            _pFileUuid.Value = entry.NexusFileUuid;
            _pVersion.Value = entry.Version;
            _pStatus.Value = (int)entry.Status;
            _pProgress.Value = entry.Progress;
            _pSpeed.Value = entry.BytesPerSecond;
            _pSize.Value = entry.Size;
            _pArchive.Value = entry.LocalArchivePath;
            _pTemporary.Value = entry.TemporaryPath;
            _pError.Value = entry.Error;
            _pDescription.Value = entry.Description;
            _pUpdated.Value = (object?)entry.UpdatedAt?.ToString("O") ?? DBNull.Value;
            _pCreated.Value = entry.CreatedAt.ToString("O");
            _pAdditionalUrl.Value = entry.AdditionalUrl;
            _pDownloadOrder.Value = (object?)entry.DownloadOrder ?? DBNull.Value;
            _pCatalogId.Value = entry.CatalogId;
            _pDecisionGroup.Value = entry.DecisionGroup;
            _pSha256.Value = entry.Sha256;
            _pArchiveFileName.Value = entry.ArchiveFileName;
            _pUpdateCheckStatus.Value = (int)entry.UpdateCheckStatus;
            _pAvailableVersion.Value = entry.AvailableVersion;
            _pAvailableFileId.Value = (object?)entry.AvailableFileId ?? DBNull.Value;
            _pAvailableFileUuid.Value = entry.AvailableFileUuid;
            _pLastUpdateCheckUtc.Value = (object?)entry.LastUpdateCheckUtc?.ToString("O") ?? DBNull.Value;
            _pUpdateCheckMessage.Value = entry.UpdateCheckMessage;
        }
    }
}

public static class DownloaderImportIdentity
{
    public static string GetKey(DownloaderEntry entry)
    {
        var game = string.IsNullOrWhiteSpace(entry.GameDomain) ? "cyberpunk2077" : entry.GameDomain.Trim().ToLowerInvariant();
        if (entry.NexusModId is { } modId)
        {
            return entry.NexusFileId is { } fileId ? $"nexus:{game}:{modId}:{fileId}" : $"nexus:{game}:{modId}";
        }
        if (!string.IsNullOrWhiteSpace(entry.Url))
            return $"url:{NormalizeUrl(entry.Url)}";
        return $"entry:{entry.Id:N}";
    }

    private static string NormalizeUrl(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed.ToLowerInvariant();
        return new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty
        }.Uri.ToString().TrimEnd('/').ToLowerInvariant();
    }
}
