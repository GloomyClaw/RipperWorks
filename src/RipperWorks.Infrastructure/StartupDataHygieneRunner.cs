using Microsoft.Data.Sqlite;

namespace RipperWorks.Infrastructure;

public sealed class StartupDataHygieneRunner
{
    private readonly string _organizerPath;
    private readonly string _catalogPath;
    private readonly StartupMigrationContext _context;
    private readonly Func<DateTimeOffset> _clock;
    private readonly StartupMigrationFault? _fault;
    private readonly int _busyTimeoutMilliseconds;
    private readonly int _minimumCatalogSchemaVersion;
    private readonly int _maximumCatalogSchemaVersion;

    public StartupDataHygieneRunner(
        string organizerPath,
        string catalogPath,
        StartupMigrationContext context,
        int maximumCatalogSchemaVersion,
        int minimumCatalogSchemaVersion = 0,
        Func<DateTimeOffset>? clock = null,
        StartupMigrationFault? fault = null,
        int busyTimeoutMilliseconds = 2000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.BackupId) ||
            string.IsNullOrWhiteSpace(context.CodeVersion))
        {
            throw new ArgumentException(
                "Backup ID and code version are required.",
                nameof(context));
        }
        if (busyTimeoutMilliseconds is < 1 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeoutMilliseconds));
        }
        if (minimumCatalogSchemaVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumCatalogSchemaVersion),
                "Minimum catalog schema version cannot be negative.");
        }
        if (maximumCatalogSchemaVersion < minimumCatalogSchemaVersion)
        {
            throw new ArgumentException(
                "Maximum catalog schema version cannot be less than minimum catalog schema version.",
                nameof(maximumCatalogSchemaVersion));
        }
        _organizerPath = Path.GetFullPath(organizerPath);
        _catalogPath = Path.GetFullPath(catalogPath);
        if (string.Equals(
                _organizerPath,
                _catalogPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Organizer and catalog stores must be distinct.");
        }
        _context = context;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _fault = fault;
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        _minimumCatalogSchemaVersion = minimumCatalogSchemaVersion;
        _maximumCatalogSchemaVersion = maximumCatalogSchemaVersion;
    }

    public async Task<StartupDataHygienePlan> DryRunAsync(
        CancellationToken cancellationToken = default)
    {
        var organizer = await Organizer().DryRunAsync(cancellationToken);
        var catalog = await Catalog().DryRunAsync(cancellationToken);
        return new StartupDataHygienePlan(
            organizer.Concat(catalog).ToArray());
    }

    public async Task<StartupDataHygieneResult> ApplyAsync(
        StartupDataHygienePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var applied = new List<string>();
        applied.AddRange(await Organizer().ApplyAsync(
            plan.Organizer,
            cancellationToken));
        applied.AddRange(await Catalog().ApplyAsync(
            plan.Catalog,
            cancellationToken));
        return new(
            plan,
            applied,
            await HealthAsync(cancellationToken));
    }

    public async Task<StartupDataHygieneResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var plan = await DryRunAsync(cancellationToken);
        return await ApplyAsync(plan, cancellationToken);
    }

    private const int SupportedOrganizerSchemaVersion = 9;
    private const int LegacyOrganizerMinimumVersion = 5;
    private const int LegacyOrganizerMaximumVersion = 7;

    public async Task<StartupDataHygieneResult>
        RunWithOrganizerInitializationAsync(
            Func<CancellationToken, Task> initializeOrganizer,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializeOrganizer);
        var version = await ReadOrganizerVersionAsync(cancellationToken);
        var prepared = new List<string>();
        if (version > SupportedOrganizerSchemaVersion)
        {
            throw new StartupMigrationBlockedException(
                $"Organizer schema {version} is newer than supported schema {SupportedOrganizerSchemaVersion}.");
        }
        if (version is >= LegacyOrganizerMinimumVersion and <= LegacyOrganizerMaximumVersion)
        {
            var legacy = LegacyOrganizer();
            var plan = await legacy.DryRunAsync(cancellationToken);
            prepared.AddRange(await legacy.ApplyAsync(
                plan,
                cancellationToken));
        }
        if (version is null || version < SupportedOrganizerSchemaVersion)
            await initializeOrganizer(cancellationToken);
        var currentVersion =
            await ReadOrganizerVersionAsync(cancellationToken);
        if (currentVersion != SupportedOrganizerSchemaVersion)
        {
            throw new StartupMigrationBlockedException(
                $"Organizer schema initialization did not reach version {SupportedOrganizerSchemaVersion}.");
        }
        var result = await RunAsync(cancellationToken);
        return result with
        {
            AppliedMigrationIds = prepared
                .Concat(result.AppliedMigrationIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    public async Task<int?> ReadOrganizerVersionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_organizerPath))
            return null;
        try
        {
            await using var connection = new SqliteConnection(
                SqliteStartupMigration.ConnectionString(
                    _organizerPath,
                    SqliteOpenMode.ReadOnly));
            connection.DefaultTimeout = Math.Max(
                1,
                (int)Math.Ceiling(_busyTimeoutMilliseconds / 1000d));
            await connection.OpenAsync(cancellationToken);
            await SqliteStartupMigration.ConfigureAsync(
                connection,
                true,
                _busyTimeoutMilliseconds,
                cancellationToken);
            var quickCheck = await SqliteStartupMigration.QuickCheckAsync(
                connection,
                cancellationToken);
            if (!string.Equals(
                    quickCheck,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new StartupMigrationBlockedException(
                    "Organizer quick_check failed.");
            }
            return await SqliteStartupMigration.UserVersionAsync(
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
            throw new StartupMigrationBlockedException(
                "Organizer schema inspection failed safely " +
                $"({exception.GetType().Name}).",
                exception);
        }
    }

    private async Task<IReadOnlyList<StartupStoreHealthReport>> HealthAsync(
        CancellationToken cancellationToken)
    {
        var organizer = await Organizer().HealthAsync(cancellationToken);
        var catalog = await Catalog().HealthAsync(cancellationToken);
        return [organizer, catalog];
    }

    private StartupMigrationStoreRunner Organizer() =>
        new(
            "organizer",
            _organizerPath,
            8,
            SupportedOrganizerSchemaVersion,
            false,
            [
                new RemoveAutomaticNexusRelationsMigration(),
                new ConfirmRedscriptUpdateFamilyMigration()
            ],
            _context,
            _clock,
            _fault,
            _busyTimeoutMilliseconds);

    private StartupMigrationStoreRunner LegacyOrganizer() =>
        new(
            "organizer",
            _organizerPath,
            LegacyOrganizerMinimumVersion,
            LegacyOrganizerMaximumVersion,
            false,
            [new RemoveAutomaticNexusRelationsMigration()],
            _context,
            _clock,
            _fault,
            _busyTimeoutMilliseconds);

    private StartupMigrationStoreRunner Catalog() =>
        new(
            "catalog",
            _catalogPath,
            _minimumCatalogSchemaVersion,
            _maximumCatalogSchemaVersion,
            true,
            [new RemoveAutomaticNexusRequirementsMigration()],
            _context,
            _clock,
            _fault,
            _busyTimeoutMilliseconds);
}
