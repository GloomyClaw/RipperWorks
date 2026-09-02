using System.Globalization;
using Microsoft.Data.Sqlite;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed class NexusRequirementSnapshotStore(string catalogDatabasePath)
{
    private readonly string _databasePath =
        Path.GetFullPath(catalogDatabasePath);

    public async Task SaveCompleteSnapshotAsync(
        NexusRequirementSnapshot snapshot,
        DateTimeOffset fetchedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = FormatTimestamp(fetchedAtUtc);

        await using var connection = await OpenInitializedAsync(
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            var generation = checked(
                await NexusRequirementSnapshotSql.LoadGenerationAsync(
                    connection,
                    transaction,
                    snapshot.Owner,
                    cancellationToken).ConfigureAwait(false) + 1);
            await NexusRequirementSnapshotSql.UpsertSuccessfulStateAsync(
                connection,
                transaction,
                snapshot.Owner,
                generation,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await NexusRequirementSnapshotSql.DeleteOwnedObservationsAsync(
                connection,
                transaction,
                snapshot.Owner,
                cancellationToken).ConfigureAwait(false);
            for (var ordinal = 0; ordinal < snapshot.Edges.Count; ordinal++)
            {
                await NexusRequirementSnapshotSql.InsertObservationAsync(
                    connection,
                    transaction,
                    snapshot.Owner,
                    generation,
                    ordinal,
                    snapshot.Edges[ordinal],
                    cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await RollbackPreservingOriginalAsync(transaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task RecordFailedAttemptAsync(
        NexusRequirementSnapshotOwner owner,
        NexusRequirementFailure failure,
        DateTimeOffset attemptedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(failure);
        if (!Enum.IsDefined(failure.Kind))
            throw new ArgumentOutOfRangeException(nameof(failure));
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = FormatTimestamp(attemptedAtUtc);

        await using var connection = await OpenInitializedAsync(
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            await NexusRequirementSnapshotSql.RecordFailedAttemptAsync(
                connection,
                transaction,
                owner,
                failure,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await RollbackPreservingOriginalAsync(transaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<NexusRequirementSnapshotRecord?> LoadSnapshotAsync(
        NexusRequirementSnapshotOwner owner,
        CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        await using var connection = await OpenInitializedAsync(
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            var state = await NexusRequirementSnapshotSql.LoadStateAsync(
                connection,
                transaction,
                owner,
                cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            NexusRequirementSnapshot? snapshot = null;
            if (state.SuccessfulGeneration > 0)
            {
                var edges = await NexusRequirementSnapshotSql
                    .LoadObservationsAsync(
                        connection,
                        transaction,
                        owner,
                        state.SuccessfulGeneration,
                        cancellationToken).ConfigureAwait(false);
                snapshot = new(owner, edges);
                try
                {
                    ValidateSnapshot(snapshot);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        "Persisted Nexus requirement snapshot is invalid.",
                        exception);
                }
            }
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new(
                owner,
                state.SuccessfulGeneration,
                state.LastSuccessfulAtUtc,
                state.LastAttemptAtUtc,
                state.LatestFailure,
                snapshot);
        }
        catch
        {
            await RollbackPreservingOriginalAsync(transaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<NexusModRequirementEdge>> LoadIncomingForwardObservationsAsync(
        NexusModIdentity targetMod,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidIdentity(targetMod))
            throw new ArgumentException("Invalid mod identity.", nameof(targetMod));
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await OpenInitializedAsync(
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            var edges = await NexusRequirementSnapshotSql
                .LoadIncomingForwardObservationsAsync(
                    connection,
                    transaction,
                    targetMod,
                    cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return edges;
        }
        catch
        {
            await RollbackPreservingOriginalAsync(transaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task DismissRelationAsync(
        NexusRequirementCanonicalKey key,
        DateTimeOffset dismissedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateDismissalKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = FormatTimestamp(dismissedAtUtc);

        await using var connection = await OpenInitializedAsync(
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            await NexusRequirementSnapshotSql.InsertDismissalAsync(
                connection,
                transaction,
                key,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await RollbackPreservingOriginalAsync(transaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task ClearDismissalsForEdgesAsync(
        IEnumerable<NexusRequirementCanonicalKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var keyList = keys.Where(k =>
            k.TargetKind == NexusRequirementTargetKind.NexusMod &&
            k.NexusTargetIdentity is not null).ToArray();
        if (keyList.Length == 0)
            return;
        foreach (var key in keyList)
        {
            ValidateDismissalKey(key);
        }
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await OpenInitializedAsync(
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            await NexusRequirementSnapshotSql.DeleteDismissalsForKeysAsync(
                connection,
                transaction,
                keyList,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await RollbackPreservingOriginalAsync(transaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlySet<NexusRequirementCanonicalKey>> LoadDismissalsForModAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidIdentity(mod))
            throw new ArgumentException("Invalid mod identity.", nameof(mod));
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await OpenInitializedAsync(
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            var result = await NexusRequirementSnapshotSql.LoadDismissalsForModAsync(
                connection,
                transaction,
                mod,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch
        {
            await RollbackPreservingOriginalAsync(transaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlySet<NexusRequirementCanonicalKey>> LoadAllDismissalsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await OpenInitializedAsync(
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            var result = await NexusRequirementSnapshotSql.LoadAllDismissalsAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch
        {
            await RollbackPreservingOriginalAsync(transaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenInitializedAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await CatalogDatabaseSchema.InitializeAsync(
                connection,
                _databasePath,
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateSnapshot(NexusRequirementSnapshot snapshot)
    {
        ValidateOwner(snapshot.Owner);
        var canonical = new HashSet<NexusRequirementCanonicalKey>();
        foreach (var edge in snapshot.Edges)
        {
            if (edge.ObservedThrough != snapshot.Owner.Traversal)
            {
                throw new ArgumentException(
                    "Snapshot edge traversal does not match its owner.",
                    nameof(snapshot));
            }
            if (!IsValidIdentity(edge.Source))
            {
                throw new ArgumentException(
                    "Snapshot source identity is invalid.",
                    nameof(snapshot));
            }
            if (snapshot.Owner.Traversal ==
                NexusRequirementTraversal.ForwardRequirements)
            {
                if (edge.Source != snapshot.Owner.QueriedMod)
                {
                    throw new ArgumentException(
                        "Forward snapshot source does not match its owner.",
                        nameof(snapshot));
                }
            }
            else if (edge.Target is not NexusModRequirementTarget reverseTarget ||
                reverseTarget.Identity != snapshot.Owner.QueriedMod)
            {
                throw new ArgumentException(
                    "Reverse snapshot target does not match its owner.",
                    nameof(snapshot));
            }

            if (edge.Target is NexusModRequirementTarget nexusTarget)
            {
                if (!IsValidIdentity(nexusTarget.Identity) ||
                    edge.CanonicalKey is not { } key ||
                    !canonical.Add(key))
                {
                    throw new ArgumentException(
                        "Snapshot contains an invalid or duplicate canonical edge.",
                        nameof(snapshot));
                }
            }
            else if (edge.Target is not NexusExternalRequirementTarget ||
                snapshot.Owner.Traversal !=
                    NexusRequirementTraversal.ForwardRequirements)
            {
                throw new ArgumentException(
                    "Snapshot contains an invalid target kind.",
                    nameof(snapshot));
            }
        }
    }

    private static void ValidateOwner(NexusRequirementSnapshotOwner owner)
    {
        if (!IsValidIdentity(owner.QueriedMod) ||
            !Enum.IsDefined(owner.Traversal))
        {
            throw new ArgumentException(
                "Nexus snapshot owner identity or traversal is invalid.",
                nameof(owner));
        }
    }

    private static bool IsValidIdentity(NexusModIdentity identity) =>
        identity.GameId > 0 && identity.ModId > 0;

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateDismissalKey(NexusRequirementCanonicalKey key)
    {
        if (!IsValidIdentity(key.Source) ||
            key.TargetKind != NexusRequirementTargetKind.NexusMod ||
            key.NexusTargetIdentity is not { } target ||
            !IsValidIdentity(target))
        {
            throw new ArgumentException(
                "Nexus requirement dismissal canonical key is invalid.",
                nameof(key));
        }
    }

    private static async Task RollbackPreservingOriginalAsync(
        SqliteTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original persistence exception.
        }
    }
}
