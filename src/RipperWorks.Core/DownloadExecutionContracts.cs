namespace RipperWorks.Core;

public sealed record ArchivePublicationResult(
    string ArchivePath,
    string MetadataPath,
    long FileSize);

public static class PublicationStatusPolicy
{
    public static readonly IReadOnlyList<DownloaderStatus> UserFacingStatuses =
    [
        DownloaderStatus.Added,
        DownloaderStatus.Waiting,
        DownloaderStatus.FetchingNexus,
        DownloaderStatus.Ready,
        DownloaderStatus.Downloading,
        DownloaderStatus.Paused,
        DownloaderStatus.Downloaded,
        DownloaderStatus.ManualActionRequired,
        DownloaderStatus.Error,
        DownloaderStatus.Canceled,
        DownloaderStatus.FileMissing,
        DownloaderStatus.FileCorrupted
    ];

    public static bool IsUserFacing(DownloaderStatus status) =>
        !IsPending(status);

    public static bool IsPending(DownloaderStatus status) =>
        status is DownloaderStatus.PublicationIntentPersisted or
            DownloaderStatus.PublicationArchiveCommitted or
            DownloaderStatus.PublicationMetadataCommitted or
            DownloaderStatus.PublicationCatalogCommitted or
            DownloaderStatus.PublicationDownloaderTerminalCommitted or
            DownloaderStatus.ManualPublicationIntentPersisted or
            DownloaderStatus.ManualPublicationArchiveCommitted or
            DownloaderStatus.ManualPublicationMetadataCommitted or
            DownloaderStatus.ManualPublicationCatalogCommitted or
            DownloaderStatus.ManualPublicationDownloaderTerminalCommitted;
}

public interface IDownloadArchivePublisher
{
    Task<ArchivePublicationResult> PublishAsync(
        DownloaderEntry entry,
        string sourcePath,
        string libraryRoot,
        CancellationToken cancellationToken = default);

    Task<ArchivePublicationResult> PublishDownloadAsync(
        DownloaderEntry entry,
        string sourcePath,
        string libraryRoot,
        CancellationToken cancellationToken = default) =>
        PublishAsync(entry, sourcePath, libraryRoot, cancellationToken);
}

public interface IDownloadQueue : IAsyncDisposable
{
    event EventHandler<DownloaderEntry>? EntryChanged;
    Task RestoreAsync(CancellationToken cancellationToken = default);
    Task EnqueueAsync(
        DownloaderEntry entry,
        Uri downloadUri,
        CancellationToken cancellationToken = default);
    Task PauseAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
    Task ResumeAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
    Task CancelAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
}
