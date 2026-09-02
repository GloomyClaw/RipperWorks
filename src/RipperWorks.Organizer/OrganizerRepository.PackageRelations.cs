using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class OrganizerRepository
{
    private async Task<bool> PackageRelationExistsAsync(
        PackageId from,
        PackageId to,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1 FROM PackageRelations
                WHERE FromPackageId = $from AND ToPackageId = $to);
            """;
        command.Parameters.AddWithValue("$from", from.Value);
        command.Parameters.AddWithValue("$to", to.Value);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private async Task<bool> WouldCreatePackageRelationCycleAsync(
        PackageId from,
        PackageId to,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE RequiredPackages(PackageId) AS (
                SELECT $to
                UNION
                SELECT r.ToPackageId
                FROM PackageRelations r
                JOIN RequiredPackages p
                  ON r.FromPackageId = p.PackageId
            )
            SELECT EXISTS(
                SELECT 1 FROM RequiredPackages WHERE PackageId = $from);
            """;
        command.Parameters.AddWithValue("$from", from.Value);
        command.Parameters.AddWithValue("$to", to.Value);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private async Task<PackageRelationRecord>
        InsertConcretePackageRelationAsync(
            PackageId from,
            PackageId to,
            PackageRelationType type,
            PackageRelationSource source,
            bool confirmed,
            CancellationToken cancellationToken)
    {
        var createdAtUtc = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PackageRelations (
                FromPackageId, ToPackageId, RelationType, Source,
                IsConfirmed, CreatedAtUtc)
            VALUES ($from, $to, $type, $source, $confirmed, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$from", from.Value);
        command.Parameters.AddWithValue("$to", to.Value);
        command.Parameters.AddWithValue("$type", (int)type);
        command.Parameters.AddWithValue("$source", (int)source);
        command.Parameters.AddWithValue("$confirmed", confirmed ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", FormatUtc(createdAtUtc));
        var relationId = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
        return new()
        {
            RelationId = relationId,
            FromPackageId = from,
            ToPackageId = to,
            RelationType = type,
            Source = source,
            IsConfirmed = confirmed,
            CreatedAtUtc = createdAtUtc
        };
    }

    private async Task<IReadOnlyList<PackageRelationView>>
        LoadConcretePackageRelationsAsync(
            PackageId packageId,
            CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.RelationId, r.FromPackageId, r.ToPackageId,
                   r.RelationType, r.Source, r.IsConfirmed, r.CreatedAtUtc,
                   COALESCE(NULLIF(f.CustomDisplayName, ''), f.IndexedDisplayName),
                   COALESCE(NULLIF(t.CustomDisplayName, ''), t.IndexedDisplayName),
                   f.InstallationState, t.InstallationState,
                   f.IsPresent, t.IsPresent
            FROM PackageRelations r
            JOIN Packages f ON f.PackageId = r.FromPackageId
            JOIN Packages t ON t.PackageId = r.ToPackageId
            WHERE r.FromPackageId = $id OR r.ToPackageId = $id
            ORDER BY r.CreatedAtUtc, r.RelationId;
            """;
        command.Parameters.AddWithValue("$id", packageId.Value);
        var result = new List<PackageRelationView>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new()
            {
                Relation = new()
                {
                    RelationId = reader.GetInt64(0),
                    FromPackageId = new(reader.GetString(1)),
                    ToPackageId = new(reader.GetString(2)),
                    RelationType = (PackageRelationType)reader.GetInt32(3),
                    Source = (PackageRelationSource)reader.GetInt32(4),
                    IsConfirmed = reader.GetInt32(5) != 0,
                    CreatedAtUtc = ParseUtc(reader.GetString(6))
                },
                FromDisplayName = reader.GetString(7),
                ToDisplayName = reader.GetString(8),
                FromInstallationState =
                    (PackageInstallationState)reader.GetInt32(9),
                ToInstallationState =
                    (PackageInstallationState)reader.GetInt32(10),
                FromArchivePresent = reader.GetInt32(11) != 0,
                ToArchivePresent = reader.GetInt32(12) != 0
            });
        }
        return result;
    }

    public async Task<InstalledPathOwner?>
        FindInstalledPathOwnerForCoordinatorAsync(
            string relativeGamePath,
            PackageId requestingPackageId,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT l.PackageId,
                   COALESCE(NULLIF(p.CustomDisplayName, ''), p.IndexedDisplayName),
                   EXISTS(
                       SELECT 1 FROM LibraryModRelations r
                       JOIN Packages requesting
                         ON requesting.PackageId = $requestingPackageId
                       JOIN Packages owning ON owning.PackageId = l.PackageId
                       WHERE r.FromLibraryModId = requesting.LibraryModId
                         AND r.ToLibraryModId = owning.LibraryModId
                         AND r.RelationType = $addOnOf
                         AND r.IsConfirmed = 1)
                   OR EXISTS(
                       SELECT 1 FROM PackageRelations pr
                       WHERE pr.FromPackageId = $requestingPackageId
                         AND pr.ToPackageId = l.PackageId
                         AND pr.RelationType = $addOnOf
                         AND pr.IsConfirmed = 1)
            FROM ManagedPathLayers l
            JOIN Packages p ON p.PackageId = l.PackageId
            WHERE l.RelativePath = $relativePath COLLATE NOCASE
            ORDER BY l.LayerOrder DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$relativePath", relativeGamePath);
        command.Parameters.AddWithValue(
            "$requestingPackageId", requestingPackageId.Value);
        command.Parameters.AddWithValue(
            "$addOnOf", (int)PackageRelationType.AddOnOf);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                new PackageId(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2) != 0)
            : null;
    }
}
