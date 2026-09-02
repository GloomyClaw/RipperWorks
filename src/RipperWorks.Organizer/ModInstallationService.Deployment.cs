using RipperWorks.Core;
using SharpCompress.Archives;

namespace RipperWorks.Organizer;

public sealed partial class ModInstallationService
{
    /// <summary>
    /// Verifies plan content identity on an exclusive stream first, then creates
    /// staging and extracts from that same stream (no path re-open).
    /// </summary>
    private async Task<IReadOnlyList<StagedFile>> ExtractToStagingAsync(
        OrganizerPackageRecord mod,
        InstallPlan plan,
        string operationStaging,
        CancellationToken cancellationToken)
    {
        var budget = ArchiveResourceBudgetScope.ForExtraction(
            _archiveResourcePolicy,
            _archiveTimeProvider);
        void CheckStagingBoundary()
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.CheckDeadline();
        }
        try
        {
            CheckStagingBoundary();
        var expectedSha = ArchiveTrustPolicy.CanonicalizeSha256(
            plan.ExpectedArchiveSha256)
            ?? throw new InvalidDataException(
                "Install plan lacks ExpectedArchiveSha256 content identity.");

        // Identity check before any staging directory mutation.
        await using var lease = await VerifiedArchiveLease.AcquireAsync(
                mod.Package.ArchivePath,
                expectedSha,
                cancellationToken,
                budget.CheckDeadline)
            .ConfigureAwait(false);
        CheckStagingBoundary();

        Directory.CreateDirectory(_stagingRoot);
        ArchivePathSafety.EnsureNoReparsePoints(_stagingRoot, _stagingRoot);
        ArchivePathSafety.CreateDirectoriesWithoutReparse(
            _stagingRoot, operationStaging);
        var required = mod.Analysis!.Entries
            .Where(entry =>
                entry.IsInstallable &&
                !entry.IsDirectory &&
                !string.IsNullOrWhiteSpace(entry.RelativeInstallPath))
            .ToDictionary(
                entry => entry.NormalizedPath,
                StringComparer.OrdinalIgnoreCase);
        var staged = new List<StagedFile>(required.Count);

        lease.Stream.Position = 0;
        IArchive archive;
        using (OperationPerformanceDiagnostics.MeasureMetric(
                   "ARCHIVE_OPEN",
                   "archive_open"))
        {
            archive = ArchiveOpen.OpenStream(lease.Stream);
        }
        using (archive)
        {
        OperationPerformanceDiagnostics.AddCounter("archive_open_count");
        CheckStagingBoundary();
        OperationPerformanceDiagnostics.AddCounter(
            "archive_entry_enumeration_count");
        using var enumerationTiming =
            OperationPerformanceDiagnostics.MeasureMetric(
                "ARCHIVE_ENUMERATION",
                "archive_entry_enumeration");
        foreach (var entry in archive.Entries)
        {
            CheckStagingBoundary();
            budget.ObserveDeclaredEntry(
                entry.Size,
                entry.CompressedSize,
                entry.IsDirectory);
            if (entry.IsDirectory ||
                !ArchivePathSafety.TryNormalizeArchivePath(
                    entry.Key,
                    out var normalized,
                    out _) ||
                !required.TryGetValue(normalized, out var analyzed))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(entry.LinkTarget) ||
                entry.IsEncrypted ||
                entry.IsSplitAfter ||
                !entry.IsComplete)
            {
                throw new InvalidDataException(
                    $"Archive entry is unsafe: {entry.Key}");
            }

            var relative = analyzed.RelativeInstallPath!;
            var stagingFile = ArchivePathSafety.ResolveSafeGamePath(
                operationStaging,
                relative);
            var parent = Path.GetDirectoryName(stagingFile)!;
            ArchivePathSafety.CreateDirectoriesWithoutReparse(
                operationStaging, parent);
            CheckStagingBoundary();
            using (OperationPerformanceDiagnostics.MeasureMetric(
                       "STAGING",
                       "staging_file_write"))
            {
                await using var source = entry.OpenEntryStream();
                await using var destination = new FileStream(
                    stagingFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);
                CheckStagingBoundary();
                await ArchiveResourceStreamCopier.CopyEntryAsync(
                    source,
                    destination,
                    entry.Size,
                    budget,
                    cancellationToken,
                    TestOnlyAfterArchiveCopyChunk);
                CheckStagingBoundary();
                await destination.FlushAsync(cancellationToken);
                CheckStagingBoundary();
            }
            OperationPerformanceDiagnostics.AddCounter("staging_file_count");
            OperationPerformanceDiagnostics.AddCounter(
                "staging_bytes_written",
                entry.Size);
            var stagedHash = await ComputeStagedHashAsync(
                    stagingFile,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);
            staged.Add(new StagedFile(
                relative,
                stagingFile,
                stagedHash));
            CheckStagingBoundary();
        }

        CheckStagingBoundary();
        if (staged.Count != required.Count)
            throw new InvalidDataException(
                "Staging does not contain every analyzed archive entry.");
        ReconcileStaging(plan, staged, CheckStagingBoundary);
        var result = staged
            .OrderBy(file => file.RelativeGamePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CheckStagingBoundary();
        return result;
        }
        }
        catch (ArchiveResourceBudgetException)
        {
            throw;
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.CheckDeadline();
            throw;
        }
    }

    private static void ReconcileStaging(
        InstallPlan plan,
        IReadOnlyList<StagedFile> staged) =>
        ReconcileStaging(plan, staged, static () => { });

    private static void ReconcileStaging(
        InstallPlan plan,
        IReadOnlyList<StagedFile> staged,
        Action checkStagingBoundary)
    {
        checkStagingBoundary();
        var plannedPaths = plan.Entries
            .Select(entry => entry.RelativeGamePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        checkStagingBoundary();
        var stagedPaths = staged
            .Select(file => file.RelativeGamePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        checkStagingBoundary();
        if (plannedPaths.Length != stagedPaths.Length)
        {
            throw new InvalidDataException(
                "Staged files do not match the fresh installation plan.");
        }
        for (var index = 0; index < plannedPaths.Length; index++)
        {
            checkStagingBoundary();
            if (!string.Equals(
                    plannedPaths[index],
                    stagedPaths[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Staged files do not match the fresh installation plan.");
            }
        }
        checkStagingBoundary();
    }

    private async Task<string> ComputeStagedHashAsync(
        string stagingFile,
        ArchiveResourceBudgetScope budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.CheckDeadline();
        await using var stream = new FileStream(
            stagingFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ArchiveContentHasher.BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        budget.CheckDeadline();
        var hash = await ArchiveContentHasher.ComputeStreamSha256Async(
                stream,
                cancellationToken,
                () =>
                {
                    TestOnlyAfterStagingHashChunk?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    budget.CheckDeadline();
                })
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        budget.CheckDeadline();
        return hash;
    }

    private async Task<IReadOnlyList<InstalledFileRecord>>
        BuildManifestAndPreserveOriginalsAsync(
            OrganizerPackageRecord mod,
            GameProfileRecord profile,
            IReadOnlyList<StagedFile> staged,
            IProgress<ModInstallationProgress>? progress,
            CancellationToken cancellationToken)
    {
        var result = new List<InstalledFileRecord>(staged.Count);
        for (var index = 0; index < staged.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stagedFile = staged[index];
            var destination = ArchivePathSafety.ResolveSafeGamePath(
                profile.GameRoot,
                stagedFile.RelativeGamePath);
            ArchivePathSafety.EnsureNoReparsePoints(
                profile.GameRoot,
                Path.GetDirectoryName(destination)!,
                allowMissing: true);
            var existed = File.Exists(destination);
            string? previousHash = null;
            if (existed)
            {
                previousHash = (await _contentStore.PutFileAsync(
                    destination,
                    cancellationToken)).Hash;
            }
            result.Add(new InstalledFileRecord
            {
                PackageId = mod.Package.PackageId,
                RelativeGamePath = stagedFile.RelativeGamePath,
                InstalledContentHash = stagedFile.Hash,
                PreviousContentHash = previousHash,
                PreviousFileExisted = existed,
                Sequence = index + 1
            });
            progress?.Report(new ModInstallationProgress(
                ModInstallationProgressPhase.PreservingOriginals,
                index + 1,
                staged.Count));
        }
        return result;
    }

    private static async Task DeployFileAsync(
        Guid operationId,
        string gameRoot,
        StagedFile staged,
        InstalledFileRecord manifest,
        CancellationToken cancellationToken)
    {
        var destination = ArchivePathSafety.ResolveSafeGamePath(
            gameRoot,
            staged.RelativeGamePath);
        var parent = Path.GetDirectoryName(destination)!;
        ArchivePathSafety.CreateDirectoriesWithoutReparse(gameRoot, parent);

        if (manifest.PreviousFileExisted)
        {
            if (!File.Exists(destination) ||
                !string.Equals(
                    await ComputeInstallLiveFileHashAsync(
                        destination,
                        cancellationToken),
                    manifest.PreviousContentHash,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Destination changed after planning: {staged.RelativeGamePath}");
            }
        }
        else if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException(
                $"Destination appeared after planning: {staged.RelativeGamePath}");
        }

        var temporary = destination +
            $".ripperworks-{operationId:N}.tmp";
        try
        {
            await CopyNewFileAsync(
                staged.StagingPath,
                temporary,
                cancellationToken);
            var temporaryHash =
                await ContentStoreService.ComputeHashAsync(
                    temporary,
                    cancellationToken);
            if (!string.Equals(
                    staged.Hash,
                    temporaryHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Deployment copy failed verification: {staged.RelativeGamePath}");
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private async Task<IReadOnlyList<string>> RollbackAsync(
        Guid operationId,
        string gameRoot,
        CancellationToken rollbackToken)
    {
        var errors = new List<string>();
        await _repository.UpdateInstallOperationAsync(
            operationId,
            InstallOperationPhase.RollingBack,
            cancellationToken: rollbackToken);
        var files = await _repository.LoadInstallOperationFilesAsync(
            operationId,
            rollbackToken);
        foreach (var file in files)
        {
            try
            {
                var destination = ArchivePathSafety.ResolveSafeGamePath(
                    gameRoot,
                    file.RelativeGamePath);
                var shouldRestore = file.Applied;
                if (!shouldRestore && File.Exists(destination))
                {
                    shouldRestore = string.Equals(
                        await ContentStoreService.ComputeHashAsync(
                            destination,
                            rollbackToken),
                        file.NewContentHash,
                        StringComparison.Ordinal);
                }
                if (!shouldRestore)
                    continue;

                if (file.PreviousFileExisted)
                {
                    var temporary = destination +
                        $".ripperworks-rollback-{operationId:N}.tmp";
                    try
                    {
                        await _contentStore.CopyVerifiedAsync(
                            file.PreviousContentHash ??
                            throw new InvalidDataException(
                                "Previous content hash is missing."),
                            temporary,
                            rollbackToken);
                        File.Move(temporary, destination, true);
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                }
                else
                {
                    if (File.Exists(destination))
                        File.Delete(destination);
                    DeleteEmptyParents(
                        Path.GetDirectoryName(destination)!,
                        gameRoot);
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{file.RelativeGamePath}: {exception}");
            }
        }
        return errors;
    }

    private static async Task CopyNewFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<string> ComputeInstallLiveFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        OperationPerformanceDiagnostics.AddCounter("live_file_hash_count");
        if (OperationPerformanceDiagnostics.IsEnabled)
        {
            try
            {
                OperationPerformanceDiagnostics.AddCounter(
                    "live_file_bytes_hashed",
                    new FileInfo(path).Length);
            }
            catch
            {
                // The authoritative hash operation owns any real I/O failure.
            }
        }
        using var timing = OperationPerformanceDiagnostics.MeasureMetric(
            "LIVE_FILE_HASH",
            "live_file_hash");
        return await ContentStoreService.ComputeHashAsync(
            path,
            cancellationToken);
    }

    private sealed record StagedFile(
        string RelativeGamePath,
        string StagingPath,
        string Hash);
}
