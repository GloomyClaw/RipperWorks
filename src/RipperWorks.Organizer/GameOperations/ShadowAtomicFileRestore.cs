namespace RipperWorks.Organizer.GameOperations;

internal enum ShadowRestoreFaultPhase
{
    None = 0,
    CopyToRestoreTemporary = 1,
    FinalReplaceCall = 2,
    AfterFinalReplaceBeforeVerification = 3,
    RollbackCall = 4
}

/// <summary>
/// One-file restore primitive: verified object → verified temporary →
/// atomic File.Replace with backup → verified destination. Any post-replace
/// failure atomically restores the backup or reports rollback failure.
/// </summary>
internal sealed class ShadowAtomicFileRestore
{
    private readonly ShadowHarnessPermit _permit;

    public ShadowAtomicFileRestore(ShadowHarnessPermit permit)
    {
        _permit = permit ?? throw new ArgumentNullException(nameof(permit));
    }

    internal ShadowRestoreFaultPhase TestOnlyFault { get; set; }

    public async Task ReplaceAsync(
        string objectPath,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        _permit.EnsureAllowed(objectPath, "restoreObject");
        _permit.EnsureAllowed(destination, "restoreDestination");

        var restoreTemporary = destination + ".rf06restore";
        var backupPath = destination + ".rf06bak";
        _permit.EnsureAllowed(restoreTemporary, "restoreTemporary");
        _permit.EnsureAllowed(backupPath, "restoreBackup");

        var replaced = false;
        var rollbackCompleted = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyToTemporaryAsync(
                    objectPath,
                    restoreTemporary,
                    cancellationToken)
                .ConfigureAwait(false);
            await AssertHashAsync(
                    restoreTemporary,
                    expectedSha256,
                    "RestoreStageHashMismatch",
                    cancellationToken)
                .ConfigureAwait(false);

            ReplaceWithOptionalRealFault(
                restoreTemporary,
                destination,
                backupPath);
            replaced = true;

            if (TestOnlyFault is
                ShadowRestoreFaultPhase.AfterFinalReplaceBeforeVerification
                or ShadowRestoreFaultPhase.RollbackCall)
            {
                throw new IOException("TestOnlyPostReplaceFault");
            }

            await AssertHashAsync(
                    destination,
                    expectedSha256,
                    "RestoreFinalHashMismatch",
                    cancellationToken)
                .ConfigureAwait(false);

            File.Delete(backupPath);
        }
        catch (Exception primary)
        {
            if (replaced && File.Exists(backupPath))
            {
                try
                {
                    RollbackWithOptionalRealFault(backupPath, destination);
                    rollbackCompleted = true;
                }
                catch (Exception rollback)
                {
                    throw new InvalidOperationException(
                        "RestoreRollbackFailed",
                        new AggregateException(primary, rollback));
                }
            }

            throw;
        }
        finally
        {
            DeleteTemporaryIfPresent(restoreTemporary);
            if (rollbackCompleted)
                DeleteTemporaryIfPresent(backupPath);
        }
    }

    private async Task CopyToTemporaryAsync(
        string objectPath,
        string restoreTemporary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? faultLock = null;
        try
        {
            if (TestOnlyFault == ShadowRestoreFaultPhase.CopyToRestoreTemporary)
            {
                faultLock = new FileStream(
                    restoreTemporary,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }

            // The fault seam reaches the real File.Copy call. With the
            // exclusive handle above, Windows reports the copy failure.
            File.Copy(objectPath, restoreTemporary, overwrite: true);
        }
        finally
        {
            if (faultLock is not null)
                await faultLock.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ReplaceWithOptionalRealFault(
        string restoreTemporary,
        string destination,
        string backupPath)
    {
        using var faultLock =
            TestOnlyFault == ShadowRestoreFaultPhase.FinalReplaceCall
                ? new FileStream(
                    destination,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None)
                : null;

        // The optional exclusive destination handle makes this actual
        // File.Replace call fail without replacing the current destination.
        File.Replace(
            restoreTemporary,
            destination,
            backupPath,
            ignoreMetadataErrors: true);
    }

    private void RollbackWithOptionalRealFault(
        string backupPath,
        string destination)
    {
        using var faultLock =
            TestOnlyFault == ShadowRestoreFaultPhase.RollbackCall
                ? new FileStream(
                    destination,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None)
                : null;

        // Atomic rollback consumes the backup only after the replacement.
        File.Replace(
            backupPath,
            destination,
            destinationBackupFileName: null,
            ignoreMetadataErrors: true);
    }

    private static async Task AssertHashAsync(
        string path,
        string expectedSha256,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var actual = await ContentStoreService.ComputeHashAsync(
                path,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                actual,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(errorCode);
        }
    }

    private static void DeleteTemporaryIfPresent(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A rollback-failure backup is recovery evidence and is retained.
        }
        catch (UnauthorizedAccessException)
        {
            // Same policy: retain evidence rather than hiding the main fault.
        }
    }
}
