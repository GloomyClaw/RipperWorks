using RipperWorks.Core;

namespace RipperWorks.Downloader;

public enum DownloaderArchiveReconciliationOutcome
{
    Unchanged,
    Found,
    Missing,
    Corrupted
}

public sealed record DownloaderArchiveReconciliationProgress(
    int Completed,
    int Total,
    DownloaderEntry? UpdatedEntry,
    DownloaderArchiveReconciliationOutcome Outcome);

public sealed record DownloaderArchiveReconciliationResult(
    int Checked,
    int Found,
    int Missing,
    int Corrupted,
    int Unchanged);

public interface IDownloaderArchiveStatusReconciler
{
    Task<DownloaderArchiveReconciliationResult> ReconcileAsync(
        IProgress<DownloaderArchiveReconciliationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class DownloaderArchiveStatusReconciler(
    IDownloaderRepository repository,
    ILibraryArchiveLookup libraryLookup)
    : IDownloaderArchiveStatusReconciler
{
    public async Task<DownloaderArchiveReconciliationResult> ReconcileAsync(
        IProgress<DownloaderArchiveReconciliationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = await repository.LoadEntriesAsync(cancellationToken);
        var found = 0;
        var missing = 0;
        var corrupted = 0;
        var unchanged = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var outcome = DownloaderArchiveReconciliationOutcome.Unchanged;
            DownloaderEntry? updated = null;
            if (ShouldReconcile(entry.Status))
            {
                var before = Snapshot(entry);
                await ReconcileEntryAsync(entry, cancellationToken);
                if (!before.Equals(Snapshot(entry)))
                {
                    await repository.SaveEntryAsync(
                        entry,
                        cancellationToken);
                    updated = entry;
                    outcome = entry.Status switch
                    {
                        DownloaderStatus.Downloaded =>
                            DownloaderArchiveReconciliationOutcome.Found,
                        DownloaderStatus.FileMissing =>
                            DownloaderArchiveReconciliationOutcome.Missing,
                        DownloaderStatus.FileCorrupted =>
                            DownloaderArchiveReconciliationOutcome.Corrupted,
                        _ => DownloaderArchiveReconciliationOutcome.Unchanged
                    };
                }
            }
            switch (outcome)
            {
                case DownloaderArchiveReconciliationOutcome.Found:
                    found++;
                    break;
                case DownloaderArchiveReconciliationOutcome.Missing:
                    missing++;
                    break;
                case DownloaderArchiveReconciliationOutcome.Corrupted:
                    corrupted++;
                    break;
                default:
                    unchanged++;
                    break;
            }
            progress?.Report(new(
                index + 1,
                entries.Count,
                updated,
                outcome));
        }
        return new(
            entries.Count,
            found,
            missing,
            corrupted,
            unchanged);
    }

    private async Task ReconcileEntryAsync(
        DownloaderEntry entry,
        CancellationToken cancellationToken)
    {
        var archivePath = await ResolveArchivePathAsync(
            entry,
            cancellationToken);
        if (archivePath is null)
        {
            entry.Status = DownloaderStatus.FileMissing;
            entry.Progress = 0;
            entry.BytesPerSecond = 0;
            entry.Size = 0;
            entry.Sha256 = string.Empty;
            entry.Error = string.Empty;
            return;
        }

        var file = new FileInfo(archivePath);
        var actualHash = await ArchiveSha256.ComputeAsync(
            archivePath,
            cancellationToken);
        var expectedHash = entry.Sha256;
        var corrupted =
            entry.Status == DownloaderStatus.FileCorrupted ||
            !string.IsNullOrWhiteSpace(expectedHash) &&
            !string.Equals(
                expectedHash,
                actualHash,
                StringComparison.OrdinalIgnoreCase);
        entry.LocalArchivePath = archivePath;
        entry.Size = file.Length;
        entry.Sha256 = actualHash;
        entry.BytesPerSecond = 0;
        entry.Error = string.Empty;
        entry.Progress = corrupted ? 0 : 100;
        entry.Status = corrupted
            ? DownloaderStatus.FileCorrupted
            : DownloaderStatus.Downloaded;
    }

    private async Task<string?> ResolveArchivePathAsync(
        DownloaderEntry entry,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entry.LocalArchivePath))
        {
            var savedPath = Path.GetFullPath(entry.LocalArchivePath);
            if (File.Exists(savedPath))
                return savedPath;
        }
        if (entry.NexusModId is not { } modId ||
            entry.NexusFileId is not { } fileId)
        {
            return null;
        }
        var libraryPath = await libraryLookup.FindArchivePathAsync(
            entry.GameDomain,
            modId,
            fileId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(libraryPath))
            return null;
        var fullPath = Path.GetFullPath(libraryPath);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static bool ShouldReconcile(DownloaderStatus status) =>
        status is DownloaderStatus.Downloaded or
            DownloaderStatus.FileMissing or
            DownloaderStatus.FileCorrupted;

    private static EntrySnapshot Snapshot(DownloaderEntry entry) =>
        new(
            entry.Status,
            entry.Progress,
            entry.BytesPerSecond,
            entry.Size,
            entry.LocalArchivePath,
            entry.Sha256,
            entry.Error);

    private sealed record EntrySnapshot(
        DownloaderStatus Status,
        double Progress,
        double BytesPerSecond,
        long Size,
        string LocalArchivePath,
        string Sha256,
        string Error);
}
