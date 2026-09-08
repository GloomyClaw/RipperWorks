using System.Net.Http;
using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Organizer;

namespace RipperWorks.App.Services;

public sealed class NexusRequirementModuleHost :
    INexusRequirementModuleBoundary
{
    private readonly string _catalogDatabasePath;
    private readonly OrganizerRepository _organizer;
    private readonly IDownloaderRepository _downloader;
    private readonly Func<IDownloadsModuleBoundary?>? _downloads;
    private HttpClient? _httpClient;
    private NexusRequirementSyncService? _service;
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public NexusRequirementModuleHost(
        string catalogDatabasePath,
        OrganizerRepository organizer,
        IDownloaderRepository downloader,
        Func<IDownloadsModuleBoundary?>? downloads = null)
    {
        _catalogDatabasePath = catalogDatabasePath;
        _organizer = organizer;
        _downloader = downloader;
        _downloads = downloads;
    }

    public string Name => "NexusRequirements";

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started || _stopped)
            throw new InvalidOperationException($"{Name} cannot be restarted.");
        cancellationToken.ThrowIfCancellationRequested();

        _httpClient = new HttpClient();
        var v3Client = new NexusV3RequirementClient(_httpClient);
        _service = new NexusRequirementSyncService(
            new NexusGraphQlRequirementClient(_httpClient),
            new NexusRequirementSnapshotStore(_catalogDatabasePath),
            new NexusRequirementAvailabilityResolver(
                _organizer,
                _downloader),
            modernClient: v3Client,
            downloads: _downloads);
        _started = true;
        return Task.CompletedTask;
    }

    public Task<NexusRequirementSyncResult> RefreshAsync(
        NexusModIdentity mod,
        bool clearDismissals = false,
        CancellationToken cancellationToken = default) =>
        RequireService().RefreshAsync(mod, clearDismissals, cancellationToken);

    public Task<NexusRequirementRelations> LoadRelationsAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken = default) =>
        RequireService().LoadRelationsAsync(mod, cancellationToken);

    public Task DismissRelationAsync(
        NexusRequirementCanonicalKey key,
        CancellationToken cancellationToken = default) =>
        RequireService().DismissRelationAsync(key, cancellationToken);

    public Task<bool> AddToDownloadsAsync(
        NexusModIdentity targetMod,
        string displayName,
        string? safeUrl = null,
        CancellationToken cancellationToken = default) =>
        RequireService().AddToDownloadsAsync(targetMod, displayName, safeUrl, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stopped = true;
        _started = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _stopped = true;
        _started = false;
        _service = null;
        _httpClient?.Dispose();
        _httpClient = null;
        return ValueTask.CompletedTask;
    }

    private NexusRequirementSyncService RequireService()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _started && _service is not null
            ? _service
            : throw new InvalidOperationException(
                $"{Name} has not completed startup.");
    }
}
