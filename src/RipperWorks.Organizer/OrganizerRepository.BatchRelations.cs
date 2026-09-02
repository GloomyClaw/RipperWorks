using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class OrganizerRepository
{
    internal async Task<IReadOnlyList<PackageRelationRecord>>
        LoadBatchDependencyRelationsAsync(
            IReadOnlyCollection<PackageId> packageIds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageIds);
        var requested = packageIds.Distinct().ToArray();
        if (requested.Length == 0)
            return [];

        await using var connection = await OpenAsync(cancellationToken);
        var packages = await OperationPerformanceDiagnostics
            .MeasureDatabaseAsync(() => LoadBatchPackageRowsAsync(
                connection,
                cancellationToken));
        var logical = await OperationPerformanceDiagnostics
            .MeasureDatabaseAsync(() => LoadBatchLogicalRowsAsync(
                connection,
                cancellationToken));
        var concrete = await OperationPerformanceDiagnostics
            .MeasureDatabaseAsync(() => LoadBatchConcreteRowsAsync(
                connection,
                cancellationToken));

        var packageById = packages.ToDictionary(row => row.PackageId);
        var packagesByMod = packages
            .GroupBy(row => row.LibraryModId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var preferredByMod = packagesByMod.ToDictionary(
            pair => pair.Key,
            pair => SelectPreferredPackage(pair.Value));
        var logicalByMod = logical
            .SelectMany(row => new[]
            {
                (row.FromLibraryModId, Row: row),
                (row.ToLibraryModId, Row: row)
            })
            .ToLookup(item => item.Item1, item => item.Row);
        var concreteByPackage = concrete
            .SelectMany(row => new[]
            {
                (row.FromPackageId, Row: row),
                (row.ToPackageId, Row: row)
            })
            .ToLookup(item => item.Item1, item => item.Row);

        var result = new List<PackageRelationRecord>();
        foreach (var packageId in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!packageById.TryGetValue(packageId, out var package))
                continue;
            var projected = new List<PackageRelationRecord>();
            foreach (var relation in logicalByMod[package.LibraryModId])
            {
                if (!TryProjectLogicalRelation(
                        relation,
                        package,
                        preferredByMod,
                        out var record))
                {
                    continue;
                }
                projected.Add(record);
            }
            projected.AddRange(concreteByPackage[packageId]);
            result.AddRange(projected
                .OrderBy(row => row.CreatedAtUtc)
                .ThenBy(row => row.RelationId));
        }
        return result;
    }

    private static async Task<IReadOnlyList<BatchPackageRow>>
        LoadBatchPackageRowsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.PackageId, p.LibraryModId, p.InstallationState,
                   p.DownloadedAtUtc, p.LastIndexedAtUtc, p.LastWriteUtc,
                   l.PreferredArchiveId
            FROM Packages p
            JOIN LibraryMods l ON l.LibraryModId = p.LibraryModId
            ORDER BY p.IndexedDisplayName COLLATE NOCASE;
            """;
        var result = new List<BatchPackageRow>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                new PackageId(reader.GetString(0)),
                new LibraryModId(reader.GetString(1)),
                (PackageInstallationState)reader.GetInt32(2),
                ReadNullableUtc(reader, 3),
                ReadNullableUtc(reader, 4),
                ParseUtc(reader.GetString(5)),
                reader.IsDBNull(6)
                    ? null
                    : new PackageId(reader.GetString(6))));
        }
        return result;
    }

    private static async Task<IReadOnlyList<BatchLogicalRelationRow>>
        LoadBatchLogicalRowsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RelationId, FromLibraryModId, ToLibraryModId,
                   RelationType, Source, CreatedAtUtc
            FROM LibraryModRelations
            WHERE IsConfirmed = 1
              AND RelationType IN ($requires, $addOnOf)
            ORDER BY CreatedAtUtc, RelationId;
            """;
        command.Parameters.AddWithValue(
            "$requires",
            (int)PackageRelationType.Requires);
        command.Parameters.AddWithValue(
            "$addOnOf",
            (int)PackageRelationType.AddOnOf);
        var result = new List<BatchLogicalRelationRow>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                -reader.GetInt64(0),
                new LibraryModId(reader.GetString(1)),
                new LibraryModId(reader.GetString(2)),
                (PackageRelationType)reader.GetInt32(3),
                (PackageRelationSource)reader.GetInt32(4),
                ParseUtc(reader.GetString(5))));
        }
        return result;
    }

    private static async Task<IReadOnlyList<PackageRelationRecord>>
        LoadBatchConcreteRowsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RelationId, FromPackageId, ToPackageId,
                   RelationType, Source, CreatedAtUtc
            FROM PackageRelations
            WHERE IsConfirmed = 1
              AND RelationType IN ($requires, $addOnOf)
            ORDER BY CreatedAtUtc, RelationId;
            """;
        command.Parameters.AddWithValue(
            "$requires",
            (int)PackageRelationType.Requires);
        command.Parameters.AddWithValue(
            "$addOnOf",
            (int)PackageRelationType.AddOnOf);
        var result = new List<PackageRelationRecord>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new()
            {
                RelationId = reader.GetInt64(0),
                FromPackageId = new PackageId(reader.GetString(1)),
                ToPackageId = new PackageId(reader.GetString(2)),
                RelationType =
                    (PackageRelationType)reader.GetInt32(3),
                Source = (PackageRelationSource)reader.GetInt32(4),
                IsConfirmed = true,
                CreatedAtUtc = ParseUtc(reader.GetString(5))
            });
        }
        return result;
    }

    private static PackageId SelectPreferredPackage(
        IReadOnlyList<BatchPackageRow> packages)
    {
        var saved = packages[0].PreferredArchiveId;
        if (saved is { } savedId &&
            packages.Any(row => row.PackageId == savedId))
        {
            return savedId;
        }
        return packages
            .OrderByDescending(row =>
                row.InstallationState ==
                PackageInstallationState.Installed)
            .ThenByDescending(row => row.SortTimestamp)
            .First()
            .PackageId;
    }

    private static bool TryProjectLogicalRelation(
        BatchLogicalRelationRow relation,
        BatchPackageRow package,
        IReadOnlyDictionary<LibraryModId, PackageId> preferredByMod,
        out PackageRelationRecord record)
    {
        var fromPackageId = relation.FromLibraryModId == package.LibraryModId
            ? package.PackageId
            : preferredByMod.GetValueOrDefault(relation.FromLibraryModId);
        var toPackageId = relation.ToLibraryModId == package.LibraryModId
            ? package.PackageId
            : preferredByMod.GetValueOrDefault(relation.ToLibraryModId);
        if (fromPackageId == default || toPackageId == default)
        {
            record = null!;
            return false;
        }
        record = new()
        {
            RelationId = relation.RelationId,
            FromPackageId = fromPackageId,
            ToPackageId = toPackageId,
            RelationType = relation.RelationType,
            Source = relation.Source,
            IsConfirmed = true,
            CreatedAtUtc = relation.CreatedAtUtc
        };
        return true;
    }

    private static DateTime? ReadNullableUtc(
        SqliteDataReader reader,
        int ordinal) => reader.IsDBNull(ordinal)
            ? null
            : ParseUtc(reader.GetString(ordinal));

    private sealed record BatchPackageRow(
        PackageId PackageId,
        LibraryModId LibraryModId,
        PackageInstallationState InstallationState,
        DateTime? DownloadedAtUtc,
        DateTime? LastIndexedAtUtc,
        DateTime LastWriteUtc,
        PackageId? PreferredArchiveId)
    {
        internal DateTime SortTimestamp =>
            DownloadedAtUtc ?? LastIndexedAtUtc ?? LastWriteUtc;
    }

    private sealed record BatchLogicalRelationRow(
        long RelationId,
        LibraryModId FromLibraryModId,
        LibraryModId ToLibraryModId,
        PackageRelationType RelationType,
        PackageRelationSource Source,
        DateTime CreatedAtUtc);
}
