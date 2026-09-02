namespace RipperWorks.Infrastructure;

/// <summary>
/// Named migration phases used for state validation and test-only fault barriers.
/// </summary>
public static class CredentialMigrationPhases
{
    public const string Discovery = "Discovery";
    public const string BeforeDecrypt = "BeforeDecrypt";
    public const string AfterCurrentTempWrite = "AfterCurrentTempWrite";
    public const string AfterAtomicReplace = "AfterAtomicReplace";
    public const string AfterCurrentVerification = "AfterCurrentVerification";
    public const string BeforeLegacyDelete = "BeforeLegacyDelete";
    public const string AfterLegacyDelete = "AfterLegacyDelete";
    public const string BeforeMarker = "BeforeMarker";
    public const string AfterMarker = "AfterMarker";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Discovery,
            BeforeDecrypt,
            AfterCurrentTempWrite,
            AfterAtomicReplace,
            AfterCurrentVerification,
            BeforeLegacyDelete,
            AfterLegacyDelete,
            BeforeMarker,
            AfterMarker
        };
}

public static class CredentialMigrationStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Blocked = "Blocked";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Pending,
            Completed,
            Failed,
            Blocked
        };
}
