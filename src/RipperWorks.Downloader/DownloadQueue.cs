using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed class DownloadQueue : IDownloadQueue
{
    private readonly IDownloaderRepository _repository;
    private readonly IDownloadTransport _transport;
    private readonly IDownloadArchivePublisher _publisher;
    private readonly Func<string> _temporaryRoot;
    private readonly Func<string> _libraryRoot;
    private readonly SemaphoreSlim _slots;
    private readonly DownloaderTechnicalLog? _technicalLog;
    private readonly ConcurrentDictionary<Guid, DownloadQueueJob> _jobs = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;

    public DownloadQueue(
        IDownloaderRepository repository,
        IDownloadTransport transport,
        IDownloadArchivePublisher publisher,
        Func<string> temporaryRoot,
        Func<string> libraryRoot,
        int concurrentDownloads,
        DownloaderTechnicalLog? technicalLog = null)
    {
        _repository = repository;
        _transport = transport;
        _publisher = publisher;
        _temporaryRoot = temporaryRoot;
        _libraryRoot = libraryRoot;
        _technicalLog = technicalLog;
        _slots = new SemaphoreSlim(
            Math.Clamp(concurrentDownloads, 1, 8));
    }

    public event EventHandler<DownloaderEntry>? EntryChanged;

    public async Task RestoreAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var entries = (await _repository.LoadEntriesAsync(cancellationToken))
            .ToDictionary(entry => entry.Id);
        foreach (var record in await _repository.LoadDownloadsAsync(
                     cancellationToken))
        {
            if (!entries.TryGetValue(record.EntryId, out var entry) ||
                PublicationStatusPolicy.IsPending(entry.Status) ||
                record.Status is DownloaderStatus.Downloaded or
                    DownloaderStatus.Canceled ||
                !Uri.TryCreate(
                    record.SourceUrl,
                    UriKind.Absolute,
                    out var uri))
            {
                continue;
            }
            await StartOrReplaceAsync(
                entry,
                uri,
                record,
                record.Status is DownloaderStatus.Waiting or
                    DownloaderStatus.Downloading,
                persistRequest: false,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EnqueueAsync(
        DownloaderEntry entry,
        Uri downloadUri,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Directory.CreateDirectory(_temporaryRoot());
        var extension = Path.GetExtension(
            Uri.UnescapeDataString(downloadUri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".zip";
        var partPath = Path.Combine(
            Path.GetFullPath(_temporaryRoot()),
            $"{entry.Id:N}{extension}.part");
        var record = new DownloaderDownloadRecord
        {
            EntryId = entry.Id,
            SourceUrl = downloadUri.ToString(),
            TemporaryPath = partPath,
            BytesDownloaded = File.Exists(partPath)
                ? new FileInfo(partPath).Length
                : 0,
            Status = DownloaderStatus.Waiting
        };
        await StartOrReplaceAsync(
            entry,
            downloadUri,
            record,
            start: true,
            persistRequest: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        var job = RequireJobForOperation(entryId);
        await job.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var run = RequireRun(job);
            await CancelAndObserveAsync(run).ConfigureAwait(false);
            await _repository.EnsureNoPendingPublicationAsync(entryId, cancellationToken).ConfigureAwait(false);
            await SetStatusAsync(
                run,
                DownloaderStatus.Paused,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            job.Gate.Release();
        }
    }

    public async Task ResumeAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        var job = RequireJobForOperation(entryId);
        await job.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var run = RequireRun(job);
            if (!run.Task.IsCompleted)
                return;
            await ObserveAndDisposeAsync(run).ConfigureAwait(false);
            await _repository.EnsureNoPendingPublicationAsync(entryId, cancellationToken).ConfigureAwait(false);
            await SetStatusAsync(
                run,
                DownloaderStatus.Waiting,
                cancellationToken).ConfigureAwait(false);
            StartLocked(job, run.Entry, run.Uri, run.Record);
        }
        finally
        {
            job.Gate.Release();
        }
    }

    public async Task CancelAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        var job = RequireJobForOperation(entryId);
        await job.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var run = RequireRun(job);
            await CancelAndObserveAsync(run).ConfigureAwait(false);
            await _repository.EnsureNoPendingPublicationAsync(entryId, cancellationToken).ConfigureAwait(false);
            await SetStatusAsync(
                run,
                DownloaderStatus.Canceled,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            job.Gate.Release();
        }
    }

    private async Task StartOrReplaceAsync(
        DownloaderEntry entry,
        Uri uri,
        DownloaderDownloadRecord record,
        bool start,
        bool persistRequest,
        CancellationToken cancellationToken)
    {
        var job = GetOrCreateJob(entry.Id);
        await job.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (job.Current is { } previous)
                await CancelAndObserveAsync(previous).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _repository.EnsureNoPendingPublicationAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            if (persistRequest)
            {
                entry.TemporaryPath = record.TemporaryPath;
                entry.Status = DownloaderStatus.Waiting;
                entry.Error = string.Empty;
                await _repository.SaveEntryAsync(
                    entry,
                    cancellationToken).ConfigureAwait(false);
                await _repository.SaveDownloadAsync(
                    record,
                    cancellationToken).ConfigureAwait(false);
                _technicalLog?.Invoke("DownloadEnqueued", entry, null);
            }
            if (start)
                StartLocked(job, entry, uri, record);
            else
                job.Current = new(entry, uri, record);
        }
        finally
        {
            job.Gate.Release();
        }
    }

    private void StartLocked(
        DownloadQueueJob job,
        DownloaderEntry entry,
        Uri uri,
        DownloaderDownloadRecord record)
    {
        var run = new DownloadQueueWorkerRun(entry, uri, record);
        job.Current = run;
        run.Task = Task.Run(
            () => RunOwnedAsync(run),
            CancellationToken.None);
    }

    private async Task RunOwnedAsync(DownloadQueueWorkerRun run)
    {
        try
        {
            await ExecuteAsync(run, run.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryTechnicalLog(
                "DownloadWorkerFaultObserved",
                run.Entry,
                exception);
            await PersistUnexpectedFaultAsync(run, exception)
                .ConfigureAwait(false);
        }
    }

    private async Task PersistUnexpectedFaultAsync(
        DownloadQueueWorkerRun run,
        Exception exception)
    {
        if (PublicationStatusPolicy.IsPending(run.Entry.Status))
            return;
        run.Entry.Status = DownloaderStatus.Error;
        run.Entry.Error = exception.Message;
        run.Record = run.Record with
        {
            Status = DownloaderStatus.Error,
            Error = exception.Message
        };
        try
        {
            await PersistAsync(run, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception persistenceException)
        {
            TryTechnicalLog(
                "DownloadWorkerFaultPersistenceFailed",
                run.Entry,
                persistenceException);
        }
    }

    private async Task ExecuteAsync(
        DownloadQueueWorkerRun job,
        CancellationToken token)
    {
        var slot = false;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false);
            slot = true;
            _technicalLog?.Invoke("DownloadStarted", job.Entry, null);
            var partPath = job.Record.TemporaryPath;
            Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
            var offset = File.Exists(partPath)
                ? new FileInfo(partPath).Length
                : 0;
            await using var response = await _transport.OpenReadAsync(
                job.Uri,
                offset,
                token).ConfigureAwait(false);
            if (!NexusMetadataMerge.IsMeaningful(
                    job.Entry.ArchiveFileName) &&
                NexusMetadataMerge.IsMeaningful(response.FileName))
            {
                job.Entry.ArchiveFileName =
                    response.FileName!.Trim();
            }
            _technicalLog?.Invoke("ResponseHeadersReceived", job.Entry, null);
            if (offset > 0 && !response.RangeAccepted)
            {
                File.Delete(partPath);
                offset = 0;
            }
            job.Entry.Status = DownloaderStatus.Downloading;
            job.Record = job.Record with
            {
                Status = DownloaderStatus.Downloading,
                BytesDownloaded = offset,
                TotalBytes = response.TotalBytes
            };
            await PersistAsync(job, token).ConfigureAwait(false);

            await using var output = new FileStream(
                partPath,
                offset > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);
            _technicalLog?.Invoke("TemporaryFileOpened", job.Entry, null);
            var buffer = new byte[128 * 1024];
            var downloaded = offset;
            var timer = Stopwatch.StartNew();
            var initial = offset;
            var lastProgressNotification = Stopwatch.GetTimestamp();
            var lastTechnicalLog = Stopwatch.GetTimestamp();
            while (true)
            {
                var count = await response.Content.ReadAsync(
                    buffer,
                    token).ConfigureAwait(false);
                if (count == 0)
                    break;
                await output.WriteAsync(
                    buffer.AsMemory(0, count),
                    token).ConfigureAwait(false);
                downloaded += count;
                var speed = timer.Elapsed.TotalSeconds > 0
                    ? (downloaded - initial) / timer.Elapsed.TotalSeconds
                    : 0;
                job.Entry.Progress = response.TotalBytes > 0
                    ? (double)downloaded /
                      response.TotalBytes.Value * 100
                    : 0;
                job.Entry.BytesPerSecond = speed;
                job.Record = job.Record with
                {
                    BytesDownloaded = downloaded,
                    TotalBytes = response.TotalBytes
                };
                if (Stopwatch.GetElapsedTime(lastTechnicalLog) >= TimeSpan.FromSeconds(1))
                {
                    lastTechnicalLog = Stopwatch.GetTimestamp();
                    _technicalLog?.Invoke("DownloadProgress", job.Entry, null);
                }
                if (Stopwatch.GetElapsedTime(lastProgressNotification) >= TimeSpan.FromMilliseconds(160))
                {
                    lastProgressNotification = Stopwatch.GetTimestamp();
                    EntryChanged?.Invoke(this, job.Entry);
                }
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Close();
            EntryChanged?.Invoke(this, job.Entry);
            _technicalLog?.Invoke("DownloadCompleted", job.Entry, null);

            var completedExtension = Path.GetExtension(
                job.Entry.ArchiveFileName);
            if (string.IsNullOrWhiteSpace(completedExtension))
            {
                completedExtension = Path.GetExtension(
                    partPath.EndsWith(
                        ".part",
                        StringComparison.OrdinalIgnoreCase)
                        ? partPath[..^5]
                        : partPath);
            }
            if (string.IsNullOrWhiteSpace(completedExtension))
                completedExtension = ".zip";
            var completedPath = Path.Combine(
                Path.GetDirectoryName(partPath)!,
                $"{job.Entry.Id:N}{completedExtension}");
            File.Move(partPath, completedPath, true);
            var publication = await _publisher.PublishDownloadAsync(
                job.Entry,
                completedPath,
                _libraryRoot(),
                token).ConfigureAwait(false);
            job.Entry.LocalArchivePath = publication.ArchivePath;
            job.Entry.Size = publication.FileSize;
            job.Entry.Progress = 100;
            job.Entry.BytesPerSecond = 0;
            job.Entry.Status = DownloaderStatus.Downloaded;
            if (job.Entry.Source == DownloaderSource.Nexus)
            {
                if (job.Entry.AvailableFileId == job.Entry.NexusFileId &&
                    !string.IsNullOrWhiteSpace(
                        job.Entry.AvailableFileUuid))
                {
                    job.Entry.NexusFileUuid =
                        job.Entry.AvailableFileUuid;
                }
                job.Entry.UpdateCheckStatus =
                    NexusUpdateCheckStatus.UpToDate;
                job.Entry.AvailableVersion = string.Empty;
                job.Entry.AvailableFileId = null;
                job.Entry.AvailableFileUuid = string.Empty;
                job.Entry.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
                job.Entry.UpdateCheckMessage =
                    "Скачанная версия отмечена как актуальная.";
            }
            job.Record = job.Record with
            {
                Status = DownloaderStatus.Downloaded,
                BytesDownloaded = publication.FileSize,
                TotalBytes = publication.FileSize,
                Error = string.Empty
            };
            await PersistAsync(job, token).ConfigureAwait(false);
            File.Delete(completedPath);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _technicalLog?.Invoke("DownloadCancelled", job.Entry, null);
        }
        catch (Exception exception)
        {
            _technicalLog?.Invoke("DownloadFailed", job.Entry, exception);
            if (PublicationStatusPolicy.IsPending(job.Entry.Status))
            {
                EntryChanged?.Invoke(this, job.Entry);
            }
            else
            {
                job.Entry.Status = DownloaderStatus.Error;
                job.Entry.Error = exception.Message;
                job.Record = job.Record with
                {
                    Status = DownloaderStatus.Error,
                    Error = exception.Message
                };
                await PersistAsync(
                    job,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (slot)
                _slots.Release();
        }
    }

    private async Task SetStatusAsync(
        DownloadQueueWorkerRun job,
        DownloaderStatus status,
        CancellationToken cancellationToken)
    {
        job.Entry.Status = status;
        job.Entry.BytesPerSecond = 0;
        job.Record = job.Record with { Status = status };
        await PersistAsync(
            job,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistAsync(
        DownloadQueueWorkerRun job,
        CancellationToken token)
    {
        await _repository.SaveEntryAsync(
            job.Entry,
            token).ConfigureAwait(false);
        await _repository.SaveDownloadAsync(
            job.Record,
            token).ConfigureAwait(false);
        EntryChanged?.Invoke(this, job.Entry);
    }

    private DownloadQueueJob GetOrCreateJob(Guid entryId)
    {
        lock (_disposeGate)
        {
            ThrowIfDisposed();
            return _jobs.GetOrAdd(
                entryId,
                static id => new DownloadQueueJob(id));
        }
    }

    private DownloadQueueJob RequireJobForOperation(Guid entryId)
    {
        lock (_disposeGate)
        {
            ThrowIfDisposed();
            return _jobs.TryGetValue(entryId, out var job)
                ? job
                : throw new InvalidOperationException(
                    "Download is not queued.");
        }
    }

    private static DownloadQueueWorkerRun RequireRun(DownloadQueueJob job) =>
        job.Current ?? throw new InvalidOperationException(
            "Download is not queued.");

    private static async Task CancelAndObserveAsync(
        DownloadQueueWorkerRun run)
    {
        if (!run.Task.IsCompleted)
            run.Cancellation.Cancel();
        await ObserveAndDisposeAsync(run).ConfigureAwait(false);
    }

    private static async Task ObserveAndDisposeAsync(
        DownloadQueueWorkerRun run)
    {
        await run.Task.ConfigureAwait(false);
        run.DisposeCancellation();
    }

    private void TryTechnicalLog(
        string stage,
        DownloaderEntry entry,
        Exception? exception = null)
    {
        try
        {
            _technicalLog?.Invoke(stage, entry, exception);
        }
        catch
        {
            // The owned task has already captured the terminal fault.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposeTask = DisposeCoreAsync(_jobs.Values.ToArray());
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(
        IReadOnlyList<DownloadQueueJob> jobs)
    {
        await Task.WhenAll(jobs.Select(StopJobAsync)).ConfigureAwait(false);
        _slots.Dispose();
    }

    private static async Task StopJobAsync(DownloadQueueJob job)
    {
        await job.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (job.Current is { } run)
                await CancelAndObserveAsync(run).ConfigureAwait(false);
        }
        finally
        {
            job.Gate.Release();
        }
    }
}
