using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Explicit-root durable journal v2. Never touches legacy InstallOperations.
/// Schema: JournalMeta, Operations, OperationSteps (FormatVersion=1).
/// Open is fail-closed for foreign/corrupt/unsupported databases.
/// </summary>
public sealed class SqliteGameOperationJournal : IGameOperationJournal, IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeSync = new();
    private SqliteConnection? _connection;
    private bool _opened;
    private bool _disposed;
    private Task? _disposeTask;
    private long _successfulCommitCount;
    private long _openTicks;
    private long _transitionTicks;
    private long _terminalTransitionTicks;

    internal string? TestOnlyFaultPhase { get; set; }

    public SqliteGameOperationJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public int FormatVersion => GameOperationJournalVersions.FormatVersion;
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Observed successful SQLite transaction commits (schema/init, insert,
    /// transitions, step intent/applied/verified). Durable cancel updates
    /// without a multi-statement transaction also increment this when they
    /// complete successfully.
    /// </summary>
    public long ObservedSuccessfulCommits =>
        Volatile.Read(ref _successfulCommitCount);

    internal SqliteGameOperationJournalMetrics ObservedMetrics => new(
        ObservedSuccessfulCommits,
        ToTimeSpan(Volatile.Read(ref _openTicks)),
        ToTimeSpan(Volatile.Read(ref _transitionTicks)),
        ToTimeSpan(Volatile.Read(ref _terminalTransitionTicks)));

    private void NoteSuccessfulCommit() =>
        Interlocked.Increment(ref _successfulCommitCount);

    private static TimeSpan ToTimeSpan(long ticks) =>
        TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_opened)
                return;

            var started = Stopwatch.GetTimestamp();
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var fileExisted = File.Exists(_databasePath);
            var length = fileExisted ? new FileInfo(_databasePath).Length : 0L;
            var needsInit = !fileExisted || length == 0;

            _connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private
                }.ToString());
            try
            {
                await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (var pragma = _connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys = ON;";
                    await pragma.ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                if (fileExisted && length > 0)
                {
                    var userTables = await SqliteGameOperationJournalSchema
                        .ListUserTablesAsync(_connection, cancellationToken)
                        .ConfigureAwait(false);
                    var version = await SqliteGameOperationJournalSchema
                        .TryReadFormatVersionAsync(_connection, cancellationToken)
                        .ConfigureAwait(false);

                    if (version is null && userTables.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "CorruptedOrForeignJournal");
                    }

                    if (version is null && userTables.Count == 0)
                    {
                        // Empty SQLite file (no user tables): allow init.
                        needsInit = true;
                    }
                    else if (version is int existingVersion &&
                             existingVersion != FormatVersion)
                    {
                        throw new InvalidOperationException(
                            "UnsupportedJournalVersion");
                    }
                    else if (version == FormatVersion)
                    {
                        await SqliteGameOperationJournalSchema
                            .ValidateV1SchemaAsync(_connection, cancellationToken)
                            .ConfigureAwait(false);
                        // Known-good v1: do not CREATE IF NOT EXISTS patching.
                        needsInit = false;
                    }
                }

                if (needsInit)
                {
                    await using var tx = (SqliteTransaction)await _connection
                        .BeginTransactionAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await SqliteGameOperationJournalSchema.EnsureSchemaAsync(
                            _connection, tx, cancellationToken)
                        .ConfigureAwait(false);
                    await SqliteGameOperationJournalSchema.EnsureFormatVersionAsync(
                            _connection, tx, FormatVersion, cancellationToken)
                        .ConfigureAwait(false);
                    await tx.CommitAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    NoteSuccessfulCommit();
                }

                _opened = true;
                Interlocked.Add(
                    ref _openTicks,
                    Stopwatch.GetTimestamp() - started);
            }
            catch
            {
                try { await _connection.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
                _connection = null;
                _opened = false;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        if (_disposed)
            return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            _opened = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GameOperationRecord?> FindByIdempotencyAsync(
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Connection.CreateCommand();
            command.CommandText =
                """
                SELECT OperationId FROM Operations
                WHERE CanonicalProfileKey = $profile AND IdempotencyKey = $key
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$profile", profileKey.Value);
            command.Parameters.AddWithValue("$key", idempotencyKey.Value);
            var id = await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (id is null or DBNull)
                return null;
            return await SqliteGameOperationJournalSupport.LoadCoreAsync(
                    Connection,
                    OperationId.Parse(Convert.ToString(id)!),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GameOperationRecord> InsertAcceptedAsync(
        OperationId operationId,
        CanonicalProfileKey profileKey,
        IdempotencyKey idempotencyKey,
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Connection;
            await using var tx = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await ThrowFaultIfAsync("AfterBeginInsert", cancellationToken)
                .ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText =
                    """
                    INSERT INTO Operations (
                        OperationId, CanonicalProfileKey, IdempotencyKey,
                        OperationKind, PlanVersion, PlanHash, SerializedPlan,
                        State, Phase, CreatedAtUtc, StartedAtUtc, CompletedAtUtc,
                        CancellationRequested, ErrorCode, RedactedErrorDetail,
                        LastSequence, JournalFormatVersion)
                    VALUES (
                        $id, $profile, $key, $kind, $planVersion, $planHash,
                        $plan, $state, $phase, $created, NULL, NULL,
                        0, NULL, NULL, 0, $fmt);
                    """;
                command.Parameters.AddWithValue("$id", operationId.ToString());
                command.Parameters.AddWithValue("$profile", profileKey.Value);
                command.Parameters.AddWithValue("$key", idempotencyKey.Value);
                command.Parameters.AddWithValue("$kind", plan.OperationKind.ToString());
                command.Parameters.AddWithValue("$planVersion", plan.PlanVersion);
                command.Parameters.AddWithValue("$planHash", plan.PlanHash);
                command.Parameters.AddWithValue("$plan", plan.Serialize());
                command.Parameters.AddWithValue("$state", GameOperationState.Accepted.ToString());
                command.Parameters.AddWithValue("$phase", GameOperationPhase.Accepted.ToString());
                command.Parameters.AddWithValue(
                    "$created",
                    SqliteGameOperationJournalSupport.FormatUtc(DateTime.UtcNow));
                command.Parameters.AddWithValue("$fmt", FormatVersion);
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await ThrowFaultIfAsync("BeforeCommitInsert", cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            NoteSuccessfulCommit();
            return (await SqliteGameOperationJournalSupport.LoadCoreAsync(
                    Connection, operationId, CancellationToken.None)
                .ConfigureAwait(false))!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TransitionAsync(
        OperationId operationId,
        GameOperationState from,
        GameOperationState to,
        GameOperationPhase phase,
        string? errorCode = null,
        string? redactedDetail = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!SqliteGameOperationJournalSupport.IsValidTransition(from, to))
                throw new InvalidOperationException("InvalidStateTransition");

            await using var tx = (SqliteTransaction)await Connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await SqliteGameOperationJournalSupport.ApplyTransitionAsync(
                    Connection, tx, operationId, from, to, phase,
                    errorCode, redactedDetail, cancellationToken)
                .ConfigureAwait(false);
            await ThrowFaultIfAsync("BeforeCommitTransition:" + to, cancellationToken)
                .ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            NoteSuccessfulCommit();
        }
        finally
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref _transitionTicks, elapsed);
            if (phase == GameOperationPhase.Terminal)
                Interlocked.Add(ref _terminalTransitionTicks, elapsed);
            _gate.Release();
        }
    }

    public async Task RecordStepIntentAsync(
        OperationId operationId,
        GameOperationStepSpec step,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await Connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await SqliteGameOperationJournalSupport.RecordStepIntentCoreAsync(
                    Connection, tx, operationId, step, cancellationToken)
                .ConfigureAwait(false);
            await ThrowFaultIfAsync("BeforeCommitStepIntent", cancellationToken)
                .ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            NoteSuccessfulCommit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordStepAppliedAsync(
        OperationId operationId,
        int sequence,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await Connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await SqliteGameOperationJournalSupport.AdvanceStepStateAsync(
                    Connection, tx, operationId, sequence,
                    GameOperationStepState.IntentRecorded,
                    GameOperationStepState.Applied,
                    "AppliedAtUtc",
                    cancellationToken)
                .ConfigureAwait(false);
            await ThrowFaultIfAsync("BeforeCommitStepApplied", cancellationToken)
                .ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            NoteSuccessfulCommit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordStepVerifiedAsync(
        OperationId operationId,
        int sequence,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await Connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await SqliteGameOperationJournalSupport.AdvanceStepStateAsync(
                    Connection, tx, operationId, sequence,
                    GameOperationStepState.Applied,
                    GameOperationStepState.Verified,
                    "VerifiedAtUtc",
                    cancellationToken)
                .ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            NoteSuccessfulCommit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetCancellationRequestedAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Connection.CreateCommand();
            command.CommandText =
                """
                UPDATE Operations SET CancellationRequested = 1
                WHERE OperationId = $id;
                """;
            command.Parameters.AddWithValue("$id", operationId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            NoteSuccessfulCommit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GameOperationRecord?> LoadAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SqliteGameOperationJournalSupport.LoadCoreAsync(
                    Connection, operationId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<GameOperationRecord>> LoadByProfileAsync(
        CanonicalProfileKey profileKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = Connection.CreateCommand();
            command.CommandText =
                """
                SELECT OperationId FROM Operations
                WHERE CanonicalProfileKey = $profile
                ORDER BY CreatedAtUtc ASC, OperationId ASC;
                """;
            command.Parameters.AddWithValue("$profile", profileKey.Value);
            var ids = new List<OperationId>();
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(OperationId.Parse(reader.GetString(0)));
            var list = new List<GameOperationRecord>();
            foreach (var id in ids)
            {
                var row = await SqliteGameOperationJournalSupport.LoadCoreAsync(
                        Connection, id, cancellationToken)
                    .ConfigureAwait(false);
                if (row is not null)
                    list.Add(row);
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasIncompletePostCommitAsync(
        CanonicalProfileKey profileKey,
        CancellationToken cancellationToken = default)
    {
        var rows = await LoadByProfileAsync(profileKey, cancellationToken)
            .ConfigureAwait(false);
        return rows.Any(r =>
            r.State is GameOperationState.MutationStarted
                or GameOperationState.Executing
                or GameOperationState.Finalizing
                or GameOperationState.RecoveryRequired);
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeSync)
        {
            if (_disposeTask is not null)
            {
                disposeTask = _disposeTask;
            }
            else
            {
                _disposeTask = DisposeCoreAsync();
                disposeTask = _disposeTask;
            }
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed)
            return;
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            try
            {
                _gate.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // concurrent dispose path: gate already released/disposed
            }
        }
    }

    private SqliteConnection Connection =>
        _connection ?? throw new InvalidOperationException("JournalClosed");

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (!_opened)
            await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task ThrowFaultIfAsync(string phase, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (string.Equals(TestOnlyFaultPhase, phase, StringComparison.Ordinal))
            throw new IOException("TestOnlyFault:" + phase);
        return Task.CompletedTask;
    }
}

internal sealed record SqliteGameOperationJournalMetrics(
    long SuccessfulCommits,
    TimeSpan OpenAndSchemaDuration,
    TimeSpan TransitionDuration,
    TimeSpan TerminalPersistenceDuration);
