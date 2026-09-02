using RipperWorks.Core;

namespace RipperWorks.Infrastructure;

/// <summary>
/// Process-lifetime owner of the current immutable settings snapshot.
/// Save is serialized. Failed saves never mutate the published snapshot.
/// Successful saves publish exactly once. Does not depend on WPF Dispatcher;
/// subscribers must marshal to their own UI context when required.
/// </summary>
public sealed class SettingsSession : ISettingsSnapshotProvider, IDisposable
{
    private readonly IRipperWorksSettingsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RipperWorksSettings _current;
    private bool _disposed;

    private SettingsSession(
        IRipperWorksSettingsStore store,
        RipperWorksSettings current)
    {
        _store = store;
        _current = current;
    }

    public event EventHandler<RipperWorksSettings>? SnapshotChanged;

    public RipperWorksSettings Current =>
        Volatile.Read(ref _current!);

    public static async Task<SettingsSession> CreateAsync(
        IRipperWorksSettingsStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var snapshot = await store.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        return new SettingsSession(store, snapshot);
    }

    public async Task SaveAsync(
        RipperWorksSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Persist first. On failure the published snapshot is unchanged
            // and SnapshotChanged is not raised.
            await _store.SaveAsync(settings, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _current!, settings);
            // Exactly one notification per successful save.
            SnapshotChanged?.Invoke(this, settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gate.Dispose();
    }
}
