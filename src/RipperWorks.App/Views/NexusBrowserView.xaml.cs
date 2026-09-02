using System.IO;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;
using RipperWorks.Infrastructure;

namespace RipperWorks.App.Views;

public partial class NexusBrowserView : UserControl, IDisposable
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private CoreWebView2Environment? _environment;
    private CoreWebView2? _coreWebView2;
    private bool _isInitialized;
    private bool _isDisposed;
    private bool _isEnsureInProgress;
    private bool _isTeardownDone;
    private Uri? _lastKnownAllowedUri;
    private bool _isPolicyRecoveryInProgress;
    private string? _pendingExplicitTargetUri;

    public NexusBrowserViewModel? ViewModel => DataContext as NexusBrowserViewModel;
    internal CoreWebView2? CoreWebView2Instance => _coreWebView2;
    internal Uri? LastKnownAllowedUri => _lastKnownAllowedUri;
    internal bool IsPolicyRecoveryInProgress => _isPolicyRecoveryInProgress;
    public bool HasExplicitTarget { get; private set; }
    public string? PendingExplicitTargetUri => _pendingExplicitTargetUri;
    internal Func<Task>? OnBeforeEnvironmentCreationHook { get; set; }
    internal Func<Task>? OnBeforeEnsureCoreWebView2Hook { get; set; }

    public NexusBrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is NexusBrowserViewModel oldVm)
        {
            oldVm.NavigationRequested -= OnNavigationRequested;
        }

        if (e.NewValue is NexusBrowserViewModel newVm)
        {
            newVm.NavigationRequested += OnNavigationRequested;
            if (newVm.PendingExplicitTargetUrl is not null)
            {
                Navigate(newVm.PendingExplicitTargetUrl);
            }
        }
    }

    private void OnNavigationRequested(string uriString)
    {
        Navigate(uriString);
    }

    public async Task InitializeAsync(
        string? userDataFolder = null,
        CoreWebView2Environment? environment = null)
    {
        lock (_stateLock)
        {
            if (_isDisposed || _isInitialized)
                return;
        }

        await _initGate.WaitAsync();
        try
        {
            lock (_stateLock)
            {
                if (_isDisposed || _isInitialized)
                    return;
                _isEnsureInProgress = true;
            }

            if (OnBeforeEnvironmentCreationHook is not null)
            {
                await OnBeforeEnvironmentCreationHook();
                lock (_stateLock)
                {
                    if (_isDisposed)
                        return;
                }
            }

            if (environment is not null)
            {
                _environment = environment;
            }
            else
            {
                var folder = userDataFolder ?? new RipperWorksPaths().WebView2Directory;
                Directory.CreateDirectory(folder);
                _environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: folder,
                    options: null);
            }

            lock (_stateLock)
            {
                if (_isDisposed)
                    return;
            }

            if (OnBeforeEnsureCoreWebView2Hook is not null)
            {
                await OnBeforeEnsureCoreWebView2Hook();
                lock (_stateLock)
                {
                    if (_isDisposed)
                        return;
                }
            }

            await Browser.EnsureCoreWebView2Async(_environment);

            string? pendingTarget = null;
            lock (_stateLock)
            {
                if (_isDisposed)
                    return;

                var core = Browser.CoreWebView2 ??
                    throw new InvalidOperationException("CoreWebView2 was not initialized.");

                _coreWebView2 = core;
                AttachEvents(core);
                _isInitialized = true;
                if (_pendingExplicitTargetUri is not null)
                {
                    pendingTarget = _pendingExplicitTargetUri;
                    _pendingExplicitTargetUri = null;
                }
            }

            if (pendingTarget is not null)
            {
                _coreWebView2?.Navigate(pendingTarget);
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _isEnsureInProgress = false;
                if (_isDisposed)
                {
                    PerformFinalTeardown();
                }
            }
            _initGate.Release();
        }
    }

    public void Navigate(string uriString) => NavigateInternal(uriString, isExplicitTarget: true);

    public void Navigate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Navigate(uri.AbsoluteUri);
    }

    public void NavigateHome()
    {
        ViewModel?.ClearPendingTarget();
        NavigateInternal(NexusBrowserViewModel.DefaultHomeUrl, isExplicitTarget: false);
    }

    private void NavigateInternal(string uriString, bool isExplicitTarget)
    {
        lock (_stateLock)
        {
            if (_isDisposed)
                return;
        }

        var decision = NexusBrowserNavigationPolicy.Evaluate(uriString);
        if (!decision.IsAllowed)
            return;

        CoreWebView2? core;
        lock (_stateLock)
        {
            if (isExplicitTarget)
            {
                HasExplicitTarget = true;
                _pendingExplicitTargetUri = uriString;
            }
            else
            {
                HasExplicitTarget = false;
                _pendingExplicitTargetUri = null;
            }
            core = _coreWebView2;
            if (core is null)
            {
                return;
            }
            _pendingExplicitTargetUri = null;
        }

        core.Navigate(uriString);
    }

    internal static bool EvaluateNavigationStarting(string? uriString)
    {
        var decision = NexusBrowserNavigationPolicy.Evaluate(uriString);
        return !decision.IsAllowed || decision.Classification is NexusBrowserNavigationClassification.NxmProtocol;
    }

    internal static bool EvaluateNewWindowRequested(string? uriString, out bool shouldNavigateInSameHost)
    {
        var decision = NexusBrowserNavigationPolicy.Evaluate(uriString);
        shouldNavigateInSameHost = decision.IsAllowed;
        return true; // Always handled to prevent default OS window opening
    }

    internal static bool EvaluateDownloadStarting()
    {
        return true; // Always cancel direct unmanaged downloads
    }

    internal static bool EvaluateLaunchingExternalUriScheme(string? uriString)
    {
        return true; // Always cancel external URI protocol escapes
    }

    internal static bool EvaluateActualSourceContainment(
        Uri? actualSource,
        Uri? lastKnownAllowedUri,
        out Uri? newLastKnownAllowedUri,
        out bool shouldRecover,
        out Uri? recoveryTarget)
    {
        newLastKnownAllowedUri = lastKnownAllowedUri;
        shouldRecover = false;
        recoveryTarget = null;

        if (actualSource is null)
        {
            return true;
        }

        if (string.Equals(actualSource.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            // Case A: Startup / pre-navigation initialization (no prior allowed URI) -> tolerated, no recovery
            if (lastKnownAllowedUri is null)
            {
                return true;
            }

            // Case B: Post-startup navigation (prior allowed URI exists) -> Disallowed, recover to last known allowed URI
            shouldRecover = true;
            var target = lastKnownAllowedUri;
            var targetDecision = NexusBrowserNavigationPolicy.Evaluate(target);
            recoveryTarget = targetDecision.IsAllowed ? target : new Uri(NexusBrowserViewModel.DefaultHomeUrl);
            return false;
        }

        var decision = NexusBrowserNavigationPolicy.Evaluate(actualSource);
        if (decision.IsAllowed)
        {
            newLastKnownAllowedUri = actualSource;
            return true;
        }

        shouldRecover = true;
        var fallbackTarget = lastKnownAllowedUri ?? new Uri(NexusBrowserViewModel.DefaultHomeUrl);
        var fallbackDecision = NexusBrowserNavigationPolicy.Evaluate(fallbackTarget);
        recoveryTarget = fallbackDecision.IsAllowed ? fallbackTarget : new Uri(NexusBrowserViewModel.DefaultHomeUrl);
        return false;
    }

    internal void ValidateActualSourceAndPublish()
    {
        if (_isDisposed)
            return;

        var sourceUri = Browser.Source;
        if (sourceUri is null)
            return;

        var isAllowed = EvaluateActualSourceContainment(
            sourceUri,
            _lastKnownAllowedUri,
            out var newAllowed,
            out var shouldRecover,
            out var recoveryTarget);

        if (isAllowed)
        {
            _lastKnownAllowedUri = newAllowed;
            _isPolicyRecoveryInProgress = false;
            ViewModel?.UpdateCurrentLocation(
                sourceUri.AbsoluteUri,
                Browser.CanGoBack,
                Browser.CanGoForward);
        }
        else
        {
            ViewModel?.UpdateCurrentLocation(
                null,
                Browser.CanGoBack,
                Browser.CanGoForward);

            if (shouldRecover && !_isPolicyRecoveryInProgress && recoveryTarget is not null)
            {
                _isPolicyRecoveryInProgress = true;
                _coreWebView2?.Navigate(recoveryTarget.AbsoluteUri);
            }
        }
    }

    private void AttachEvents(CoreWebView2 core)
    {
        var attached = 0;
        try
        {
            core.NavigationStarting += Core_OnNavigationStarting; attached++;
            core.SourceChanged += Core_OnSourceChanged; attached++;
            core.HistoryChanged += Core_OnHistoryChanged; attached++;
            core.NavigationCompleted += Core_OnNavigationCompleted; attached++;
            core.NewWindowRequested += Core_OnNewWindowRequested; attached++;
            core.DownloadStarting += Core_OnDownloadStarting; attached++;
            core.LaunchingExternalUriScheme += Core_OnLaunchingExternalUriScheme; attached++;
        }
        catch
        {
            if (attached > 6) core.LaunchingExternalUriScheme -= Core_OnLaunchingExternalUriScheme;
            if (attached > 5) core.DownloadStarting -= Core_OnDownloadStarting;
            if (attached > 4) core.NewWindowRequested -= Core_OnNewWindowRequested;
            if (attached > 3) core.NavigationCompleted -= Core_OnNavigationCompleted;
            if (attached > 2) core.HistoryChanged -= Core_OnHistoryChanged;
            if (attached > 1) core.SourceChanged -= Core_OnSourceChanged;
            if (attached > 0) core.NavigationStarting -= Core_OnNavigationStarting;
            throw;
        }
    }

    private void DetachEvents(CoreWebView2 core)
    {
        try
        {
            core.NavigationStarting -= Core_OnNavigationStarting;
            core.SourceChanged -= Core_OnSourceChanged;
            core.HistoryChanged -= Core_OnHistoryChanged;
            core.NavigationCompleted -= Core_OnNavigationCompleted;
            core.NewWindowRequested -= Core_OnNewWindowRequested;
            core.DownloadStarting -= Core_OnDownloadStarting;
            core.LaunchingExternalUriScheme -= Core_OnLaunchingExternalUriScheme;
        }
        catch
        {
            // Best effort when COM handle is already disposed
        }
    }

    private void Core_OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        var cancel = EvaluateNavigationStarting(e.Uri);
        e.Cancel = cancel;

        if (ViewModel is not null)
        {
            ViewModel.IsLoading = !cancel;
        }
    }

    private void Core_OnSourceChanged(
        object? sender,
        CoreWebView2SourceChangedEventArgs e)
    {
        ValidateActualSourceAndPublish();
    }

    private void Core_OnHistoryChanged(
        object? sender,
        object e)
    {
        ValidateActualSourceAndPublish();
    }

    private void Core_OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.IsLoading = false;
        }
        ValidateActualSourceAndPublish();
    }

    private void Core_OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = EvaluateNewWindowRequested(e.Uri, out var shouldNavigate);
        if (shouldNavigate && _coreWebView2 is not null)
        {
            _coreWebView2.Navigate(e.Uri);
        }
    }

    private void Core_OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = EvaluateDownloadStarting();
    }

    private void Core_OnLaunchingExternalUriScheme(
        object? sender,
        CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = EvaluateLaunchingExternalUriScheme(e.Uri);
    }

    private void PerformFinalTeardown()
    {
        if (_isTeardownDone)
            return;

        _isTeardownDone = true;

        if (_coreWebView2 is not null)
        {
            DetachEvents(_coreWebView2);
            _coreWebView2 = null;
        }

        try
        {
            Browser.Dispose();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        DataContextChanged -= OnDataContextChanged;
        if (ViewModel is { } vm)
        {
            vm.NavigationRequested -= OnNavigationRequested;
        }

        lock (_stateLock)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _pendingExplicitTargetUri = null;
            HasExplicitTarget = false;

            if (!_isEnsureInProgress)
            {
                PerformFinalTeardown();
            }
        }

        ViewModel?.Dispose();
    }
}
