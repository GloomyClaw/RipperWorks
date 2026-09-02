using Microsoft.Data.Sqlite;

namespace RipperWorks.Infrastructure;

internal sealed class StartupMigrationStoreRunner
{
    private readonly string _store;
    private readonly string _databasePath;
    private readonly int _minimumSchemaVersion;
    private readonly int _maximumSchemaVersion;
    private readonly bool _allowMissing;
    private readonly IReadOnlyList<IStartupMigrationStep> _steps;
    private readonly StartupMigrationContext _context;
    private readonly Func<DateTimeOffset> _clock;
    private readonly StartupMigrationFault? _fault;
    private readonly int _busyTimeoutMilliseconds;

    public StartupMigrationStoreRunner(
        string store,
        string databasePath,
        int minimumSchemaVersion,
        int maximumSchemaVersion,
        bool allowMissing,
        IReadOnlyList<IStartupMigrationStep> steps,
        StartupMigrationContext context,
        Func<DateTimeOffset> clock,
        StartupMigrationFault? fault,
        int busyTimeoutMilliseconds)
    {
        _store = store;
        _databasePath = Path.GetFullPath(databasePath);
        _minimumSchemaVersion = minimumSchemaVersion;
        _maximumSchemaVersion = maximumSchemaVersion;
        _allowMissing = allowMissing;
        _steps = steps;
        _context = context;
        _clock = clock;
        _fault = fault;
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
    }

