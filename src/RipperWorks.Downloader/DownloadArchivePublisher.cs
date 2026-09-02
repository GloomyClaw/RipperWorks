using System.Collections.Concurrent;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed class DownloadArchivePublisher : IDownloadArchivePublisher
{
    private readonly IDownloaderRepository _repository;
    private readonly PublicationFileStore _files = new();
    private readonly PublicationPathAllocator _paths = new();
    private readonly PublicationCatalogStore _catalog;
    private readonly PublicationDownloadProjection _downloadProjection;
    private readonly DownloaderTechnicalLog? _technicalLog;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim>
        _entryGates = new();

    public DownloadArchivePublisher(
        IDownloaderRepository repository,
        string catalogDatabasePath,
        DownloaderTechnicalLog? technicalLog = null)
    {
        _repository = repository;
        _catalog = new PublicationCatalogStore(catalogDatabasePath);
        _downloadProjection = new PublicationDownloadProjection(repository);
        _technicalLog = technicalLog;
    }

    internal Func<PublicationFaultPoint, Task>? TestOnlyPhaseHook
    {
        get;
        set;
    }

    public Task<ArchivePublicationResult> PublishAsync(
        DownloaderEntry entry,
        string sourcePath,
        string libraryRoot,
        CancellationToken cancellationToken = default) =>
        PublishCoreAsync(
            entry,
            sourcePath,
            libraryRoot,
            ArchivePublicationOrigin.ManualAttach,
            cancellationToken);

    public Task<ArchivePublicationResult> PublishDownloadAsync(
        DownloaderEntry entry,
        string sourcePath,
        string libraryRoot,
        CancellationToken cancellationToken = default) =>
        PublishCoreAsync(
            entry,
            sourcePath,
            libraryRoot,
            ArchivePublicationOrigin.DownloadJob,
            cancellationToken);

    private async Task<ArchivePublicationResult> PublishCoreAsync(
        DownloaderEntry entry,
        string sourcePath,
        string libraryRoot,
        ArchivePublicationOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var gate = _entryGates.GetOrAdd(
            entry.Id,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var persisted = await _repository.LoadEntryAsync(
                entry.Id,
                cancellationToken).ConfigureAwait(false);
            if (persisted?.Status == DownloaderStatus.Downloaded &&
                await IsSameTerminalPublicationAsync(
                    persisted,
                    sourcePath,
                    libraryRoot,
                    cancellationToken).ConfigureAwait(false))
            {
                var terminalIntent = PublicationIntent.FromPersisted(
                    persisted,
                    libraryRoot,
                    origin);
                await VerifyTerminalAsync(
                    persisted,
                    terminalIntent,
                    cancellationToken).ConfigureAwait(false);
                CopyPublicationState(persisted, entry);
                return new(
                    terminalIntent.ArchivePath,
                    terminalIntent.MetadataPath,
                    terminalIntent.Size);
            }
            var owner = persisted is not null &&
                        PublicationStatusPolicy.IsPending(persisted.Status)
                ? persisted
                : entry;
            PublicationIntent intent;
            if (PublicationStatusPolicy.IsPending(owner.Status))
            {
                if (PublicationPhaseState.Origin(owner.Status) != origin)
                {
                    throw new PublicationConflictException(
                        "PublicationOriginMismatch",
                        "Retry origin contradicts the durable publication phase.");
                }
                intent = PublicationIntent.FromPersisted(
                    owner,
                    libraryRoot);
                await _downloadProjection.ValidateRequiredAsync(
                    owner.Id,
                    intent.Origin,
                    cancellationToken).ConfigureAwait(false);
                await VerifyRetrySourceIfPresentAsync(
                    sourcePath,
                    intent,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                intent = await PersistIntentAsync(
                    owner,
                    sourcePath,
                    libraryRoot,
                    origin,
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var result = await ContinueAsync(
                    owner,
                    intent,
                    cancellationToken).ConfigureAwait(false);
                CopyPublicationState(owner, entry);
                return result;
            }
            catch (Exception exception)
            {
                owner.Error = ErrorCode(exception);
                await _repository.SaveEntryAsync(
                    owner,
                    CancellationToken.None).ConfigureAwait(false);
                CopyPublicationState(owner, entry);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> ReconcilePendingAsync(
            string libraryRoot,
            CancellationToken cancellationToken = default)
    {
        var entries = (await _repository.LoadEntriesAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(entry => PublicationStatusPolicy.IsPending(entry.Status))
            .ToArray();
        var completed = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gate = _entryGates.GetOrAdd(
                entry.Id,
                static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    var intent = PublicationIntent.FromPersisted(
                        entry,
                        libraryRoot);
                    await _downloadProjection.ValidateRequiredAsync(
                        entry.Id,
                        intent.Origin,
                        cancellationToken).ConfigureAwait(false);
                    await ContinueAsync(
                        entry,
                        intent,
                        cancellationToken).ConfigureAwait(false);
                    completed++;
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await RecordReconciliationFailureAsync(
                        entry,
                        exception).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        return completed;
    }

    private async Task<PublicationIntent> PersistIntentAsync(
        DownloaderEntry entry,
        string sourcePath,
        string libraryRoot,
        ArchivePublicationOrigin origin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _downloadProjection.ValidateRequiredAsync(
            entry.Id,
            origin,
            cancellationToken).ConfigureAwait(false);
        var source = Path.GetFullPath(sourcePath);
        _technicalLog?.Invoke("HashStarted", entry, null);
        var inspection = await _files.InspectSourceAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        _technicalLog?.Invoke("HashCompleted", entry, null);
        var root = PublicationPathSafety.NormalizeRoot(libraryRoot);
        var archivePath = await _paths.SelectIntendedArchivePathAsync(
            entry,
            source,
            root,
            inspection.Sha256,
            cancellationToken).ConfigureAwait(false);
        entry.Sha256 = inspection.Sha256;
        entry.Size = inspection.Size;
        entry.TemporaryPath = source;
        entry.LocalArchivePath = archivePath;
        entry.ArchiveFileName = Path.GetFileName(archivePath);
        entry.Status = PublicationPhaseState.For(
            PublicationPhase.Intent,
            origin);
        entry.Error = string.Empty;
        var intent = PublicationIntent.FromPersisted(
            entry,
            root);
        entry.CatalogId = intent.PublicationId;
        await _repository.SaveEntryAsync(
            entry,
            cancellationToken).ConfigureAwait(false);
        await InvokeHookAsync(PublicationFaultPoint.AfterIntentPersisted)
            .ConfigureAwait(false);
        return intent;
    }

    private async Task<ArchivePublicationResult> ContinueAsync(
        DownloaderEntry entry,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        if (PublicationPhaseState.Is(entry.Status, PublicationPhase.Intent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _files.EnsureArchiveAsync(
                intent,
                () => InvokeHookAsync(
                    PublicationFaultPoint.AfterArchiveReplayTempVerified),
                CancellationToken.None).ConfigureAwait(false);
            entry.Status = PublicationPhaseState.For(
                PublicationPhase.Archive,
                intent.Origin);
            entry.Error = string.Empty;
            await _repository.SaveEntryAsync(
                entry,
                CancellationToken.None).ConfigureAwait(false);
            await InvokeHookAsync(PublicationFaultPoint.AfterArchiveCommitted)
                .ConfigureAwait(false);
        }
        if (PublicationPhaseState.Is(entry.Status, PublicationPhase.Archive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _files.EnsureMetadataAsync(
                entry,
                intent,
                () => InvokeHookAsync(
                    PublicationFaultPoint.AfterMetadataReplayTempVerified),
                CancellationToken.None).ConfigureAwait(false);
            entry.Status = PublicationPhaseState.For(
                PublicationPhase.Metadata,
                intent.Origin);
            entry.Error = string.Empty;
            await _repository.SaveEntryAsync(
                entry,
                CancellationToken.None).ConfigureAwait(false);
            await InvokeHookAsync(PublicationFaultPoint.AfterMetadataCommitted)
                .ConfigureAwait(false);
        }
        if (PublicationPhaseState.Is(entry.Status, PublicationPhase.Metadata))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.CatalogId = await _catalog.EnsureRegisteredAsync(
                entry,
                intent,
                CancellationToken.None).ConfigureAwait(false);
            entry.Status = PublicationPhaseState.For(
                PublicationPhase.Catalog,
                intent.Origin);
            entry.Error = string.Empty;
            await _repository.SaveEntryAsync(
                entry,
                CancellationToken.None).ConfigureAwait(false);
            await InvokeHookAsync(PublicationFaultPoint.AfterCatalogCommitted)
                .ConfigureAwait(false);
        }
        if (PublicationPhaseState.Is(entry.Status, PublicationPhase.Catalog))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InvokeHookAsync(
                    PublicationFaultPoint.BeforeDownloaderTerminalCommit)
                .ConfigureAwait(false);
            await _downloadProjection.CommitTerminalAsync(entry, intent)
                .ConfigureAwait(false);
            entry.Status = PublicationPhaseState.For(
                PublicationPhase.DownloaderTerminal,
                intent.Origin);
            entry.Error = string.Empty;
            await _repository.SaveEntryAsync(
                entry,
                CancellationToken.None).ConfigureAwait(false);
            await InvokeHookAsync(
                    PublicationFaultPoint.AfterDownloaderTerminalCommitted)
                .ConfigureAwait(false);
        }
        if (PublicationPhaseState.Is(
                entry.Status,
                PublicationPhase.DownloaderTerminal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _downloadProjection.CommitTerminalAsync(entry, intent)
                .ConfigureAwait(false);
            await InvokeHookAsync(PublicationFaultPoint.BeforeSourceCleanup)
                .ConfigureAwait(false);
            await _downloadProjection.CleanupSourceAsync(
                entry,
                intent,
                () => InvokeHookAsync(
                    PublicationFaultPoint.AfterSourceCleanupVerified))
                .ConfigureAwait(false);
            await VerifyTerminalAsync(
                entry,
                intent,
                CancellationToken.None).ConfigureAwait(false);
            entry.Status = DownloaderStatus.Downloaded;
            entry.Progress = 100;
            entry.BytesPerSecond = 0;
            entry.Error = string.Empty;
            ApplyPublishedUpdateState(entry);
            await _repository.SaveEntryAsync(
                entry,
                CancellationToken.None).ConfigureAwait(false);
        }
        if (entry.Status != DownloaderStatus.Downloaded)
        {
            throw new InvalidOperationException(
                $"Unsupported publication phase: {entry.Status}.");
        }
        await VerifyTerminalAsync(
            entry,
            intent,
            CancellationToken.None).ConfigureAwait(false);
        _technicalLog?.Invoke("PublishedToLibrary", entry, null);
        return new(intent.ArchivePath, intent.MetadataPath, intent.Size);
    }

    private async Task VerifyTerminalAsync(
        DownloaderEntry entry,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        await PublicationFileStore.VerifyFileAsync(
            intent.ArchivePath,
            intent.Size,
            intent.Sha256,
            "FinalArchiveMismatch",
            cancellationToken).ConfigureAwait(false);
        await _files.VerifyMetadataAsync(
            entry,
            intent,
            cancellationToken).ConfigureAwait(false);
        await _catalog.VerifyRegisteredAsync(
            entry,
            intent,
            cancellationToken).ConfigureAwait(false);
        await _downloadProjection.VerifyTerminalAsync(
            entry,
            intent,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyRetrySourceIfPresentAsync(
        string sourcePath,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
            return;
        var inspection = await _files.InspectSourceAsync(
            fullPath,
            cancellationToken).ConfigureAwait(false);
        if (inspection.Size != intent.Size ||
            !string.Equals(
                inspection.Sha256,
                intent.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PublicationConflictException(
                "PublicationRetryArtifactMismatch",
                "Retry source differs from the durable publication intent.");
        }
    }

    private async Task<bool> IsSameTerminalPublicationAsync(
        DownloaderEntry entry,
        string sourcePath,
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.Sha256) ||
            string.IsNullOrWhiteSpace(entry.LocalArchivePath) ||
            entry.Size <= 0)
        {
            return false;
        }
        _ = PublicationPathSafety.RequireContainedPath(
            libraryRoot,
            entry.LocalArchivePath);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            return true;
        var inspection = await _files.InspectSourceAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        return inspection.Size == entry.Size &&
               string.Equals(
                   inspection.Sha256,
                   entry.Sha256,
                   StringComparison.OrdinalIgnoreCase);
    }

    private Task InvokeHookAsync(PublicationFaultPoint point) =>
        TestOnlyPhaseHook?.Invoke(point) ?? Task.CompletedTask;

    private async Task RecordReconciliationFailureAsync(
        DownloaderEntry entry,
        Exception exception)
    {
        entry.Error = ErrorCode(exception);
        try
        {
            await _repository.SaveEntryAsync(
                entry,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception persistenceException)
        {
            _technicalLog?.Invoke(
                "PublicationReconciliationErrorPersistenceFailed",
                entry,
                persistenceException);
        }
        _technicalLog?.Invoke(
            "PublicationReconciliationBlocked",
            entry,
            exception);
    }

    private static string ErrorCode(Exception exception) =>
        exception is PublicationConflictException conflict
            ? conflict.Code
            : exception is ArgumentException or InvalidDataException or
                NotSupportedException or PathTooLongException
                ? "PublicationIntentInvalid"
                : exception.GetType().Name;

    private static void CopyPublicationState(
        DownloaderEntry source,
        DownloaderEntry destination)
    {
        destination.Sha256 = source.Sha256;
        destination.Size = source.Size;
        destination.TemporaryPath = source.TemporaryPath;
        destination.LocalArchivePath = source.LocalArchivePath;
        destination.ArchiveFileName = source.ArchiveFileName;
        destination.CatalogId = source.CatalogId;
        destination.Status = source.Status;
        destination.Progress = source.Progress;
        destination.BytesPerSecond = source.BytesPerSecond;
        destination.Error = source.Error;
        destination.UpdateCheckStatus = source.UpdateCheckStatus;
        destination.AvailableVersion = source.AvailableVersion;
        destination.AvailableFileId = source.AvailableFileId;
        destination.AvailableFileUuid = source.AvailableFileUuid;
        destination.LastUpdateCheckUtc = source.LastUpdateCheckUtc;
        destination.UpdateCheckMessage = source.UpdateCheckMessage;
    }

    private static void ApplyPublishedUpdateState(DownloaderEntry entry)
    {
        if (entry.Source != DownloaderSource.Nexus)
            return;
        if (entry.AvailableFileId == entry.NexusFileId &&
            !string.IsNullOrWhiteSpace(entry.AvailableFileUuid))
        {
            entry.NexusFileUuid = entry.AvailableFileUuid;
        }
        entry.UpdateCheckStatus = NexusUpdateCheckStatus.UpToDate;
        entry.AvailableVersion = string.Empty;
        entry.AvailableFileId = null;
        entry.AvailableFileUuid = string.Empty;
        entry.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        entry.UpdateCheckMessage =
            "Опубликованная версия отмечена как актуальная.";
    }

}

public static class ArchiveSha256
{
    public static async Task<string> ComputeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        using var algorithm = System.Security.Cryptography.SHA256.Create();
        var hash = await algorithm.ComputeHashAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
