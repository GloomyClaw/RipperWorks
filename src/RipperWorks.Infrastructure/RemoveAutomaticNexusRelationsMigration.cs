using Microsoft.Data.Sqlite;

namespace RipperWorks.Infrastructure;

internal sealed class RemoveAutomaticNexusRelationsMigration
    : IStartupMigrationStep
{
    private static readonly string[] PackageColumns =
    [
        "RelationId",
        "FromPackageId",
        "ToPackageId",
        "RelationType",
        "Source",
        "IsConfirmed",
        "CreatedAtUtc"
    ];

    private static readonly string[] LibraryColumns =
    [
        "RelationId",
        "FromLibraryModId",
        "ToLibraryModId",
        "RelationType",
        "Source",
        "IsConfirmed",
        "CreatedAtUtc"
    ];

    public string Id => StartupMigrationIds.RemoveAutomaticNexusRelations;

    public async Task<StartupMigrationInspection> InspectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        if (schemaVersion is < 5 or > 9)
        {
            throw new StartupMigrationBlockedException(
                $"Organizer schema {schemaVersion} cannot contain the " +
                "supported automatic relation shape.");
        }
        await SqliteStartupMigration.RequireColumnsAsync(
            connection,
            transaction,
            "PackageRelations",
            PackageColumns,
            cancellationToken);
        var values = new List<string> { "organizer-relations-v1" };
        var deletes = await AddRowsAsync(
            connection,
            transaction,
            "PackageRelations",
            "FromPackageId",
            "ToPackageId",
            values,
            cancellationToken);
        if (schemaVersion >= 7)
        {
            await SqliteStartupMigration.RequireColumnsAsync(
                connection,
                transaction,
                "LibraryModRelations",
                LibraryColumns,
                cancellationToken);
            deletes += await AddRowsAsync(
                connection,
                transaction,
                "LibraryModRelations",
                "FromLibraryModId",
                "ToLibraryModId",
                values,
                cancellationToken);
        }
        else
        {
            values.Add("LibraryModRelations|not-in-schema");
        }
        return new(
            schemaVersion,
            SqliteStartupMigration.Hash(values),
            0,
            0,
            deletes,
            0,
            [],
            true);
    }

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedUtc,
        CancellationToken cancellationToken)
    {
        await DeleteIfPresentAsync(
            connection,
            transaction,
            "PackageRelations",
            cancellationToken);
        await DeleteIfPresentAsync(
            connection,
            transaction,
            "LibraryModRelations",
            cancellationToken);
    }

    private static async Task<int> AddRowsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string fromColumn,
        string toColumn,
        ICollection<string> values,
        CancellationToken cancellationToken)
    {
        var count = 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT RelationId, {fromColumn}, {toColumn},
                    RelationType, Source, IsConfirmed, CreatedAtUtc
             FROM {table}
             WHERE Source IN (2, 3)
             ORDER BY RelationId;
             """;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
            values.Add(string.Join(
                "|",
                table,
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6)));
        }
        return count;
    }

    private static async Task DeleteIfPresentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        if (!await SqliteStartupMigration.TableExistsAsync(
                connection,
                transaction,
                table,
                cancellationToken))
        {
            return;
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"DELETE FROM {table} WHERE Source IN (2, 3);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
