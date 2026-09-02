using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Infrastructure;

internal sealed class ConfirmRedscriptUpdateFamilyMigration
    : IStartupMigrationStep
{
    private const string GameDomain = "cyberpunk2077";
    private const long NexusModId = 1511;
    private const long CurrentFileId = 101739;
    private const long AvailableFileId = 120421;
    private const string Source = "Hotfix3.6.1ConfirmedRedscriptPair";

    private static readonly string[] PairColumns =
    [
        "GameDomain",
        "NexusModId",
        "CurrentFileId",
        "AvailableFileId",
        "Source",
        "ConfirmedAtUtc"
    ];

    private static readonly string[] PackageColumns =
    [
        "PackageId",
        "Source",
        "GameDomain",
        "NexusModId",
        "NexusFileId",
        "LibraryModId",
        "ArchiveFamilyKey"
    ];

    private static readonly string[] LinkColumns =
    [
        "LibraryModId",
        "FromArchiveId",
        "ToArchiveId",
        "Source",
        "CreatedAtUtc"
    ];

    public string Id => StartupMigrationIds.ConfirmRedscriptUpdateFamily;

    public async Task<StartupMigrationInspection> InspectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        if (schemaVersion is < 8 or > 9)
        {
            throw new StartupMigrationBlockedException(
                $"Organizer schema {schemaVersion} is not the supported " +
                "schema 8 for RF-02 data hygiene.");
        }
        await RequireShapeAsync(
            connection,
            transaction,
            cancellationToken);
        var remembered = await LoadRememberedSourceAsync(
            connection,
            transaction,
            cancellationToken);
        var candidates = await LoadCandidatesAsync(
            connection,
            transaction,
            cancellationToken);
        var values = new List<string>
        {
            "redscript-family-v1",
            remembered ?? "<missing>"
        };
        values.AddRange(candidates.Select(candidate =>
            candidate.Canonical));
        var warnings = new List<string>();
        if (candidates.Count == 0)
            warnings.Add("CONFIRMED_PAIR_PACKAGES_NOT_PRESENT");
        if (candidates.Count > 1)
            warnings.Add("AMBIGUOUS_CONFIRMED_PAIR_PACKAGES");
        var updates = 0;
        var links = 0;
        var familyAmbiguity = candidates.Count == 1 &&
                              HasConflictingStableFamilies(candidates[0]);
        if (familyAmbiguity)
            warnings.Add("AMBIGUOUS_CONFIRMED_PAIR_FAMILY_KEYS");
        if (candidates.Count == 1 && !familyAmbiguity)
        {
            var candidate = candidates[0];
            var family = StableFamily(candidate);
            updates = CountFamilyUpdates(candidate, family);
            if (!await LinkExistsAsync(
                    connection,
                    transaction,
                    candidate,
                    cancellationToken))
            {
                links = 1;
            }
            values.Add("stable-family|" + family);
            values.Add("link-pending|" + links);
        }
        return new(
            schemaVersion,
            SqliteStartupMigration.Hash(values),
            (remembered is null ? 1 : 0) + links,
            updates,
            0,
            Math.Max(0, candidates.Count - 1) +
            (familyAmbiguity ? 1 : 0),
            warnings,
            candidates.Count <= 1 && !familyAmbiguity);
    }

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedUtc,
        CancellationToken cancellationToken)
    {
        await RememberPairAsync(
            connection,
            transaction,
            appliedUtc,
            cancellationToken);
        var candidates = await LoadCandidatesAsync(
            connection,
            transaction,
            cancellationToken);
        if (candidates.Count == 0)
            return;
        if (candidates.Count != 1)
        {
            throw new StartupMigrationBlockedException(
                "Confirmed Redscript pair has ambiguous organizer matches.");
        }
        var candidate = candidates[0];
        if (HasConflictingStableFamilies(candidate))
        {
            throw new StartupMigrationBlockedException(
                "Confirmed Redscript pair has conflicting stable family keys.");
        }
        var family = StableFamily(candidate);
        await UpdateFamiliesAsync(
            connection,
            transaction,
            candidate,
            family,
            cancellationToken);
        await InsertLinkAsync(
            connection,
            transaction,
            candidate,
            appliedUtc,
            cancellationToken);
    }

    private static async Task RequireShapeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await SqliteStartupMigration.RequireColumnsAsync(
            connection,
            transaction,
            "ConfirmedArchiveUpdatePairs",
            PairColumns,
            cancellationToken);
        await SqliteStartupMigration.RequireColumnsAsync(
            connection,
            transaction,
            "Packages",
            PackageColumns,
            cancellationToken);
        await SqliteStartupMigration.RequireColumnsAsync(
            connection,
            transaction,
            "ArchiveFamilyLinks",
            LinkColumns,
            cancellationToken);
    }

    private static async Task<string?> LoadRememberedSourceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Source
            FROM ConfirmedArchiveUpdatePairs
            WHERE GameDomain=$game COLLATE NOCASE
              AND NexusModId=$mod
              AND CurrentFileId=$current
              AND AvailableFileId=$available;
            """;
        AddIdentityParameters(command);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task<List<Candidate>> LoadCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var result = new List<Candidate>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
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
              AND current.NexusModId = $mod
              AND current.NexusFileId = $current
              AND available.Source = $nexus
              AND available.GameDomain = $game COLLATE NOCASE
              AND available.NexusModId = $mod
              AND available.NexusFileId = $available
            ORDER BY current.PackageId, available.PackageId;
            """;
        AddIdentityParameters(command);
        command.Parameters.AddWithValue(
            "$nexus",
            (int)PackageSource.Nexus);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
    }

    private static async Task<bool> LinkExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Candidate candidate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM ArchiveFamilyLinks
                WHERE FromArchiveId=$from AND ToArchiveId=$to);
            """;
        command.Parameters.AddWithValue("$from", candidate.CurrentId);
        command.Parameters.AddWithValue("$to", candidate.AvailableId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task RememberPairAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO ConfirmedArchiveUpdatePairs (
                GameDomain, NexusModId, CurrentFileId,
                AvailableFileId, Source, ConfirmedAtUtc)
            VALUES (
                $game, $mod, $current, $available, $source, $confirmed);
            """;
        AddIdentityParameters(command);
        command.Parameters.AddWithValue("$source", Source);
        command.Parameters.AddWithValue(
            "$confirmed",
            appliedUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateFamiliesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Candidate candidate,
        string family,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE Packages
            SET ArchiveFamilyKey=$family
            WHERE PackageId IN ($currentId, $availableId)
              AND (
                  ArchiveFamilyKey IS NULL OR
                  ArchiveFamilyKey <> $family);
            """;
        command.Parameters.AddWithValue("$family", family);
        command.Parameters.AddWithValue("$currentId", candidate.CurrentId);
        command.Parameters.AddWithValue(
            "$availableId",
            candidate.AvailableId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Candidate candidate,
        DateTimeOffset appliedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO ArchiveFamilyLinks (
                LibraryModId, FromArchiveId, ToArchiveId,
                Source, CreatedAtUtc)
            VALUES ($mod, $from, $to, $source, $created);
            """;
        command.Parameters.AddWithValue(
            "$mod",
            candidate.LibraryModId);
        command.Parameters.AddWithValue("$from", candidate.CurrentId);
        command.Parameters.AddWithValue("$to", candidate.AvailableId);
        command.Parameters.AddWithValue("$source", Source);
        command.Parameters.AddWithValue(
            "$created",
            appliedUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static int CountFamilyUpdates(
        Candidate candidate,
        string stableFamily) =>
        new[] { candidate.CurrentFamily, candidate.AvailableFamily }
            .Count(family =>
                !string.Equals(
                    family,
                    stableFamily,
                    StringComparison.Ordinal));

    private static bool HasConflictingStableFamilies(
        Candidate candidate) =>
        StableFamilies(candidate).Distinct(StringComparer.Ordinal).Count() > 1;

    private static string StableFamily(Candidate candidate)
    {
        var stable = StableFamilies(candidate).FirstOrDefault();
        if (stable is not null)
            return stable;
        var identity =
            $"{GameDomain}|{NexusModId}|{CurrentFileId}|{AvailableFileId}";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..20];
        return $"nexus-chain:{GameDomain}:{NexusModId}:{hash}";
    }

    private static IEnumerable<string> StableFamilies(Candidate candidate) =>
        new[] { candidate.CurrentFamily, candidate.AvailableFamily }
            .Where(value =>
                !string.IsNullOrWhiteSpace(value) &&
                !value.StartsWith(
                    "archive:",
                    StringComparison.OrdinalIgnoreCase))
            .Select(value => value!);

    private static void AddIdentityParameters(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$game", GameDomain);
        command.Parameters.AddWithValue("$mod", NexusModId);
        command.Parameters.AddWithValue("$current", CurrentFileId);
        command.Parameters.AddWithValue("$available", AvailableFileId);
    }

    private sealed record Candidate(
        string CurrentId,
        string AvailableId,
        string LibraryModId,
        string? CurrentFamily,
        string? AvailableFamily)
    {
        public string Canonical =>
            string.Join(
                "|",
                CurrentId,
                AvailableId,
                LibraryModId,
                CurrentFamily ?? "<null>",
                AvailableFamily ?? "<null>");
    }
}
