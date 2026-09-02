namespace RipperWorks.Infrastructure;

public static class StartupMigrationIds
{
    public const string RemoveAutomaticNexusRelations =
        "organizer.001.remove-automatic-nexus-relations";

    public const string ConfirmRedscriptUpdateFamily =
        "organizer.002.confirm-redscript-update-family";

    public const string RemoveAutomaticNexusRequirements =
        "catalog.001.remove-automatic-nexus-requirements";
}

public sealed record StartupMigrationContext(
    string BackupId,
    string CodeVersion)
{
    public static StartupMigrationContext Create(
        string backupId,
        Type codeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        ArgumentNullException.ThrowIfNull(codeType);
        var version = codeType.Assembly
            .GetCustomAttributes(false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Select(attribute => attribute.InformationalVersion)
            .FirstOrDefault()
            ?? codeType.Assembly.GetName().Version?.ToString()
            ?? "unknown";
        return new(backupId, version);
    }
}

public sealed record StartupMigrationDryRun
{
    public required string MigrationId { get; init; }
    public required string Store { get; init; }
    public required int SchemaVersion { get; init; }
    public required bool Pending { get; init; }
    public required string InputFingerprint { get; init; }
    public required int EstimatedInserts { get; init; }
    public required int EstimatedUpdates { get; init; }
    public required int EstimatedDeletes { get; init; }
    public required int AmbiguityCount { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required bool Compatible { get; init; }
    public required string BackupId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string CodeVersion { get; init; }
    public required string ReportHash { get; init; }
}

public sealed record StartupDataHygienePlan(
    IReadOnlyList<StartupMigrationDryRun> Migrations)
{
    public IReadOnlyList<StartupMigrationDryRun> Organizer =>
        Migrations.Where(report => report.Store == "organizer").ToArray();

    public IReadOnlyList<StartupMigrationDryRun> Catalog =>
        Migrations.Where(report => report.Store == "catalog").ToArray();
}

public sealed record StartupStoreHealthReport(
    string Store,
    int SchemaVersion,
    string QuickCheck,
    IReadOnlyList<string> AppliedMigrationIds);

public sealed record StartupDataHygieneResult(
    StartupDataHygienePlan Plan,
    IReadOnlyList<string> AppliedMigrationIds,
    IReadOnlyList<StartupStoreHealthReport> Health);

public enum StartupMigrationBarrier
{
    BeforeDryRun,
    AfterDryRunBeforeTransaction,
    AfterTransactionBegins,
    AfterDataWriteBeforeLedger,
    AfterLedgerWriteBeforeCommit,
    ImmediatelyAfterCommit
}

public delegate void StartupMigrationFault(
    string migrationId,
    StartupMigrationBarrier barrier);

public sealed class StartupMigrationBlockedException : Exception
{
    public StartupMigrationBlockedException(string message)
        : base(message)
    {
    }

    public StartupMigrationBlockedException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
