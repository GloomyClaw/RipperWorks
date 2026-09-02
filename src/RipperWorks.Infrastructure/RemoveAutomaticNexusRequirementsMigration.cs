using Microsoft.Data.Sqlite;

namespace RipperWorks.Infrastructure;

internal sealed class RemoveAutomaticNexusRequirementsMigration
    : IStartupMigrationStep
{
    private static readonly string[] RequirementsColumns =
    [
        "PackageId",
        "RequiredNexusModId"
    ];

    private static readonly string[] SyncColumns =
    [
        "PackageId",
        "RetrievedAtUtc"
    ];

    public string Id =>
        StartupMigrationIds.RemoveAutomaticNexusRequirements;

    public async Task<StartupMigrationInspection> InspectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        var values = new List<string> { "catalog-requirements-v1" };
        var requirements = await InspectTableAsync(
            connection,
            transaction,
            "CatalogPackageRequirements",
            RequirementsColumns,
            values,
            cancellationToken);
        var sync = await InspectTableAsync(
            connection,
            transaction,
            "CatalogRequirementSync",
            SyncColumns,
            values,
            cancellationToken);
        return new(
            schemaVersion,
            SqliteStartupMigration.Hash(values),
            0,
            0,
            requirements + sync,
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
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DROP TABLE IF EXISTS CatalogPackageRequirements;
            DROP TABLE IF EXISTS CatalogRequirementSync;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> InspectTableAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyCollection<string> expectedColumns,
        ICollection<string> values,
        CancellationToken cancellationToken)
    {
        if (!await SqliteStartupMigration.TableExistsAsync(
                connection,
                transaction,
                table,
                cancellationToken))
        {
            values.Add(table + "|absent");
            return 0;
        }
        var columns = await SqliteStartupMigration.ColumnsAsync(
            connection,
            transaction,
            table,
            cancellationToken);
        if (columns.Count != expectedColumns.Count ||
            expectedColumns.Any(column => !columns.ContainsKey(column)))
        {
            throw new StartupMigrationBlockedException(
                $"{table} has an ambiguous legacy shape.");
        }
        values.Add(
            table + "|present|" +
            string.Join(",", columns.Keys.OrderBy(
                value => value,
                StringComparer.Ordinal)));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = table == "CatalogPackageRequirements"
            ? """
              SELECT PackageId, RequiredNexusModId
              FROM CatalogPackageRequirements
              ORDER BY PackageId, RequiredNexusModId;
              """
            : """
              SELECT PackageId, RetrievedAtUtc
              FROM CatalogRequirementSync
              ORDER BY PackageId, RetrievedAtUtc;
              """;
        var count = 0;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
            values.Add(
                table + "|" +
                Convert.ToString(reader.GetValue(0)) + "|" +
                Convert.ToString(reader.GetValue(1)));
        }
        return count;
    }
}
