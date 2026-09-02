using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Downloader;
using RipperWorks.Infrastructure;

namespace RipperWorks.App.ViewModels;

public sealed class NexusBrowserViewModel : ObservableObject, IDisposable
{
    public const string DefaultHomeUrl = "https://www.nexusmods.com/games/cyberpunk2077";

    private Uri? _currentUri;
    private NexusPageContext? _currentPageContext;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private string? _statusText;
    private string? _addressText;
    private bool _isDisposed;

    public NexusBrowserViewModel(
        IShortlistStore? shortlistStore = null,
        INexusModLocalStateService? localStateService = null,
        LocalizationService? localization = null,
        INexusApiClient? nexusApi = null,
        IProtectedCredentialStore? credentials = null,
        INexusRequirementRelationsService? requirementRelationsService = null,
        INexusRequirementRefreshService? requirementRefreshService = null,
        Action<NexusModIdentity>? requestNavigateDownloads = null)
    {
        var store = shortlistStore ?? new FallbackShortlistStore();
        var stateService = localStateService ?? new FallbackNexusModLocalStateService(store);
        var loc = localization ?? new LocalizationService(SupportedLanguages.Russian);

        Panel = new NexusBrowserPanelViewModel(
            store,
            stateService,
            loc,
            url => NavigationRequested?.Invoke(url),
            nexusApi,
            credentials,
            requirementRelationsService,
            requirementRefreshService,
            requestNavigateDownloads);
    }

    public NexusBrowserPanelViewModel Panel { get; }

    public event Action<string>? NavigationRequested;

    private string? _pendingExplicitTargetUrl;
    public string? PendingExplicitTargetUrl => _pendingExplicitTargetUrl;

    public void Navigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        var decision = NexusBrowserNavigationPolicy.Evaluate(url);
        if (!decision.IsAllowed)
            return;
        _pendingExplicitTargetUrl = url;
        NavigationRequested?.Invoke(url);
    }

    public void ClearPendingTarget()
    {
        _pendingExplicitTargetUrl = null;
    }

    public Uri? CurrentUri
    {
        get => _currentUri;
        private set
        {
            if (SetProperty(ref _currentUri, value))
            {
                AddressText = value?.AbsoluteUri;
            }
        }
    }

    public NexusPageContext? CurrentPageContext
    {
        get => _currentPageContext;
        private set
        {
            if (SetProperty(ref _currentPageContext, value))
            {
                Panel.SetCurrentPageContext(value);
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        set => SetProperty(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        set => SetProperty(ref _canGoForward, value);
    }

    public string? StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string? AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    public NexusBrowserNavigationDecision EvaluateNavigation(string? uriString)
    {
        return NexusBrowserNavigationPolicy.Evaluate(uriString);
    }

    public void UpdateCurrentLocation(string? uriString, bool canGoBack = false, bool canGoForward = false)
    {
        if (_isDisposed)
            return;

        CanGoBack = canGoBack;
        CanGoForward = canGoForward;

        if (string.IsNullOrWhiteSpace(uriString) || !Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            CurrentUri = null;
            CurrentPageContext = null;
            return;
        }

        var decision = NexusBrowserNavigationPolicy.Evaluate(uri);
        if (!decision.IsAllowed)
        {
            CurrentUri = null;
            CurrentPageContext = null;
            StatusText = decision.Reason ?? "Navigation blocked by policy.";
            return;
        }

        CurrentUri = uri;
        CurrentPageContext = decision.PageContext;
        StatusText = decision.Classification is NexusBrowserNavigationClassification.AllowedModPage
            ? $"Mod {decision.PageContext?.NexusModId}"
            : (decision.Classification is NexusBrowserNavigationClassification.AllowedNexusAuthPage
                ? "Nexus Auth"
                : "Cyberpunk 2077 Hub");
    }

    public Task OnActivatedAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            return Task.CompletedTask;

        return Panel.OnActivatedAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        CurrentUri = null;
        CurrentPageContext = null;
        Panel.Dispose();
    }

    private sealed class FallbackNexusModLocalStateService : INexusModLocalStateService
    {
        private readonly IShortlistStore _store;

        public FallbackNexusModLocalStateService(IShortlistStore store) => _store = store;

        public async Task<NexusModLocalState> QueryAsync(
            string gameDomain,
            long nexusModId,
            CancellationToken cancellationToken = default)
        {
            var isShortlisted = await _store.ContainsAsync(gameDomain, nexusModId, cancellationToken);
            return new NexusModLocalState
            {
                GameDomain = gameDomain,
                NexusModId = nexusModId,
                IsShortlisted = isShortlisted
            };
        }
    }

    private sealed class FallbackShortlistStore : IShortlistStore
    {
        private readonly List<ShortlistEntryRecord> _entries = [];
        private readonly object _lock = new();

        public string FilePath => "in-memory://fallback-shortlist.json";

        public Task<ShortlistDocument> LoadAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(new ShortlistDocument
                {
                    FormatVersion = RipperWorksShortlistSchema.CurrentVersion,
                    Items = _entries.ToArray()
                });
            }
        }

        public Task SaveAsync(ShortlistDocument document, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _entries.Clear();
                if (document.Items is not null)
                {
                    _entries.AddRange(document.Items);
                }
                return Task.CompletedTask;
            }
        }

        public Task<IReadOnlyList<ShortlistEntryRecord>> LoadEntriesAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<ShortlistEntryRecord>>(_entries.ToArray());
            }
        }

        public Task<bool> AddAsync(
            string gameDomain,
            long nexusModId,
            string? name = null,
            string? author = null,
            string? lastKnownVersion = null,
            DateTimeOffset? addedAtUtc = null,
            CancellationToken cancellationToken = default)
        {
            var key = new ShortlistEntryKey(gameDomain, nexusModId);
            lock (_lock)
            {
                var index = _entries.FindIndex(e => e.Key.EqualsCanonical(key));
                if (index >= 0)
                {
                    var existing = _entries[index];
                    _entries[index] = existing with
                    {
                        Name = name ?? existing.Name,
                        Author = author ?? existing.Author,
                        LastKnownVersion = lastKnownVersion ?? existing.LastKnownVersion
                    };
                    return Task.FromResult(false);
                }

                _entries.Add(new ShortlistEntryRecord
                {
                    GameDomain = key.GameDomain,
                    NexusModId = key.NexusModId,
                    AddedAtUtc = addedAtUtc ?? DateTimeOffset.UtcNow,
                    Name = name,
                    Author = author,
                    LastKnownVersion = lastKnownVersion
                });
                return Task.FromResult(true);
            }
        }

        public Task<bool> RemoveAsync(string gameDomain, long nexusModId, CancellationToken cancellationToken = default)
        {
            var key = new ShortlistEntryKey(gameDomain, nexusModId);
            lock (_lock)
            {
                var removed = _entries.RemoveAll(e => e.Key.EqualsCanonical(key)) > 0;
                return Task.FromResult(removed);
            }
        }

        public Task<bool> ContainsAsync(string gameDomain, long nexusModId, CancellationToken cancellationToken = default)
        {
            var key = new ShortlistEntryKey(gameDomain, nexusModId);
            lock (_lock)
            {
                var exists = _entries.Any(e => e.Key.EqualsCanonical(key));
                return Task.FromResult(exists);
            }
        }
    }
}
