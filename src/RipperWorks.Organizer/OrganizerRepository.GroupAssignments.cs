using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class OrganizerRepository
{
    internal int? TestOnlyGroupAssignmentFaultAfterUpdateCount { get; set; }

    public async Task AssignLibraryModsToGroupAsync(
        IReadOnlyCollection<LibraryModId> libraryModIds,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(libraryModIds);
        var ids = libraryModIds.Distinct().ToArray();
        if (ids.Length == 0)
            return;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        if (groupId is not null)
        {
            await using var group = connection.CreateCommand();
            group.Transaction = (SqliteTransaction)transaction;
            group.CommandText = "SELECT COUNT(*) FROM Groups WHERE Id = $id;";
            group.Parameters.AddWithValue("$id", groupId.Value);
            if (Convert.ToInt64(
                    await group.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                throw new InvalidOperationException(
                    "The target Library group does not exist.");
            }
        }

        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            await using (var mod = connection.CreateCommand())
            {
                mod.Transaction = (SqliteTransaction)transaction;
                mod.CommandText =
                    """
                    UPDATE LibraryMods
                    SET GroupId = $groupId,
                        UpdatedAtUtc = $updated
                    WHERE LibraryModId = $libraryModId;
                    """;
                mod.Parameters.AddWithValue("$groupId", DbValue(groupId));
                mod.Parameters.AddWithValue(
                    "$updated",
                    FormatUtc(DateTime.UtcNow));
                mod.Parameters.AddWithValue("$libraryModId", id.Value);
                if (await mod.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException(
                        $"Library mod '{id.Value}' does not exist.");
                }
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
                packages.Parameters.AddWithValue("$libraryModId", id.Value);
                if (await packages.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new InvalidOperationException(
                        $"Library mod '{id.Value}' has no archive records.");
                }
            }

            if (TestOnlyGroupAssignmentFaultAfterUpdateCount == index + 1)
            {
                throw new InvalidOperationException(
                    "Injected Library group assignment failure.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
