using RipperWorks.Core;

namespace RipperWorks.Downloader;

public interface INexusUpdateRequestQueue
{
    Task<IReadOnlyList<DownloaderEntry>> SnapshotPendingAsync(
        CancellationToken cancellationToken = default);

    Task<bool> AppendAsync(
        DownloaderEntry entry,
        CancellationToken cancellationToken = default);
}

public enum NexusUpdateBatchOutcomeKind
{
    Accepted,
    SkippedNotUpdateable,
    AlreadyQueued,
    AlreadyCurrent,
    Failed
}

public sealed record NexusUpdateBatchOutcome(
    Guid EntryId,
    string EntryName,
    NexusUpdateBatchOutcomeKind Kind,
    string Error = "");

public sealed record NexusUpdateBatchResult(
    IReadOnlyList<NexusUpdateBatchOutcome> Outcomes,
    string BrowserError = "")
{
    public int Accepted => Count(NexusUpdateBatchOutcomeKind.Accepted);
    public int Skipped => Count(
        NexusUpdateBatchOutcomeKind.SkippedNotUpdateable);
    public int AlreadyHandled =>
        Count(NexusUpdateBatchOutcomeKind.AlreadyQueued) +
        Count(NexusUpdateBatchOutcomeKind.AlreadyCurrent);
    public int Failed => Count(NexusUpdateBatchOutcomeKind.Failed);
    public bool HasFailure => Failed > 0 ||
        !string.IsNullOrWhiteSpace(BrowserError);

    private int Count(NexusUpdateBatchOutcomeKind kind) =>
        Outcomes.Count(outcome => outcome.Kind == kind);
}

public sealed class NexusUpdateBatchWorkflow(
    INexusUpdateRequestQueue queue,
    Func<CancellationToken, Task> openBrowser)
{
    public async Task<NexusUpdateBatchResult> ExecuteAsync(
        IEnumerable<DownloaderEntry> selectedEntries,
        CancellationToken cancellationToken = default)
    {
        var selected = selectedEntries.ToArray();
        return await ExecuteAsync(
                selected,
                selected,
                cancellationToken);
    }

    public async Task<NexusUpdateBatchResult> ExecuteAsync(
        IEnumerable<DownloaderEntry> selectedEntries,
        IEnumerable<DownloaderEntry> existingEntries,
        CancellationToken cancellationToken = default)
    {
        var selected = selectedEntries
            .GroupBy(entry => entry.Id)
            .Select(group => group.First())
            .ToArray();
        var pending = await queue.SnapshotPendingAsync(cancellationToken);
        var queuedCandidates = pending
            .Select(GetCandidateKey)
            .Where(key => key is not null)
            .Select(key => key!.Value)
            .ToHashSet();
        var currentCandidates = new HashSet<NexusUpdateCandidateKey>();
        foreach (var existing in existingEntries.Where(
                     IsCurrentOrActiveJob))
        {
            if (NexusUpdateClassifier.TryGetCurrentFileKey(
                    existing,
                    out var currentKey))
            {
                if (existing.Status == DownloaderStatus.Downloaded)
                    currentCandidates.Add(currentKey);
                else
                    queuedCandidates.Add(currentKey);
            }
        }
        var outcomes = new List<NexusUpdateBatchOutcome>(selected.Length);

        foreach (var entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NexusUpdateClassifier.IsUpdateAvailable(entry))
            {
                outcomes.Add(Outcome(
                    entry,
                    NexusUpdateBatchOutcomeKind.SkippedNotUpdateable));
                continue;
            }
            if (entry.NexusFileId == entry.AvailableFileId)
            {
                outcomes.Add(Outcome(
                    entry,
                    entry.Status == DownloaderStatus.Downloaded
                        ? NexusUpdateBatchOutcomeKind.AlreadyCurrent
                        : NexusUpdateBatchOutcomeKind.AlreadyQueued));
                continue;
            }
            if (!NexusUpdateClassifier.TryGetCandidateKey(
                    entry,
                    out var candidateKey))
            {
                outcomes.Add(Outcome(
                    entry,
                    NexusUpdateBatchOutcomeKind.SkippedNotUpdateable));
                continue;
            }
            if (currentCandidates.Contains(candidateKey))
            {
                outcomes.Add(Outcome(
                    entry,
                    NexusUpdateBatchOutcomeKind.AlreadyCurrent));
                continue;
            }
            if (!queuedCandidates.Add(candidateKey))
            {
                outcomes.Add(Outcome(
                    entry,
                    NexusUpdateBatchOutcomeKind.AlreadyQueued));
                continue;
            }

            try
            {
                var appended = await queue.AppendAsync(
                        entry,
                        cancellationToken);
                outcomes.Add(Outcome(
                    entry,
                    appended
                        ? NexusUpdateBatchOutcomeKind.Accepted
                        : NexusUpdateBatchOutcomeKind.AlreadyQueued));
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                queuedCandidates.Remove(candidateKey);
                outcomes.Add(Outcome(
                    entry,
                    NexusUpdateBatchOutcomeKind.Failed,
                    exception.Message));
            }
        }

        var browserError = string.Empty;
        if (outcomes.Any(outcome =>
                outcome.Kind == NexusUpdateBatchOutcomeKind.Accepted))
        {
            try
            {
                await openBrowser(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                browserError = exception.Message;
            }
        }

        return new(outcomes, browserError);
    }

    private static NexusUpdateCandidateKey? GetCandidateKey(
        DownloaderEntry entry) =>
        NexusUpdateClassifier.TryGetCandidateKey(entry, out var key)
            ? key
            : null;

    private static bool IsCurrentOrActiveJob(DownloaderEntry entry) =>
        entry.Status is DownloaderStatus.Waiting or
            DownloaderStatus.FetchingNexus or
            DownloaderStatus.Downloading or
            DownloaderStatus.Paused or
            DownloaderStatus.Downloaded or
            DownloaderStatus.PublicationIntentPersisted or
            DownloaderStatus.PublicationArchiveCommitted or
            DownloaderStatus.PublicationMetadataCommitted or
            DownloaderStatus.PublicationCatalogCommitted or
            DownloaderStatus.PublicationDownloaderTerminalCommitted or
            DownloaderStatus.ManualPublicationIntentPersisted or
            DownloaderStatus.ManualPublicationArchiveCommitted or
            DownloaderStatus.ManualPublicationMetadataCommitted or
            DownloaderStatus.ManualPublicationCatalogCommitted or
            DownloaderStatus.ManualPublicationDownloaderTerminalCommitted;

    private static NexusUpdateBatchOutcome Outcome(
        DownloaderEntry entry,
        NexusUpdateBatchOutcomeKind kind,
        string error = "") =>
        new(entry.Id, entry.Name, kind, error);
}
