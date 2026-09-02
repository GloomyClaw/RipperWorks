using RipperWorks.Core;

namespace RipperWorks.Downloader;

public enum ManualBrowserState
{
    WaitingForFile,
    FileDetected,
    Queued,
    ManualActionRequired,
    Error,
    Completed,
    Stopped
}

public sealed record ManualBrowserItem(
    DownloaderEntry Entry,
    int Position,
    int Total,
    Uri PageUri);

public sealed class ManualBrowserQueue(
    IDownloaderRepository repository,
    INexusEntryDownloadCoordinator downloads)
    : INexusUpdateRequestQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DownloaderEntry> _entries = [];
    private int _index;

    public ManualBrowserItem? Current { get; private set; }
    public ManualBrowserState State { get; private set; } =
        ManualBrowserState.Stopped;
    public string Error { get; private set; } = string.Empty;
    public bool IsActive => Current is not null;

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<DownloaderEntry>>
        SnapshotPendingAsync(
            CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _entries.Skip(_index).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> AppendAsync(
        DownloaderEntry entry,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_entries.Any(value => value.Id == entry.Id))
                return false;
            if (Current is null)
            {
                _entries = [entry];
                _index = 0;
                Error = string.Empty;
                SetCurrent();
            }
            else
            {
                _entries = [.. _entries, entry];
                Current = new(
                    Current.Entry,
                    Current.Position,
                    _entries.Count,
                    Current.PageUri);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(
        IEnumerable<DownloaderEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var uniqueNewEntries = entries
                .GroupBy(entry => entry.Id)
                .Select(group => group.First())
                .ToArray();

            if (Current is null)
            {
                _entries = uniqueNewEntries;
                _index = 0;
                Error = string.Empty;
                SetCurrent();
            }
            else
            {
                var existingIds = _entries.Select(entry => entry.Id).ToHashSet();
                var toAppend = uniqueNewEntries
                    .Where(entry => !existingIds.Contains(entry.Id))
                    .ToArray();

                if (toAppend.Length > 0)
                {
                    _entries = [.. _entries, .. toAppend];
                    if (Current is not null)
                    {
                        Current = new(
                            Current.Entry,
                            Current.Position,
                            _entries.Count,
                            Current.PageUri);
                    }
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task HandleNxmAsync(
        string value,
        CancellationToken cancellationToken = default) =>
        CompleteCurrentAsync(
            entry => downloads.ProcessNxmForEntryAsync(
                entry.Id,
                value,
                cancellationToken),
            cancellationToken);

    public Task HandleDirectAsync(
        Uri downloadUri,
        CancellationToken cancellationToken = default) =>
        CompleteCurrentAsync(
            entry => downloads.QueueDirectForEntryAsync(
                entry.Id,
                downloadUri,
                cancellationToken),
            cancellationToken);

    public async Task SkipAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Current is null)
                return;
            Current.Entry.Status =
                DownloaderStatus.ManualActionRequired;
            await repository.SaveEntryAsync(
                Current.Entry,
                cancellationToken);
            State = ManualBrowserState.ManualActionRequired;
            Changed?.Invoke(this, EventArgs.Empty);
            _index++;
            SetCurrent();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var position = _entries
                .Select((entry, index) => (entry, index))
                .FirstOrDefault(pair => pair.entry.Id == entryId);
            if (position.entry is null || position.index < _index)
                return;
            position.entry.Status = DownloaderStatus.Canceled;
            await repository.SaveEntryAsync(
                position.entry,
                cancellationToken);
            var values = _entries.ToList();
            values.RemoveAt(position.index);
            _entries = values;
            if (position.index == _index)
                SetCurrent();
            else
                Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        _entries = [];
        _index = 0;
        Current = null;
        Error = string.Empty;
        State = ManualBrowserState.Stopped;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task CompleteCurrentAsync(
        Func<DownloaderEntry, Task<DownloaderEntry>> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Current is null)
                return;
            try
            {
                State = ManualBrowserState.FileDetected;
                Error = string.Empty;
                Changed?.Invoke(this, EventArgs.Empty);
                await operation(Current.Entry);
                State = ManualBrowserState.Queued;
                Changed?.Invoke(this, EventArgs.Empty);
                _index++;
                SetCurrent();
            }
            catch (Exception exception)
            {
                State = ManualBrowserState.Error;
                Error = exception.Message;
                Changed?.Invoke(this, EventArgs.Empty);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetCurrent()
    {
        if (_index >= _entries.Count)
        {
            Current = null;
            State = ManualBrowserState.Completed;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        var entry = _entries[_index];
        Current = new(
            entry,
            _index + 1,
            _entries.Count,
            GetPageUri(entry));
        State = ManualBrowserState.WaitingForFile;
        Error = string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Uri GetPageUri(DownloaderEntry entry)
    {
        if (DownloaderLinkParser.TryParseNexusPage(
                entry.Url,
                out _) &&
            Uri.TryCreate(entry.Url, UriKind.Absolute, out var page))
        {
            return DownloaderLinkParser.BuildNexusFilesPageUri(page);
        }
        if (entry.NexusModId is { } modId)
        {
            return DownloaderLinkParser.BuildNexusFilesPageUri(
                new Uri(
                    $"https://www.nexusmods.com/{entry.GameDomain}/mods/{modId}"));
        }
        throw new InvalidOperationException(
            "A Nexus page URL is required for manual file selection.");
    }
}
