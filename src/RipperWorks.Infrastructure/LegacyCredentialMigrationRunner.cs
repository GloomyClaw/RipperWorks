using RipperWorks.Core;

namespace RipperWorks.Infrastructure;

/// <summary>
/// One-shot legacy → current credential migration orchestration.
/// Marker I/O is owned by <see cref="CredentialMigrationStateStore"/>.
/// </summary>
internal sealed class LegacyCredentialMigrationRunner
{
    private readonly string _currentPath;
    private readonly string _legacyPath;
    private readonly CredentialMigrationStateStore _stateStore;

    public LegacyCredentialMigrationRunner(
        string currentPath,
        string legacyPath,
        string migrationStatePath)
    {
        _currentPath = currentPath;
        _legacyPath = legacyPath;
        _stateStore = new CredentialMigrationStateStore(migrationStatePath);
    }

    /// <summary>
    /// Test-only: when set to a <see cref="CredentialMigrationPhases"/> value,
    /// the runner faults at that barrier.
    /// </summary>
    public string? TestOnlyFaultPhase { get; set; }

    /// <summary>
    /// Test-only: when true with <see cref="TestOnlyFaultPhase"/>, throws
    /// <see cref="OperationCanceledException"/> instead of IOException.
    /// </summary>
    public bool TestOnlyFaultIsCancellation { get; set; }

    public Task<MigrationStateRead> ReadStateAsync(
        CancellationToken cancellationToken) =>
        _stateStore.ReadAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await HitBarrierAsync(
                CredentialMigrationPhases.Discovery,
                cancellationToken)
            .ConfigureAwait(false);

        var read = await _stateStore.ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (read.IsBlocked)
        {
            // Corrupt / unknown / unreadable marker: do not mutate blobs.
            return;
        }

        // Terminal markers: no further blob mutation on startup.
        if (read.State is { Status: CredentialMigrationStatuses.Completed })
            return;
        if (read.State is { Status: CredentialMigrationStatuses.Blocked })
            return;

        var currentExists = File.Exists(_currentPath);
        var legacyExists = File.Exists(_legacyPath);
        var currentReadable = currentExists &&
            DpapiCredentialBlobOperations.TryDecrypt(_currentPath, out _);
        var legacyReadable = legacyExists &&
            DpapiCredentialBlobOperations.TryDecrypt(_legacyPath, out _);

