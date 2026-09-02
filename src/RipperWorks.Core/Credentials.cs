namespace RipperWorks.Core;

/// <summary>
/// Stable credential slot identity. Canonical Nexus slot is
/// (<see cref="CredentialProviders.NexusMods"/>, <see cref="CredentialAccounts.Default"/>).
/// Public construction always goes through <see cref="Create"/> so normalization
/// cannot be bypassed.
/// </summary>
public readonly record struct CredentialIdentity
{
    public string Provider { get; }
    public string Account { get; }

    private CredentialIdentity(string provider, string account)
    {
        Provider = provider;
        Account = account;
    }

    public static CredentialIdentity NexusDefault { get; } =
        new(CredentialProviders.NexusMods, CredentialAccounts.Default);

    public static CredentialIdentity Create(string provider, string account)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(account))
            throw new ArgumentException("Account is required.", nameof(account));
        return new(
            NormalizeToken(provider),
            NormalizeToken(account));
    }

    public bool EqualsCanonical(CredentialIdentity other) =>
        string.Equals(Provider, other.Provider, StringComparison.Ordinal) &&
        string.Equals(Account, other.Account, StringComparison.Ordinal);

    private static string NormalizeToken(string value) =>
        value.Trim().ToLowerInvariant();
}

public static class CredentialProviders
{
    public const string NexusMods = "nexusmods";
}

public static class CredentialAccounts
{
    public const string Default = "default";
}

public enum CredentialPresenceStatus
{
    Missing = 0,
    Present = 1,
    Unreadable = 2,
    LegacyMigrationRequired = 3,
    LegacyMigrationFailed = 4
}

public sealed record CredentialStatusInfo(
    CredentialPresenceStatus Status,
    string? Detail = null);

/// <summary>
/// Request-scoped credential access. Normal runtime never returns plaintext.
/// </summary>
public interface IProtectedCredentialStore
{
    Task RunStartupMigrationAsync(
        CancellationToken cancellationToken = default);

    Task<CredentialStatusInfo> GetStatusAsync(
        CredentialIdentity identity,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        CredentialIdentity identity,
        string secret,
        CancellationToken cancellationToken = default);

    Task UseAsync(
        CredentialIdentity identity,
        Func<string, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<T> UseAsync<T>(
        CredentialIdentity identity,
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        CredentialIdentity identity,
        CancellationToken cancellationToken = default);
}

public interface ISettingsSnapshotProvider
{
    RipperWorksSettings Current { get; }
    event EventHandler<RipperWorksSettings>? SnapshotChanged;
}

public sealed record SettingsSaveResult(
    bool SettingsSaved,
    bool CredentialSaved,
    bool CredentialAttempted,
    string? ErrorMessage = null)
{
    public bool Succeeded =>
        SettingsSaved &&
        (!CredentialAttempted || CredentialSaved) &&
        string.IsNullOrWhiteSpace(ErrorMessage);
}
