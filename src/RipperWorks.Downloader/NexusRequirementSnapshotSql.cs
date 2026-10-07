using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RipperWorks.Downloader;

internal static class NexusRequirementSnapshotSql
{
    internal const int ProviderRowIdLimit = 512;
    internal const int DisplayNameLimit = 1024;
    internal const int FailureMessageLimit = 2048;
    internal const int UrlLimit = 4096;
    internal const int NotesLimit = 8192;

    internal static async Task<long> LoadGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NexusRequirementSnapshotOwner owner,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT SuccessfulGeneration
            FROM NexusRequirementSnapshotStates
            WHERE OwnerGameId = $game AND OwnerModId = $mod
              AND Traversal = $traversal;
            """;
        AddOwnerParameters(command, owner);
        var value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt64(value);
    }

    internal static async Task UpsertSuccessfulStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NexusRequirementSnapshotOwner owner,
        long generation,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO NexusRequirementSnapshotStates (
                OwnerGameId, OwnerModId, Traversal,
                SuccessfulGeneration, LastSuccessfulAtUtc,
                LastAttemptAtUtc, LatestFailureKind,
                LatestFailureMessage)
            VALUES ($game, $mod, $traversal, $generation,
                    $timestamp, $timestamp, NULL, NULL)
            ON CONFLICT (OwnerGameId, OwnerModId, Traversal)
            DO UPDATE SET
                SuccessfulGeneration = excluded.SuccessfulGeneration,
                LastSuccessfulAtUtc = excluded.LastSuccessfulAtUtc,
                LastAttemptAtUtc = excluded.LastAttemptAtUtc,
                LatestFailureKind = NULL,
                LatestFailureMessage = NULL;
            """;
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task DeleteOwnedObservationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NexusRequirementSnapshotOwner owner,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM NexusRequirementObservations
            WHERE OwnerGameId = $game AND OwnerModId = $mod
              AND Traversal = $traversal;
            """;
        AddOwnerParameters(command, owner);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task InsertObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NexusRequirementSnapshotOwner owner,
        long generation,
        int ordinal,
        NexusModRequirementEdge edge,
        CancellationToken cancellationToken)
    {
        var nexusTarget = edge.Target as NexusModRequirementTarget;
        var externalTarget = edge.Target as NexusExternalRequirementTarget;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO NexusRequirementObservations (
                OwnerGameId, OwnerModId, Traversal,
                ObservationOrdinal, SuccessfulGeneration,
                SourceGameId, SourceModId, TargetKind,
                TargetGameId, TargetModId,
                SourceDisplayName, SourceProviderUrl, SourceClickableUrl,
                TargetDisplayName, TargetProviderUrl, TargetClickableUrl,
                SourceAdultContent, TargetAdultContent,
                ProviderRequirementId, Notes)
            VALUES (
                $ownerGame, $ownerMod, $traversal,
                $ordinal, $generation,
                $sourceGame, $sourceMod, $targetKind,
                $targetGame, $targetMod,
                $sourceName, $sourceUrl, $sourceClickable,
                $targetName, $targetUrl, $targetClickable,
                $sourceAdult, $targetAdult,
                $providerId, $notes);
            """;
        command.Parameters.AddWithValue("$ownerGame", owner.QueriedMod.GameId);
        command.Parameters.AddWithValue("$ownerMod", owner.QueriedMod.ModId);
        command.Parameters.AddWithValue("$traversal", (int)owner.Traversal);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$sourceGame", edge.Source.GameId);
        command.Parameters.AddWithValue("$sourceMod", edge.Source.ModId);
        command.Parameters.AddWithValue("$targetKind", (int)edge.Target.Kind);
        command.Parameters.AddWithValue(
            "$targetGame",
            nexusTarget?.Identity.GameId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$targetMod",
            nexusTarget?.Identity.ModId ?? (object)DBNull.Value);
        AddBoundText(
            command,
            "$sourceName",
            edge.SourceMetadata?.DisplayName,
            DisplayNameLimit);
        AddBoundText(
            command,
            "$sourceUrl",
            edge.SourceMetadata?.ProviderUrl,
            UrlLimit);
        AddClickableUrlParameter(
            command,
            "$sourceClickable",
            edge.SourceMetadata?.ClickableUrl);
        AddBoundText(
            command,
            "$targetName",
            nexusTarget?.DisplayName ?? externalTarget?.DisplayName,
            DisplayNameLimit);
        AddBoundText(
            command,
            "$targetUrl",
            nexusTarget?.ProviderUrl ?? externalTarget?.ProviderUrl,
            UrlLimit);
        AddClickableUrlParameter(
            command,
            "$targetClickable",
            nexusTarget?.ClickableUrl ?? externalTarget?.ClickableUrl);
        AddAdultContentParameter(
            command,
            "$sourceAdult",
            edge.SourceAdultContent);
        AddAdultContentParameter(
            command,
            "$targetAdult",
            edge.TargetAdultContent);
        AddBoundText(
            command,
            "$providerId",
            edge.ProviderRequirementId,
            ProviderRowIdLimit);
        AddBoundText(command, "$notes", edge.Notes, NotesLimit);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task RecordFailedAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NexusRequirementSnapshotOwner owner,
        NexusRequirementFailure failure,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO NexusRequirementSnapshotStates (
                OwnerGameId, OwnerModId, Traversal,
                SuccessfulGeneration, LastSuccessfulAtUtc,
                LastAttemptAtUtc, LatestFailureKind,
                LatestFailureMessage)
            VALUES ($game, $mod, $traversal, 0, NULL,
                    $attempted, $kind, $message)
            ON CONFLICT (OwnerGameId, OwnerModId, Traversal)
            DO UPDATE SET
                LastAttemptAtUtc = excluded.LastAttemptAtUtc,
                LatestFailureKind = excluded.LatestFailureKind,
                LatestFailureMessage = excluded.LatestFailureMessage;
            """;
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("$attempted", timestamp);
        command.Parameters.AddWithValue("$kind", (int)failure.Kind);
        command.Parameters.AddWithValue(
            "$message",
            Bound(failure.Message, FailureMessageLimit) ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<NexusRequirementSnapshotStateRow?>
        LoadStateAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            NexusRequirementSnapshotOwner owner,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT SuccessfulGeneration, LastSuccessfulAtUtc,
                   LastAttemptAtUtc, LatestFailureKind,
                   LatestFailureMessage
            FROM NexusRequirementSnapshotStates
            WHERE OwnerGameId = $game AND OwnerModId = $mod
              AND Traversal = $traversal;
            """;
        AddOwnerParameters(command, owner);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        try
        {
            var generation = reader.GetInt64(0);
            DateTimeOffset? successful = reader.IsDBNull(1)
                ? null
                : ParseTimestamp(reader.GetString(1));
            var attempted = ParseTimestamp(reader.GetString(2));
            NexusRequirementFailure? failure = null;
            if (!reader.IsDBNull(3))
            {
                var kindValue = reader.GetInt32(3);
                if (!Enum.IsDefined(typeof(NexusRequirementFailureKind), kindValue))
                    throw new InvalidDataException("Persisted failure kind is invalid.");
                failure = new(
                    (NexusRequirementFailureKind)kindValue,
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4));
            }
            if (generation < 0 ||
                (generation == 0) != (successful is null))
            {
                throw new InvalidDataException(
                    "Persisted Nexus snapshot state is inconsistent.");
            }
            return new(generation, successful, attempted, failure);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                "Persisted Nexus snapshot state is malformed.",
                exception);
        }
    }

    internal static async Task<IReadOnlyList<NexusModRequirementEdge>>
        LoadObservationsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            NexusRequirementSnapshotOwner owner,
            long expectedGeneration,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT ObservationOrdinal, SuccessfulGeneration,
                   SourceGameId, SourceModId, TargetKind,
                   TargetGameId, TargetModId,
                   SourceDisplayName, SourceProviderUrl,
                   SourceClickableUrl, TargetDisplayName,
                   TargetProviderUrl, TargetClickableUrl,
                   SourceAdultContent, TargetAdultContent,
                   ProviderRequirementId, Notes
            FROM NexusRequirementObservations
            WHERE OwnerGameId = $game AND OwnerModId = $mod
              AND Traversal = $traversal
            ORDER BY ObservationOrdinal;
            """;
        AddOwnerParameters(command, owner);
        var edges = new List<NexusModRequirementEdge>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(0) != edges.Count ||
                reader.GetInt64(1) != expectedGeneration)
            {
                throw new InvalidDataException(
                    "Persisted Nexus observation generation is inconsistent.");
            }
            edges.Add(ReadObservation(reader, owner));
        }
        return edges.AsReadOnly();
    }

    internal static async Task<IReadOnlyList<NexusModRequirementEdge>>
        LoadIncomingForwardObservationsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            NexusModIdentity targetMod,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT ObservationOrdinal, SuccessfulGeneration,
                   SourceGameId, SourceModId, TargetKind,
                   TargetGameId, TargetModId,
                   SourceDisplayName, SourceProviderUrl,
                   SourceClickableUrl, TargetDisplayName,
                   TargetProviderUrl, TargetClickableUrl,
                   SourceAdultContent, TargetAdultContent,
                   ProviderRequirementId, Notes,
                   OwnerGameId, OwnerModId, Traversal
            FROM NexusRequirementObservations
            WHERE Traversal = 0
              AND TargetKind = 0
              AND TargetGameId = $targetGameId
              AND TargetModId = $targetModId
            ORDER BY SourceGameId, SourceModId, ObservationOrdinal;
            """;
        command.Parameters.AddWithValue("$targetGameId", targetMod.GameId);
        command.Parameters.AddWithValue("$targetModId", targetMod.ModId);
        var edges = new List<NexusModRequirementEdge>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var owner = new NexusRequirementSnapshotOwner(
                new(reader.GetInt64(17), reader.GetInt64(18)),
                (NexusRequirementTraversal)reader.GetInt32(19));
            edges.Add(ReadObservation(reader, owner));
        }
        return edges.AsReadOnly();
    }

    private static NexusModRequirementEdge ReadObservation(
        SqliteDataReader reader,
        NexusRequirementSnapshotOwner owner)
    {
        var source = new NexusModIdentity(reader.GetInt64(2), reader.GetInt64(3));
        var kindValue = reader.GetInt32(4);
        NexusRequirementTarget target = kindValue switch
        {
            (int)NexusRequirementTargetKind.NexusMod
                when !reader.IsDBNull(5) && !reader.IsDBNull(6) =>
                new NexusModRequirementTarget(
                    new(reader.GetInt64(5), reader.GetInt64(6)),
                    GetNullableString(reader, 10),
                    GetNullableString(reader, 11),
                    ParseClickableUrl(GetNullableString(reader, 12))),
            (int)NexusRequirementTargetKind.External
                when reader.IsDBNull(5) && reader.IsDBNull(6) =>
                new NexusExternalRequirementTarget(
                    GetNullableString(reader, 10),
                    GetNullableString(reader, 11),
                    ParseClickableUrl(GetNullableString(reader, 12))),
            _ => throw new InvalidDataException(
                "Persisted Nexus target identity is invalid.")
        };
        NexusModRequirementEndpointMetadata? sourceMetadata = null;
        if (!reader.IsDBNull(7) || !reader.IsDBNull(8) || !reader.IsDBNull(9))
        {
            sourceMetadata = new(
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                ParseClickableUrl(GetNullableString(reader, 9)));
        }
        return new(
            source,
            target,
            sourceMetadata,
            GetNullableString(reader, 15),
            GetNullableString(reader, 16),
            owner.Traversal,
            ReadAdultContent(reader, 13),
            ReadAdultContent(reader, 14));
    }

    private static void AddOwnerParameters(
        SqliteCommand command,
        NexusRequirementSnapshotOwner owner)
    {
        command.Parameters.AddWithValue("$game", owner.QueriedMod.GameId);
        command.Parameters.AddWithValue("$mod", owner.QueriedMod.ModId);
        command.Parameters.AddWithValue("$traversal", (int)owner.Traversal);
    }

    private static void AddBoundText(
        SqliteCommand command,
        string parameter,
        string? value,
        int maximumLength) =>
        command.Parameters.AddWithValue(
            parameter,
            Bound(value, maximumLength) ?? (object)DBNull.Value);

    private static void AddClickableUrlParameter(
        SqliteCommand command,
        string parameter,
        Uri? value)
    {
        var serialized = value?.ToString();
        command.Parameters.AddWithValue(
            parameter,
            serialized is not null && serialized.Length <= UrlLimit
                ? serialized
                : (object)DBNull.Value);
    }

    private static void AddAdultContentParameter(
        SqliteCommand command,
        string parameter,
        NexusAdultContentClassification classification)
    {
        object value = classification switch
        {
            NexusAdultContentClassification.NonAdult => 0,
            NexusAdultContentClassification.Adult => 1,
            _ => DBNull.Value
        };
        command.Parameters.AddWithValue(parameter, value);
    }

    private static NexusAdultContentClassification ReadAdultContent(
        SqliteDataReader reader,
        int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return NexusAdultContentClassification.Unknown;
        return reader.GetInt32(ordinal) switch
        {
            0 => NexusAdultContentClassification.NonAdult,
            1 => NexusAdultContentClassification.Adult,
            _ => throw new InvalidDataException(
                "Persisted Nexus adult-content classification is invalid.")
        };
    }

    internal static string? Bound(string? value, int maximumLength)
    {
        if (value is null || value.Length <= maximumLength)
            return value;
        var length = maximumLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length];
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            throw new InvalidDataException(
                "Persisted Nexus snapshot timestamp is invalid.");
        }
        return parsed;
    }

    private static Uri? ParseClickableUrl(string? value)
    {
        if (value is null)
            return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException(
                "Persisted clickable Nexus requirement URL is invalid.");
        }
        return uri;
    }

    private static string? GetNullableString(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    internal static async Task InsertDismissalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NexusRequirementCanonicalKey key,
        string timestamp,
        CancellationToken cancellationToken)
    {
        if (key.NexusTargetIdentity is not { } target)
            throw new ArgumentException("Cannot dismiss an external requirement without a target identity.", nameof(key));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO NexusRequirementDismissals (
                SourceGameId, SourceModId, TargetGameId, TargetModId, DismissedAtUtc)
            VALUES ($sourceGame, $sourceMod, $targetGame, $targetMod, $timestamp)
            ON CONFLICT (SourceGameId, SourceModId, TargetGameId, TargetModId)
            DO UPDATE SET DismissedAtUtc = excluded.DismissedAtUtc;
            """;
        command.Parameters.AddWithValue("$sourceGame", key.Source.GameId);
        command.Parameters.AddWithValue("$sourceMod", key.Source.ModId);
        command.Parameters.AddWithValue("$targetGame", target.GameId);
        command.Parameters.AddWithValue("$targetMod", target.ModId);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task DeleteDismissalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NexusRequirementCanonicalKey key,
        CancellationToken cancellationToken)
    {
        if (key.NexusTargetIdentity is not { } target)
            return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM NexusRequirementDismissals
            WHERE SourceGameId = $sourceGame AND SourceModId = $sourceMod
              AND TargetGameId = $targetGame AND TargetModId = $targetMod;
            """;
        command.Parameters.AddWithValue("$sourceGame", key.Source.GameId);
        command.Parameters.AddWithValue("$sourceMod", key.Source.ModId);
        command.Parameters.AddWithValue("$targetGame", target.GameId);
        command.Parameters.AddWithValue("$targetMod", target.ModId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task DeleteDismissalsForKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<NexusRequirementCanonicalKey> keys,
        CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            await DeleteDismissalAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<IReadOnlySet<NexusRequirementCanonicalKey>> LoadDismissalsForModAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NexusModIdentity mod,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT SourceGameId, SourceModId, TargetGameId, TargetModId
            FROM NexusRequirementDismissals
            WHERE (SourceGameId = $game AND SourceModId = $mod)
               OR (TargetGameId = $game AND TargetModId = $mod);
            """;
        command.Parameters.AddWithValue("$game", mod.GameId);
        command.Parameters.AddWithValue("$mod", mod.ModId);
        var set = new HashSet<NexusRequirementCanonicalKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var source = new NexusModIdentity(reader.GetInt64(0), reader.GetInt64(1));
            var target = new NexusModIdentity(reader.GetInt64(2), reader.GetInt64(3));
            set.Add(new NexusRequirementCanonicalKey(source, NexusRequirementTargetKind.NexusMod, target));
        }
        return set;
    }

    internal static async Task<IReadOnlySet<NexusRequirementCanonicalKey>> LoadAllDismissalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT SourceGameId, SourceModId, TargetGameId, TargetModId
            FROM NexusRequirementDismissals;
            """;
        var set = new HashSet<NexusRequirementCanonicalKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var source = new NexusModIdentity(reader.GetInt64(0), reader.GetInt64(1));
            var target = new NexusModIdentity(reader.GetInt64(2), reader.GetInt64(3));
            set.Add(new NexusRequirementCanonicalKey(source, NexusRequirementTargetKind.NexusMod, target));
        }
        return set;
    }
}
