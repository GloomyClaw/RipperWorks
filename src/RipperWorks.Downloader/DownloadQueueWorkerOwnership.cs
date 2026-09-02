using RipperWorks.Core;

namespace RipperWorks.Downloader;

internal sealed class DownloadQueueJob(Guid entryId)
{
    public Guid EntryId { get; } = entryId;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public DownloadQueueWorkerRun? Current { get; set; }
}

internal sealed class DownloadQueueWorkerRun(
    DownloaderEntry entry,
    Uri uri,
    DownloaderDownloadRecord record)
{
    private int _cancellationDisposed;

    public DownloaderEntry Entry { get; } = entry;
    public Uri Uri { get; } = uri;
    public DownloaderDownloadRecord Record { get; set; } = record;
    public CancellationTokenSource Cancellation { get; } = new();
    public Task Task { get; set; } = Task.CompletedTask;

    public void DisposeCancellation()
    {
        if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
            Cancellation.Dispose();
    }
}

internal static class DownloadQueuePublicationInterlock
{
    public static async Task EnsureNoPendingPublicationAsync(
        this IDownloaderRepository repository,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        var persisted = await repository.LoadEntryAsync(
            entryId,
            cancellationToken).ConfigureAwait(false);
        if (persisted is not null &&
            PublicationStatusPolicy.IsPending(persisted.Status))
        {
            throw new InvalidOperationException(
                $"Download entry '{entryId}' has pending durable publication " +
                $"state '{persisted.Status}'. Reconciliation must resolve it " +
                "before an ordinary queue transition.");
        }
    }
}
