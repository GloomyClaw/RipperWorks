using System.Collections.Concurrent;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed class NexusModUpdateCheckClient(
    INexusUpdateApiClient api,
    IProtectedCredentialStore credentials)
    : INexusModUpdateCheckClient
{
    public async Task<IReadOnlyDictionary<long, NexusModUpdateCheckResult>>
        CheckModAsync(
            string gameDomain,
            long modId,
            IReadOnlyCollection<long> numericFileIds,
            CancellationToken cancellationToken = default)
    {
        try
        {
            return await credentials.UseAsync(
                CredentialIdentity.NexusDefault,
                (apiKey, token) =>
                    CheckModWithKeyAsync(
                        gameDomain,
                        modId,
                        numericFileIds,
                        apiKey,
                        token),
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new NexusAuthenticationException(exception.Message);
        }
    }

    private async Task<IReadOnlyDictionary<long, NexusModUpdateCheckResult>>
        CheckModWithKeyAsync(
            string gameDomain,
            long modId,
            IReadOnlyCollection<long> numericFileIds,
            string apiKey,
            CancellationToken cancellationToken)
    {
        var publishedFileIds = await api.GetModFileIdsAsync(
            gameDomain,
            modId,
            apiKey,
            cancellationToken);
        var results = new Dictionary<long, NexusModUpdateCheckResult>();
        foreach (var numericFileId in numericFileIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!publishedFileIds.Contains(numericFileId))
            {
                results[numericFileId] = CurrentUnavailable();
                continue;
            }

            var current = await api.GetFileVersionByGameScopedIdAsync(
                gameDomain,
                numericFileId,
                apiKey,
                cancellationToken);
            if (current is null)
            {
                results[numericFileId] = ManualReview(
                    "Числовой file_id найден у мода, но Nexus не вернул " +
                    "официальное соответствие UUID файловой цепочки.");
                continue;
            }

            var chain = (await api.GetFileVersionsAsync(
                    current.ModFileUuid,
                    apiKey,
                    cancellationToken))
                .Where(value => string.Equals(
                    value.ModFileUuid,
                    current.ModFileUuid,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.Position)
                .ToArray();
            var currentInChain = chain.FirstOrDefault(value =>
                string.Equals(
                    value.VersionUuid,
                    current.VersionUuid,
                    StringComparison.OrdinalIgnoreCase) ||
                value.NumericFileId == numericFileId);
            if (currentInChain is null)
            {
                results[numericFileId] = CurrentUnavailable();
                continue;
            }

            results[numericFileId] = NexusUpdateClassifier.SelectCandidate(
                currentInChain,
                chain);
        }
        return results;
    }

    private static NexusModUpdateCheckResult CurrentUnavailable() =>
        new(
            NexusUpdateCheckStatus.CurrentFileUnavailable,
            null,
            null,
            false,
            false,
            true,
            "Текущий файл недоступен в официальном списке Nexus.");

    private static NexusModUpdateCheckResult ManualReview(
        string message,
        NexusUpdateFileVersion? current = null) =>
        new(
            NexusUpdateCheckStatus.ManualReviewRequired,
            current,
            null,
            false,
            true,
            false,
            message);
}

public sealed class NexusModUpdateCheckService(
    IDownloaderRepository repository,
    INexusModUpdateCheckClient client)
    : INexusModUpdateCheckService
{
    public async Task<NexusModUpdateCheckResult> CheckEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        var entry = await RunDatabaseAsync(
            () => repository.LoadEntryAsync(
                entryId,
                cancellationToken),
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException(
                "Запись Downloader не найдена.");
        if (entry.Source != DownloaderSource.Nexus ||
            entry.NexusModId is null ||
            string.IsNullOrWhiteSpace(entry.GameDomain))
        {
            throw new InvalidOperationException(
                "Проверка доступна только для записи Nexus с mod_id.");
        }
        if (entry.NexusFileId is null)
        {
            var missing = MissingIdentity();
            await ApplyAndSaveAsync(entry, missing, cancellationToken);
            return missing;
        }

        entry.UpdateCheckStatus = NexusUpdateCheckStatus.Checking;
        NexusModUpdateCheckResult result;
        try
        {
            var values = await client.CheckModAsync(
                entry.GameDomain,
                entry.NexusModId.Value,
                [entry.NexusFileId.Value],
                cancellationToken);
            result = values.TryGetValue(
                    entry.NexusFileId.Value,
                    out var value)
                ? value
                : ManualReviewNoResult();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = ApiError(exception.Message);
        }
        await ApplyAndSaveAsync(entry, result, cancellationToken);
        return result;
    }

    public async Task<NexusModUpdateCheckSummary> CheckAllAsync(
        IProgress<NexusModUpdateCheckProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = (await RunDatabaseAsync(
                () => repository.LoadEntriesAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false))
            .Where(entry =>
                entry.Source == DownloaderSource.Nexus &&
                entry.NexusModId is not null)
            .ToArray();
        var total = entries.Length;
        var completed = 0;
        var updatesFound = 0;
        var authenticationFailed = 0;
        var completedStatuses =
            new ConcurrentBag<NexusUpdateCheckStatus>();

        void ReportCompleted(DownloaderEntry entry)
        {
            completedStatuses.Add(entry.UpdateCheckStatus);
            if (entry.UpdateCheckStatus ==
                NexusUpdateCheckStatus.UpdateAvailable)
            {
                Interlocked.Increment(ref updatesFound);
            }
            var completedNow = Interlocked.Increment(ref completed);
            progress?.Report(new(
                completedNow,
                total,
                entry,
                Volatile.Read(ref updatesFound)));
        }

        void ReportState(DownloaderEntry entry) =>
            progress?.Report(new(
                Volatile.Read(ref completed),
                total,
                entry,
                Volatile.Read(ref updatesFound)));

        foreach (var entry in entries.Where(entry =>
                     entry.NexusFileId is null ||
                     string.IsNullOrWhiteSpace(entry.GameDomain)))
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            await ApplyAndSaveAsync(
                entry,
                MissingIdentity(),
                cancellationToken);
            ReportCompleted(entry);
        }

        var groups = entries
            .Where(entry =>
                entry.NexusFileId is not null &&
                !string.IsNullOrWhiteSpace(entry.GameDomain))
            .GroupBy(
                entry => new ModKey(
                    entry.GameDomain.Trim().ToLowerInvariant(),
                    entry.NexusModId!.Value))
            .ToArray();
        using var networkGate = new SemaphoreSlim(3, 3);

        async Task CheckGroupAsync(
            IGrouping<ModKey, DownloaderEntry> group)
        {
            var groupEntries = group.ToArray();
            var previous = groupEntries.ToDictionary(
                entry => entry.Id,
                entry => entry.UpdateCheckStatus);
            var saved = new HashSet<Guid>();
            var checkingReported = false;
            try
            {
                await networkGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyDictionary<long, NexusModUpdateCheckResult>
                    results;
                try
                {
                    foreach (var entry in groupEntries)
                    {
                        entry.UpdateCheckStatus =
                            NexusUpdateCheckStatus.Checking;
                        ReportState(entry);
                    }
                    checkingReported = true;
                    results = await client.CheckModAsync(
                            group.Key.GameDomain,
                            group.Key.ModId,
                            groupEntries
                                .Select(entry =>
                                    entry.NexusFileId!.Value)
                                .Distinct()
                                .ToArray(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    networkGate.Release();
                }

                foreach (var entry in groupEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = results.TryGetValue(
                            entry.NexusFileId!.Value,
                            out var value)
                        ? value
                        : ManualReviewNoResult();
                    await ApplyAndSaveAsync(
                            entry,
                            result,
                            cancellationToken)
                        .ConfigureAwait(false);
                    saved.Add(entry.Id);
                    ReportCompleted(entry);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                if (checkingReported)
                {
                    foreach (var entry in groupEntries.Where(
                                 entry => !saved.Contains(entry.Id)))
                    {
                        entry.UpdateCheckStatus = previous[entry.Id];
                        ReportState(entry);
                    }
                }
                throw;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (checkingReported)
                    {
                        foreach (var entry in groupEntries.Where(
                                     entry => !saved.Contains(entry.Id)))
                        {
                            entry.UpdateCheckStatus =
                                previous[entry.Id];
                            ReportState(entry);
                        }
                    }
                    throw new OperationCanceledException(
                        cancellationToken);
                }
                if (exception is NexusAuthenticationException)
                {
                    Interlocked.Exchange(
                        ref authenticationFailed,
                        1);
                }
                var message =
                    exception is NexusAuthenticationException
                        ? "Nexus отклонил API-ключ."
                        : exception.Message;
                foreach (var entry in groupEntries.Where(
                             entry => !saved.Contains(entry.Id)))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (checkingReported)
                        {
                            entry.UpdateCheckStatus =
                                previous[entry.Id];
                            ReportState(entry);
                        }
                        throw new OperationCanceledException(
                            cancellationToken);
                    }
                    await ApplyAndSaveAsync(
                            entry,
                            ApiError(message),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    saved.Add(entry.Id);
                    ReportCompleted(entry);
                }
            }
        }

        try
        {
            await Task.WhenAll(groups.Select(CheckGroupAsync))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Completed records stay persisted; active incomplete records
            // restore their previous status in the worker above.
        }

        return new(
            Volatile.Read(ref completed),
            completedStatuses.Count(status =>
                status is NexusUpdateCheckStatus.UpToDate or
                    NexusUpdateCheckStatus.OlderVersion),
            completedStatuses.Count(status =>
                status ==
                NexusUpdateCheckStatus.UpdateAvailable),
            completedStatuses.Count(status =>
                status is
                    NexusUpdateCheckStatus.ManualReviewRequired or
                    NexusUpdateCheckStatus.CurrentFileUnavailable or
                    NexusUpdateCheckStatus.DifferentComponent or
                    NexusUpdateCheckStatus.UnknownComparison),
            completedStatuses.Count(status =>
                status ==
                NexusUpdateCheckStatus.MissingIdentity),
            completedStatuses.Count(status =>
                status == NexusUpdateCheckStatus.ApiError),
            cancellationToken.IsCancellationRequested,
            Volatile.Read(ref authenticationFailed) != 0);
    }

    private async Task ApplyAndSaveAsync(
        DownloaderEntry entry,
        NexusModUpdateCheckResult result,
        CancellationToken cancellationToken)
    {
        entry.UpdateCheckStatus = result.Status;
        if (result.CurrentFile is not null)
            entry.NexusFileUuid = result.CurrentFile.VersionUuid;
        entry.AvailableVersion =
            result.Status == NexusUpdateCheckStatus.UpdateAvailable
                ? result.LatestFileInSameChain?.Version ?? string.Empty
                : string.Empty;
        entry.AvailableFileId =
            result.Status == NexusUpdateCheckStatus.UpdateAvailable
                ? result.LatestFileInSameChain?.NumericFileId
                : null;
        entry.AvailableFileUuid =
            result.Status == NexusUpdateCheckStatus.UpdateAvailable
                ? result.LatestFileInSameChain?.VersionUuid ?? string.Empty
                : string.Empty;
        entry.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        entry.UpdateCheckMessage = result.Message;
        await RunDatabaseAsync(
                () => repository.SaveEntryAsync(
                    entry,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task RunDatabaseAsync(
        Func<Task> operation,
        CancellationToken cancellationToken) =>
        Task.Run(operation, cancellationToken);

    private static Task<T> RunDatabaseAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken) =>
        Task.Run(operation, cancellationToken);

    private static NexusModUpdateCheckResult MissingIdentity() =>
        new(
            NexusUpdateCheckStatus.MissingIdentity,
            null,
            null,
            false,
            false,
            false,
            "Недостаточно данных: файл ещё не выбран.");

    private static NexusModUpdateCheckResult ManualReviewNoResult() =>
        new(
            NexusUpdateCheckStatus.ManualReviewRequired,
            null,
            null,
            false,
            true,
            false,
            "Nexus не вернул однозначный результат для текущего файла.");

    private static NexusModUpdateCheckResult ApiError(string message) =>
        new(
            NexusUpdateCheckStatus.ApiError,
            null,
            null,
            false,
            false,
            false,
            message);

    private sealed record ModKey(string GameDomain, long ModId);
}
