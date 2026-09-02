using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class OrganizerRepository
{
    public async Task<bool> PackageExistsAsync(
        PackageId packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM Packages WHERE PackageId = $id);";
        command.Parameters.AddWithValue("$id", packageId.Value);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task<bool> AreSameLibraryModAsync(
        PackageId firstPackageId,
        PackageId secondPackageId,
        CancellationToken cancellationToken = default)
    {
        // Application mutation identity is concrete PackageId.  A shared
        // LibraryModId may represent independently installable page components.
        await Task.CompletedTask;
        return firstPackageId == secondPackageId;
    }

    public async Task<bool> RelationExistsAsync(
        PackageId fromPackageId,
        PackageId toPackageId,
        CancellationToken cancellationToken = default)
    {
        var fromLibraryModId = await ResolveLibraryModIdAsync(
            fromPackageId,
            cancellationToken);
        var toLibraryModId = await ResolveLibraryModIdAsync(
            toPackageId,
            cancellationToken);
        if (fromLibraryModId is null || toLibraryModId is null)
            return false;
        if (fromLibraryModId == toLibraryModId)
        {
            return await PackageRelationExistsAsync(
                fromPackageId,
                toPackageId,
                cancellationToken);
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM LibraryModRelations
                WHERE FromLibraryModId = $from
                  AND ToLibraryModId = $to);
            """;
        command.Parameters.AddWithValue(
            "$from",
            fromLibraryModId.Value.Value);
        command.Parameters.AddWithValue(
            "$to",
            toLibraryModId.Value.Value);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task<bool> WouldCreateRelationCycleAsync(
        PackageId fromPackageId,
        PackageId toPackageId,
        CancellationToken cancellationToken = default)
    {
        var fromLibraryModId = await ResolveLibraryModIdAsync(
            fromPackageId,
            cancellationToken);
        var toLibraryModId = await ResolveLibraryModIdAsync(
            toPackageId,
            cancellationToken);
        if (fromLibraryModId is null || toLibraryModId is null)
            return false;
        if (fromLibraryModId == toLibraryModId)
        {
            return await WouldCreatePackageRelationCycleAsync(
                fromPackageId,
                toPackageId,
                cancellationToken);
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE RequiredMods(LibraryModId) AS (
                SELECT $to
                UNION
                SELECT r.ToLibraryModId
                FROM LibraryModRelations r
                JOIN RequiredMods p
                  ON r.FromLibraryModId = p.LibraryModId
            )
            SELECT EXISTS(
                SELECT 1
                FROM RequiredMods
                WHERE LibraryModId = $from);
            """;
        command.Parameters.AddWithValue(
            "$from",
            fromLibraryModId.Value.Value);
        command.Parameters.AddWithValue(
            "$to",
            toLibraryModId.Value.Value);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task<PackageRelationRecord> InsertPackageRelationAsync(
        PackageId fromPackageId,
        PackageId toPackageId,
        PackageRelationType relationType,
        PackageRelationSource source,
        bool isConfirmed,
        CancellationToken cancellationToken = default)
    {
        var fromLibraryModId = await ResolveLibraryModIdAsync(
            fromPackageId,
            cancellationToken) ?? throw new InvalidOperationException(
            "The source package has no library mod.");
        var toLibraryModId = await ResolveLibraryModIdAsync(
            toPackageId,
            cancellationToken) ?? throw new InvalidOperationException(
            "The target package has no library mod.");
        if (fromLibraryModId == toLibraryModId)
        {
            return await InsertConcretePackageRelationAsync(
                fromPackageId,
                toPackageId,
                relationType,
                source,
                isConfirmed,
                cancellationToken);
        }
        var createdAtUtc = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO LibraryModRelations (
                FromLibraryModId, ToLibraryModId, RelationType, Source,
                IsConfirmed, CreatedAtUtc)
            VALUES ($from, $to, $type, $source, $confirmed, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$from", fromLibraryModId.Value);
        command.Parameters.AddWithValue("$to", toLibraryModId.Value);
        command.Parameters.AddWithValue("$type", (int)relationType);
        command.Parameters.AddWithValue("$source", (int)source);
        command.Parameters.AddWithValue("$confirmed", isConfirmed ? 1 : 0);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            FormatUtc(createdAtUtc));
        var relationId = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
        return new PackageRelationRecord
        {
            RelationId = -relationId,
            FromPackageId = fromPackageId,
            ToPackageId = toPackageId,
            RelationType = relationType,
            Source = source,
            IsConfirmed = isConfirmed,
            CreatedAtUtc = createdAtUtc
        };
    }

    public async Task DeletePackageRelationAsync(
        long relationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = relationId < 0
            ? "DELETE FROM LibraryModRelations WHERE RelationId = $relationId;"
            : "DELETE FROM PackageRelations WHERE RelationId = $relationId;";
        command.Parameters.AddWithValue(
            "$relationId",
            relationId < 0 ? -relationId : relationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PackageRelationView>>
        LoadPackageRelationsAsync(
            PackageId packageId,
            CancellationToken cancellationToken = default)
    {
        var libraryModId = await ResolveLibraryModIdAsync(
            packageId,
            cancellationToken);
        if (libraryModId is null)
            return [];
        var mods = await LoadLibraryModsAsync(cancellationToken);
        var byId = mods.ToDictionary(mod => mod.LibraryModId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.RelationId, r.FromLibraryModId, r.ToLibraryModId,
                   r.RelationType, r.Source, r.IsConfirmed, r.CreatedAtUtc
            FROM LibraryModRelations r
            WHERE r.FromLibraryModId = $libraryModId
               OR r.ToLibraryModId = $libraryModId
            ORDER BY r.CreatedAtUtc, r.RelationId;
            """;
        command.Parameters.AddWithValue(
            "$libraryModId",
            libraryModId.Value.Value);
        var result = new List<PackageRelationView>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fromId = new LibraryModId(reader.GetString(1));
            var toId = new LibraryModId(reader.GetString(2));
            if (!byId.TryGetValue(fromId, out var from) ||
                !byId.TryGetValue(toId, out var to))
            {
                continue;
            }
            var fromPackageId = fromId == libraryModId
                ? packageId
                : from.PreferredArchive?.Package.PackageId;
            var toPackageId = toId == libraryModId
                ? packageId
                : to.PreferredArchive?.Package.PackageId;
            if (fromPackageId is null || toPackageId is null)
                continue;
            result.Add(new PackageRelationView
            {
                Relation = new PackageRelationRecord
                {
                    RelationId = -reader.GetInt64(0),
                    FromPackageId = fromPackageId.Value,
                    ToPackageId = toPackageId.Value,
                    RelationType =
                        (PackageRelationType)reader.GetInt32(3),
                    Source = (PackageRelationSource)reader.GetInt32(4),
                    IsConfirmed = reader.GetInt32(5) != 0,
                    CreatedAtUtc = ParseUtc(reader.GetString(6))
                },
                FromDisplayName = from.EffectiveDisplayName,
                ToDisplayName = to.EffectiveDisplayName,
                FromInstallationState = from.InstallationState,
                ToInstallationState = to.InstallationState,
                FromArchivePresent =
                    from.Archives.Any(archive => archive.IsPresent),
                ToArchivePresent =
                    to.Archives.Any(archive => archive.IsPresent)
            });
        }
        result.AddRange(await LoadConcretePackageRelationsAsync(
            packageId,
            cancellationToken));
        return result.OrderBy(item => item.Relation.CreatedAtUtc)
            .ThenBy(item => item.Relation.RelationId)
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<PackageId, IReadOnlyList<string>>>
        LoadMissingDependencyNamesAsync(
            CancellationToken cancellationToken = default)
    {
        var mods = await LoadLibraryModsAsync(cancellationToken);
        var byId = mods.ToDictionary(mod => mod.LibraryModId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT FromLibraryModId, ToLibraryModId
            FROM LibraryModRelations
            WHERE IsConfirmed = 1;
            """;
        var values = new Dictionary<PackageId, List<string>>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fromId = new LibraryModId(reader.GetString(0));
            var toId = new LibraryModId(reader.GetString(1));
            if (!byId.TryGetValue(fromId, out var from) ||
                !byId.TryGetValue(toId, out var to) ||
                to.InstallationState ==
                PackageInstallationState.Installed)
            {
                continue;
            }
            foreach (var archive in from.Archives)
            {
                var packageId = archive.Package.PackageId;
                if (!values.TryGetValue(packageId, out var names))
                {
                    names = [];
                    values.Add(packageId, names);
                }
                names.Add(to.EffectiveDisplayName);
            }
        }
        return values.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value);
    }

    private async Task<LibraryModId?> ResolveLibraryModIdAsync(
        PackageId packageId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT LibraryModId FROM Packages WHERE PackageId = $id;";
        command.Parameters.AddWithValue("$id", packageId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : new LibraryModId(Convert.ToString(value)!);
    }

    public async Task<IReadOnlyList<PackageRelationView>>
        LoadInstalledDependentRelationsAsync(
            PackageId requiredPackageId,
            CancellationToken cancellationToken = default)
    {
        var relations = await LoadPackageRelationsAsync(
            requiredPackageId,
            cancellationToken);
        return relations.Where(relation =>
                relation.Relation.ToPackageId == requiredPackageId &&
                relation.Relation.IsConfirmed &&
                relation.FromInstallationState ==
                PackageInstallationState.Installed)
            .ToArray();
    }

    public async Task<ManagedPathRecord?> LoadManagedPathAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RelativePath, BaseFileExisted, BaseContentHash
            FROM ManagedPaths
            WHERE RelativePath = $path COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$path", relativePath);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new ManagedPathRecord
        {
            RelativePath = reader.GetString(0),
            BaseFileExisted = reader.GetInt32(1) != 0,
            BaseContentHash = GetNullableString(reader, 2)
        };
    }

    public async Task<IReadOnlyList<ManagedPathLayerRecord>>
        LoadManagedPathLayersAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RelativePath, LayerOrder, PackageId, ContentHash,
                   OperationId, InstalledAtUtc
            FROM ManagedPathLayers
            WHERE RelativePath = $path COLLATE NOCASE
            ORDER BY LayerOrder;
            """;
        command.Parameters.AddWithValue("$path", relativePath);
        return await ReadLayersAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ManagedPathLayerRecord>>
        LoadManagedPathLayersForPackageAsync(
            PackageId packageId,
            CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RelativePath, LayerOrder, PackageId, ContentHash,
                   OperationId, InstalledAtUtc
            FROM ManagedPathLayers
            WHERE PackageId = $packageId
            ORDER BY RelativePath COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$packageId", packageId.Value);
        return await ReadLayersAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<ManagedPathLayerRecord>>
        ReadLayersAsync(
            SqliteCommand command,
            CancellationToken cancellationToken)
    {
        var result = new List<ManagedPathLayerRecord>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ManagedPathLayerRecord
            {
                RelativePath = reader.GetString(0),
                LayerOrder = reader.GetInt32(1),
                PackageId = new PackageId(reader.GetString(2)),
                ContentHash = reader.GetString(3),
                OperationId = reader.GetString(4),
                InstalledAtUtc = ParseUtc(reader.GetString(5))
            });
        }
        return result;
    }
}