    public async Task<IReadOnlyList<StartupMigrationDryRun>> DryRunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection =
                await OpenForInspectionAsync(cancellationToken);
            return await InspectAllAsync(
                connection,
                null,
                cancellationToken);
        }
        catch (StartupMigrationBlockedException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or
            UnauthorizedAccessException)
        {
            throw Blocked("dry-run", exception);
        }
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(
        IReadOnlyList<StartupMigrationDryRun> expected,
        CancellationToken cancellationToken)
    {
        ValidatePlanShape(expected);
        var applied = new List<string>();
        foreach (var step in _steps)
        {
            var report = expected.Single(value =>
                value.MigrationId == step.Id);
            if (!report.Pending)
                continue;
            await ApplyStepAsync(
                step,
                report,
                cancellationToken);
            applied.Add(step.Id);
        }
        return applied;
    }

    public async Task<StartupStoreHealthReport> HealthAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection =
                await OpenForInspectionAsync(cancellationToken);
            var version = await ValidateStoreAsync(
                connection,
                null,
                cancellationToken);
            var ledger = await ReadKnownLedgerAsync(
                connection,
                null,
                cancellationToken);
            return new(
                _store,
                version,
                await SqliteStartupMigration.QuickCheckAsync(
                    connection,
                    cancellationToken),
                ledger.Keys.OrderBy(
                    value => value,
                    StringComparer.Ordinal).ToArray());
        }
        catch (StartupMigrationBlockedException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or
            UnauthorizedAccessException)
        {
            throw Blocked("health check", exception);
        }
    }

    private async Task ApplyStepAsync(
        IStartupMigrationStep step,
        StartupMigrationDryRun expected,
        CancellationToken cancellationToken)
    {
        try
        {
            Hit(step.Id, StartupMigrationBarrier.BeforeDryRun);
            var fresh = (await DryRunAsync(cancellationToken))
                .Single(report => report.MigrationId == step.Id);
            ValidateUnchanged(expected, fresh);
            EnsureApplicable(fresh);
            Hit(
                step.Id,
                StartupMigrationBarrier.AfterDryRunBeforeTransaction);
            await using var connection =
                await OpenForWriteAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction =
                connection.BeginTransaction(deferred: false);
            Hit(
                step.Id,
                StartupMigrationBarrier.AfterTransactionBegins);
            await StartupMigrationLedger.EnsureCreatedAsync(
                connection,
                transaction,
                cancellationToken);
            var version = await ValidateStoreAsync(
                connection,
                transaction,
                cancellationToken);
            var ledger = await ReadKnownLedgerAsync(
                connection,
                transaction,
                cancellationToken);
            if (ledger.ContainsKey(step.Id))
            {
                throw new StartupMigrationBlockedException(
                    $"{step.Id} completion changed after dry-run.");
            }
            var inspection = await step.InspectAsync(
                connection,
                transaction,
                version,
                cancellationToken);
            var insideTransaction = CreateReport(
                step,
                inspection,
                null);
            ValidateUnchanged(expected, insideTransaction);
            EnsureApplicable(insideTransaction);
            var appliedUtc = _clock().ToUniversalTime();
            await step.ApplyAsync(
                connection,
                transaction,
                appliedUtc,
                cancellationToken);
            Hit(
                step.Id,
                StartupMigrationBarrier.AfterDataWriteBeforeLedger);
            await StartupMigrationLedger.InsertAsync(
                connection,
                transaction,
                insideTransaction,
                appliedUtc,
                cancellationToken);
            Hit(
                step.Id,
                StartupMigrationBarrier.AfterLedgerWriteBeforeCommit);
            await transaction.CommitAsync(cancellationToken);
            Hit(
                step.Id,
                StartupMigrationBarrier.ImmediatelyAfterCommit);
        }
        catch (StartupMigrationBlockedException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or
            UnauthorizedAccessException)
        {
            throw Blocked($"apply {step.Id}", exception);
        }
    }

    private async Task<IReadOnlyList<StartupMigrationDryRun>>
        InspectAllAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            CancellationToken cancellationToken)
    {
        var version = await ValidateStoreAsync(
            connection,
            transaction,
            cancellationToken);
        var ledger = await ReadKnownLedgerAsync(
            connection,
            transaction,
            cancellationToken);
        var reports = new List<StartupMigrationDryRun>(_steps.Count);
        foreach (var step in _steps)
        {
            if (ledger.TryGetValue(step.Id, out var completed))
            {
                reports.Add(CreateCompletedReport(
                    step,
                    version,
                    completed));
                continue;
            }
            var inspection = await step.InspectAsync(
                connection,
                transaction,
                version,
                cancellationToken);
            reports.Add(CreateReport(step, inspection, null));
        }
        return reports;
    }

    private StartupMigrationDryRun CreateReport(
        IStartupMigrationStep step,
        StartupMigrationInspection inspection,
        DateTimeOffset? createdAtUtc)
    {
        var reportHash = SqliteStartupMigration.Hash(
        [
            step.Id,
            _store,
            inspection.SchemaVersion.ToString(),
            inspection.InputFingerprint,
            inspection.Inserts.ToString(),
            inspection.Updates.ToString(),
            inspection.Deletes.ToString(),
            inspection.Ambiguities.ToString(),
            string.Join(",", inspection.Warnings),
            inspection.Compatible.ToString(),
            _context.BackupId,
            _context.CodeVersion
        ]);
        return new()
        {
            MigrationId = step.Id,
            Store = _store,
            SchemaVersion = inspection.SchemaVersion,
            Pending = true,
            InputFingerprint = inspection.InputFingerprint,
            EstimatedInserts = inspection.Inserts,
            EstimatedUpdates = inspection.Updates,
            EstimatedDeletes = inspection.Deletes,
            AmbiguityCount = inspection.Ambiguities,
            Warnings = inspection.Warnings,
            Compatible = inspection.Compatible,
            BackupId = _context.BackupId,
            CreatedAtUtc = createdAtUtc ?? _clock().ToUniversalTime(),
            CodeVersion = _context.CodeVersion,
            ReportHash = reportHash
        };
    }

    private StartupMigrationDryRun CreateCompletedReport(
        IStartupMigrationStep step,
        int schemaVersion,
        StartupMigrationLedgerEntry completed) =>
        new()
        {
            MigrationId = step.Id,
            Store = _store,
            SchemaVersion = schemaVersion,
            Pending = false,
            InputFingerprint = completed.InputFingerprint,
            EstimatedInserts = 0,
            EstimatedUpdates = 0,
            EstimatedDeletes = 0,
            AmbiguityCount = 0,
            Warnings = ["ALREADY_APPLIED"],
            Compatible = true,
            BackupId = _context.BackupId,
            CreatedAtUtc = DateTimeOffset.TryParse(
                completed.AppliedUtc,
                out var applied)
                ? applied
                : _clock().ToUniversalTime(),
            CodeVersion = completed.CodeVersion,
            ReportHash = completed.ReportHash
        };

    private async Task<int> ValidateStoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var version = await SqliteStartupMigration.UserVersionAsync(
            connection,
            transaction,
            cancellationToken);
        if (version < _minimumSchemaVersion ||
            version > _maximumSchemaVersion)
        {
            throw new StartupMigrationBlockedException(
                $"{_store} schema {version} is outside supported range " +
                $"{_minimumSchemaVersion}..{_maximumSchemaVersion}.");
        }
        if (transaction is null)
        {
            var quickCheck = await SqliteStartupMigration.QuickCheckAsync(
                connection,
                cancellationToken);
            if (!string.Equals(
                    quickCheck,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new StartupMigrationBlockedException(
                    $"{_store} quick_check failed.");
            }
        }
        return version;
    }

    private async Task<IReadOnlyDictionary<string,
        StartupMigrationLedgerEntry>> ReadKnownLedgerAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var ledger = await StartupMigrationLedger.ReadAsync(
            connection,
            transaction,
            cancellationToken);
        var known = _steps.Select(step => step.Id)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = ledger.Keys
            .Where(id => !known.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new StartupMigrationBlockedException(
                $"{_store} ledger contains unknown migration IDs: " +
                string.Join(", ", unknown) + ".");
        }
        return ledger;
    }

    private async Task<SqliteConnection> OpenForInspectionAsync(
        CancellationToken cancellationToken)
    {
        SqliteConnection connection;
        if (!File.Exists(_databasePath))
        {
            if (!_allowMissing)
            {
                throw new StartupMigrationBlockedException(
                    $"{_store} database is missing.");
            }
            connection = new SqliteConnection("Data Source=:memory:");
        }
        else
        {
            connection = new SqliteConnection(
                SqliteStartupMigration.ConnectionString(
                    _databasePath,
                    SqliteOpenMode.ReadOnly));
        }
        connection.DefaultTimeout = CommandTimeoutSeconds();
        await connection.OpenAsync(cancellationToken);
        await SqliteStartupMigration.ConfigureAsync(
            connection,
            true,
            _busyTimeoutMilliseconds,
            cancellationToken);
        return connection;
    }

    private async Task<SqliteConnection> OpenForWriteAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath) && !_allowMissing)
        {
            throw new StartupMigrationBlockedException(
                $"{_store} database disappeared after dry-run.");
        }
        Directory.CreateDirectory(
            Path.GetDirectoryName(_databasePath)
            ?? throw new StartupMigrationBlockedException(
                $"{_store} database directory is missing."));
        var connection = new SqliteConnection(
            SqliteStartupMigration.ConnectionString(
                _databasePath,
                _allowMissing
                    ? SqliteOpenMode.ReadWriteCreate
                    : SqliteOpenMode.ReadWrite));
        connection.DefaultTimeout = CommandTimeoutSeconds();
        await connection.OpenAsync(cancellationToken);
        await SqliteStartupMigration.ConfigureAsync(
            connection,
            false,
            _busyTimeoutMilliseconds,
            cancellationToken);
        return connection;
    }

    private void ValidatePlanShape(
        IReadOnlyList<StartupMigrationDryRun> expected)
    {
        var expectedIds = expected.Select(report => report.MigrationId)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var actualIds = _steps.Select(step => step.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!expectedIds.SequenceEqual(actualIds, StringComparer.Ordinal) ||
            expected.Any(report => report.Store != _store))
        {
            throw new StartupMigrationBlockedException(
                $"{_store} apply plan does not match the known migration set.");
        }
    }

    private static void ValidateUnchanged(
        StartupMigrationDryRun expected,
        StartupMigrationDryRun actual)
    {
        if (expected.MigrationId != actual.MigrationId ||
            expected.Store != actual.Store ||
            expected.SchemaVersion != actual.SchemaVersion ||
            expected.Pending != actual.Pending ||
            expected.InputFingerprint != actual.InputFingerprint ||
            expected.EstimatedInserts != actual.EstimatedInserts ||
            expected.EstimatedUpdates != actual.EstimatedUpdates ||
            expected.EstimatedDeletes != actual.EstimatedDeletes ||
            expected.AmbiguityCount != actual.AmbiguityCount ||
            expected.Compatible != actual.Compatible ||
            expected.ReportHash != actual.ReportHash)
        {
            throw new StartupMigrationBlockedException(
                $"{expected.MigrationId} input changed after dry-run.");
        }
    }

    private static void EnsureApplicable(StartupMigrationDryRun report)
    {
        if (!report.Pending || !report.Compatible ||
            report.AmbiguityCount != 0)
        {
            throw new StartupMigrationBlockedException(
                $"{report.MigrationId} is not safe to apply.");
        }
    }

    private void Hit(string id, StartupMigrationBarrier barrier) =>
        _fault?.Invoke(id, barrier);

    private int CommandTimeoutSeconds() =>
        Math.Max(
            1,
            (int)Math.Ceiling(_busyTimeoutMilliseconds / 1000d));

    private StartupMigrationBlockedException Blocked(
        string operation,
        Exception exception) =>
        new(
            $"{_store} startup migration {operation} failed safely " +
            $"({exception.GetType().Name}).",
            exception);
}
