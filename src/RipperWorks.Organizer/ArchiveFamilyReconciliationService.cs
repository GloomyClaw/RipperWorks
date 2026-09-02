using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed class ArchiveFamilyReconciliationService
{
    private readonly OrganizerRepository _organizer;
    private readonly string _downloaderDatabasePath;

    public ArchiveFamilyReconciliationService(
        OrganizerRepository organizer,
        string downloaderDatabasePath)
    {
        _organizer = organizer;
        _downloaderDatabasePath =
            Path.GetFullPath(downloaderDatabasePath);
    }

    public async Task<ArchiveFamilyReconciliationResult> ReconcileAsync(
        IEnumerable<ConfirmedArchiveUpdatePair>? additionalPairs = null,
        CancellationToken cancellationToken = default)
    {
        var pairs = new List<ConfirmedArchiveUpdatePair>();
        if (File.Exists(_downloaderDatabasePath))
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _downloaderDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            await using var connection =
                new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT GameDomain, NexusModId, NexusFileId,
                       AvailableFileId
                FROM DownloaderEntries
                WHERE Source = $nexus
                  AND UpdateCheckStatus = $updateAvailable
                  AND NexusModId IS NOT NULL
                  AND NexusFileId IS NOT NULL
                  AND AvailableFileId IS NOT NULL
                  AND NexusFileId <> AvailableFileId;
                """;
            command.Parameters.AddWithValue(
                "$nexus",
                (int)DownloaderSource.Nexus);
            command.Parameters.AddWithValue(
                "$updateAvailable",
                (int)NexusUpdateCheckStatus.UpdateAvailable);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pairs.Add(new(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3)));
            }
        }
        if (additionalPairs is not null)
            pairs.AddRange(additionalPairs);
        return await _organizer.ReconcileArchiveFamiliesAsync(
            pairs,
            cancellationToken);
    }
}
