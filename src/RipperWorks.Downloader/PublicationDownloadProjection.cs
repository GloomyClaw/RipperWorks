using RipperWorks.Core;

namespace RipperWorks.Downloader;

internal sealed class PublicationDownloadProjection(
    IDownloaderRepository repository)
{
    internal async Task ValidateRequiredAsync(
        Guid entryId,
        ArchivePublicationOrigin origin,
        CancellationToken cancellationToken)
    {
        if (origin == ArchivePublicationOrigin.DownloadJob)
            _ = await RequireAsync(entryId, cancellationToken)
                .ConfigureAwait(false);
    }

    internal async Task CommitTerminalAsync(
        DownloaderEntry entry,
        PublicationIntent intent)
    {
        if (intent.Origin == ArchivePublicationOrigin.ManualAttach)
            return;
        var record = await RequireAsync(entry.Id, CancellationToken.None)
            .ConfigureAwait(false);
        await repository.SaveDownloadAsync(
            record with
            {
                Status = DownloaderStatus.Downloaded,
                BytesDownloaded = intent.Size,
                TotalBytes = intent.Size,
                Error = string.Empty
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task VerifyTerminalAsync(
        DownloaderEntry entry,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        if (intent.Origin == ArchivePublicationOrigin.ManualAttach)
            return;
        var record = await RequireAsync(entry.Id, cancellationToken)
            .ConfigureAwait(false);
        if (record.Status != DownloaderStatus.Downloaded)
        {
            throw new PublicationConflictException(
                "PublicationDownloaderTerminalMissing",
                "Downloader terminal state is not committed.");
        }
    }

    internal async Task CleanupSourceAsync(
        DownloaderEntry entry,
        PublicationIntent intent,
        Func<Task> afterVerification)
    {
        if (intent.Origin == ArchivePublicationOrigin.ManualAttach)
            return;
        if (string.Equals(
                intent.SourcePath,
                intent.ArchivePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var evidence = await RequireAsync(entry.Id, CancellationToken.None)
            .ConfigureAwait(false);
        RequireTerminal(evidence);
        PublicationPathSafety.RevalidateOwnedDownloadSource(
            entry.Id,
            evidence.TemporaryPath,
            intent.SourcePath);
        if (string.IsNullOrWhiteSpace(intent.SourcePath) ||
            !File.Exists(intent.SourcePath))
        {
            return;
        }
        await PublicationFileStore.VerifyFileAsync(
            intent.SourcePath,
            intent.Size,
            intent.Sha256,
            "PublicationCleanupSourceMismatch",
            CancellationToken.None).ConfigureAwait(false);
        await afterVerification().ConfigureAwait(false);

        var current = await RequireAsync(entry.Id, CancellationToken.None)
            .ConfigureAwait(false);
        RequireTerminal(current);
        if (current.DownloadId != evidence.DownloadId ||
            !string.Equals(
                Path.GetFullPath(current.TemporaryPath),
                Path.GetFullPath(evidence.TemporaryPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PublicationConflictException(
                "PublicationSourceOwnershipChanged",
                "Downloader source ownership changed during cleanup.");
        }
        PublicationPathSafety.RevalidateOwnedDownloadSource(
            entry.Id,
            current.TemporaryPath,
            intent.SourcePath);
        File.Delete(intent.SourcePath);
    }

    private async Task<DownloaderDownloadRecord> RequireAsync(
        Guid entryId,
        CancellationToken cancellationToken)
    {
        var record = (await repository.LoadDownloadsAsync(cancellationToken)
                .ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.EntryId == entryId);
        return record ?? throw new PublicationConflictException(
            "PublicationDownloadProjectionMissing",
            "A job-backed publication has no downloader projection.");
    }

    private static void RequireTerminal(DownloaderDownloadRecord record)
    {
        if (record.Status != DownloaderStatus.Downloaded)
        {
            throw new PublicationConflictException(
                "PublicationDownloaderTerminalMissing",
                "Downloader terminal state is not committed.");
        }
    }
}