        if (!currentExists && !legacyExists)
        {
            await WriteCompletedAsync(null, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (currentReadable && !legacyExists)
        {
            await WriteCompletedAsync(
                    DpapiCredentialBlobOperations.HashFile(_currentPath),
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (currentReadable && legacyExists)
        {
            await DeleteLegacyAfterCurrentVerifiedAsync(cancellationToken)
                .ConfigureAwait(false);
            await WriteCompletedAsync(
                    DpapiCredentialBlobOperations.HashFile(_currentPath),
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!currentExists && legacyReadable)
        {
            await MigrateLegacyToCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (currentExists && !currentReadable && legacyReadable)
        {
            await WriteFailedAsync(
                    DpapiCredentialBlobOperations.HashFile(_currentPath),
                    DpapiCredentialBlobOperations.HashFile(_legacyPath),
                    "CurrentUnreadableLegacyPreserved",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!currentExists && legacyExists && !legacyReadable)
        {
            await WriteFailedAsync(
                    null,
                    DpapiCredentialBlobOperations.HashFile(_legacyPath),
                    "LegacyUnreadable",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (currentExists && !currentReadable &&
            legacyExists && !legacyReadable)
        {
            await WriteFailedAsync(
                    DpapiCredentialBlobOperations.HashFile(_currentPath),
                    DpapiCredentialBlobOperations.HashFile(_legacyPath),
                    "BothUnreadable",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (currentExists && !currentReadable)
        {
            await WriteFailedAsync(
                    DpapiCredentialBlobOperations.HashFile(_currentPath),
                    legacyExists
                        ? DpapiCredentialBlobOperations.HashFile(_legacyPath)
                        : null,
                    "CurrentUnreadable",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public CredentialStatusInfo ToStatus(MigrationStateRead read)
    {
        if (read.IsBlocked)
        {
            return new CredentialStatusInfo(
                CredentialPresenceStatus.LegacyMigrationFailed,
                read.FailureKind);
        }

        if (File.Exists(_currentPath))
        {
            return DpapiCredentialBlobOperations.TryDecrypt(_currentPath, out _)
                ? new CredentialStatusInfo(CredentialPresenceStatus.Present)
                : new CredentialStatusInfo(
                    CredentialPresenceStatus.Unreadable,
                    read.State?.FailureKind ?? read.FailureKind);
        }

        if (read.State is { Status: CredentialMigrationStatuses.Failed } or
            { Status: CredentialMigrationStatuses.Blocked })
        {
            return new CredentialStatusInfo(
                CredentialPresenceStatus.LegacyMigrationFailed,
                read.State.FailureKind ?? read.FailureKind);
        }

        if (File.Exists(_legacyPath) &&
            read.State?.Status != CredentialMigrationStatuses.Completed)
        {
            return new CredentialStatusInfo(
                CredentialPresenceStatus.LegacyMigrationRequired);
        }

        return new CredentialStatusInfo(CredentialPresenceStatus.Missing);
    }

    private async Task MigrateLegacyToCurrentAsync(
        CancellationToken cancellationToken)
    {
        await HitBarrierAsync(
                CredentialMigrationPhases.BeforeDecrypt,
                cancellationToken)
            .ConfigureAwait(false);

        if (!DpapiCredentialBlobOperations.TryDecrypt(
                _legacyPath,
                out var secret) ||
            string.IsNullOrWhiteSpace(secret))
        {
            await WriteFailedAsync(
                    null,
                    DpapiCredentialBlobOperations.HashFile(_legacyPath),
                    "LegacyUnreadable",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await DpapiCredentialBlobOperations.WriteAtomicAsync(
                    _currentPath,
                    secret,
                    cancellationToken,
                    HitBarrierAsync)
                .ConfigureAwait(false);

            if (!DpapiCredentialBlobOperations.TryDecrypt(
                    _currentPath,
                    out var verified) ||
                verified != secret)
            {
                await WriteFailedAsync(
                        DpapiCredentialBlobOperations.HashFile(_currentPath),
                        DpapiCredentialBlobOperations.HashFile(_legacyPath),
                        "CurrentVerificationFailed",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await HitBarrierAsync(
                    CredentialMigrationPhases.AfterCurrentVerification,
                    cancellationToken)
                .ConfigureAwait(false);

            await DeleteLegacyAfterCurrentVerifiedAsync(cancellationToken)
                .ConfigureAwait(false);

            await WriteCompletedAsync(
                    DpapiCredentialBlobOperations.HashFile(_currentPath),
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Never continue decrypt/delete after cancellation.
            throw;
        }
        finally
        {
            secret = string.Empty;
        }
    }

    /// <summary>
    /// Explicit delete tombstone: Status=Completed, empty hashes, FailureKind=Deleted.
    /// Startup treats Completed as terminal; GetStatus with no blobs is Missing.
    /// </summary>
    public Task WriteDeletedTombstoneAsync(CancellationToken cancellationToken) =>
        _stateStore.WriteAsync(
            CredentialMigrationStatuses.Completed,
            CredentialMigrationPhases.AfterMarker,
            currentHash: null,
            legacyHash: null,
            failureKind: "Deleted",
            cancellationToken,
            HitBarrierAsync);

    /// <summary>
    /// After current credential is verified, cancel-safe legacy removal.
    /// Cancellation already requested before this call must not delete legacy.
    /// </summary>
    private async Task DeleteLegacyAfterCurrentVerifiedAsync(
        CancellationToken cancellationToken)
    {
        // Explicit CT check immediately before any legacy delete.
        cancellationToken.ThrowIfCancellationRequested();
        await HitBarrierAsync(
                CredentialMigrationPhases.BeforeLegacyDelete,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        DpapiCredentialBlobOperations.DeleteIfExists(_legacyPath);
        DpapiCredentialBlobOperations.DeleteIfExists(
            DpapiCredentialBlobOperations.GetTemporaryPath(_legacyPath));
        await HitBarrierAsync(
                CredentialMigrationPhases.AfterLegacyDelete,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task WriteCompletedAsync(
        string? currentHash,
        string? legacyHash,
        CancellationToken cancellationToken) =>
        _stateStore.WriteAsync(
            CredentialMigrationStatuses.Completed,
            CredentialMigrationPhases.AfterMarker,
            currentHash,
            legacyHash,
            null,
            cancellationToken,
            HitBarrierAsync);

    private Task WriteFailedAsync(
        string? currentHash,
        string? legacyHash,
        string failureKind,
        CancellationToken cancellationToken) =>
        _stateStore.WriteAsync(
            CredentialMigrationStatuses.Failed,
            CredentialMigrationPhases.AfterMarker,
            currentHash,
            legacyHash,
            failureKind,
            cancellationToken,
            HitBarrierAsync);

    private Task HitBarrierAsync(
        string phase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(TestOnlyFaultPhase, phase, StringComparison.Ordinal))
            return Task.CompletedTask;

        if (TestOnlyFaultIsCancellation)
        {
            throw new OperationCanceledException(
                $"Test-only cancellation at phase '{phase}'.",
                cancellationToken);
        }

        throw new IOException(
            $"Test-only credential migration fault at phase '{phase}'.");
    }
}
