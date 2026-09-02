using RipperWorks.Core;

namespace RipperWorks.Infrastructure;

/// <summary>
/// Public credential store facade: serializes access to current DPAPI blob
/// operations and the legacy migration runner. Never returns plaintext.
/// </summary>
public sealed class DpapiProtectedCredentialStore : IProtectedCredentialStore, IDisposable
{
    public const string MigrationId = "credentials.001.legacy-nexus-to-current";
    public const string MigrationStateFileName = "credential-migration.json";

    private readonly string _currentPath;
    private readonly string _legacyPath;
    private readonly string _migrationStatePath;
    private readonly LegacyCredentialMigrationRunner _migration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DpapiProtectedCredentialStore(
        string currentPath,
        string legacyPath,
        string migrationStatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationStatePath);
        _currentPath = Path.GetFullPath(currentPath);
        _legacyPath = Path.GetFullPath(legacyPath);
        _migrationStatePath = Path.GetFullPath(migrationStatePath);
        _migration = new LegacyCredentialMigrationRunner(
            _currentPath,
            _legacyPath,
            _migrationStatePath);
    }

    /// <summary>
    /// Test-only fault barrier phase name
    /// (<see cref="CredentialMigrationPhases"/>).
    /// </summary>
    public string? TestOnlyFaultPhase
    {
        get => _migration.TestOnlyFaultPhase;
        set => _migration.TestOnlyFaultPhase = value;
    }

    /// <summary>
    /// Test-only: fault barrier throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public bool TestOnlyFaultIsCancellation
    {
        get => _migration.TestOnlyFaultIsCancellation;
        set => _migration.TestOnlyFaultIsCancellation = value;
    }

    public static DpapiProtectedCredentialStore CreateProductionLayout(
        RipperWorksPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var defaultDataRoot = Path.GetFullPath(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "RipperWorks"));
        var isDefaultProductionRoot = string.Equals(
            Path.GetFullPath(paths.DataRoot),
            defaultDataRoot,
            StringComparison.OrdinalIgnoreCase);
        // Safe-copy / RIPPERWORKS_DATA_ROOT overrides must never probe the
        // real ModDownloader legacy path under the user profile.
        var legacy = isDefaultProductionRoot
            ? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "ModDownloader",
                "nexus.key")
            : Path.Combine(
                paths.DataRoot,
                "legacy-moddownloader",
                "nexus.key");
        return new DpapiProtectedCredentialStore(
            paths.NexusKeyPath,
            legacy,
            Path.Combine(paths.DataRoot, MigrationStateFileName));
    }

    public static DpapiProtectedCredentialStore CreateExplicit(
        string currentPath,
        string legacyPath,
        string migrationStatePath) =>
        new(currentPath, legacyPath, migrationStatePath);

    public async Task RunStartupMigrationAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _migration.RunAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CredentialStatusInfo> GetStatusAsync(
        CredentialIdentity identity,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported(identity);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await _migration.ReadStateAsync(cancellationToken)
                .ConfigureAwait(false);
            return _migration.ToStatus(read);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        CredentialIdentity identity,
        string secret,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported(identity);
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Nexus API key is empty.", nameof(secret));

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DpapiCredentialBlobOperations.WriteAtomicAsync(
                    _currentPath,
                    secret.Trim(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task UseAsync(
        CredentialIdentity identity,
        Func<string, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        UseAsync(
            identity,
            async (secret, token) =>
            {
                await operation(secret, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public async Task<T> UseAsync<T>(
        CredentialIdentity identity,
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnsureSupported(identity);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string secret;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_currentPath) ||
                !DpapiCredentialBlobOperations.TryDecrypt(
                    _currentPath,
                    out secret) ||
                string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException(
                    "Save and validate the Nexus API key in Settings first.");
            }
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await operation(secret, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            secret = string.Empty;
        }
    }

    /// <summary>
    /// Removes current/legacy blobs and temps, then writes a Completed tombstone
    /// (FailureKind=Deleted) so the next startup stays Missing.
    /// Fail-closed: corrupt or unknown/newer MigrationId markers abort with
    /// no partial blob deletion.
    /// Known Failed/Blocked (same MigrationId) are converted to the tombstone.
    /// </summary>
    public async Task DeleteAsync(
        CredentialIdentity identity,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported(identity);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await _migration.ReadStateAsync(cancellationToken)
                .ConfigureAwait(false);
            // Fail-closed: unsupported marker must not be overwritten after a
            // partial blob wipe.
            if (read.IsBlocked)
            {
                throw new InvalidOperationException(
                    "Credential delete refused: migration marker is corrupt " +
                    "or uses an unknown/newer MigrationId " +
                    $"({read.FailureKind}). Blobs were not modified.");
            }

            DpapiCredentialBlobOperations.DeleteIfExists(_currentPath);
            DpapiCredentialBlobOperations.DeleteIfExists(
                DpapiCredentialBlobOperations.GetTemporaryPath(_currentPath));
            DpapiCredentialBlobOperations.DeleteIfExists(_legacyPath);
            DpapiCredentialBlobOperations.DeleteIfExists(
                DpapiCredentialBlobOperations.GetTemporaryPath(_legacyPath));

            // Canonical deleted/completed tombstone for known markers
            // (including Failed/Blocked) and for missing markers.
            await _migration.WriteDeletedTombstoneAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gate.Dispose();
    }

    private static void EnsureSupported(CredentialIdentity identity)
    {
        if (!identity.EqualsCanonical(CredentialIdentity.NexusDefault))
        {
            throw new NotSupportedException(
                "Only the default NexusMods credential slot is supported.");
        }
    }
}
