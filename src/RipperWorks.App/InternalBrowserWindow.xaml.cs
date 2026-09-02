using System.ComponentModel;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Downloader;

namespace RipperWorks.App;

public sealed class InternalBrowserWindowViewModel : ObservableObject
{
    private string _title = "Nexus";
    private string _currentName = string.Empty;
    private string _currentUrl = string.Empty;
    private string _progressText = string.Empty;
    private string _statusText = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
    public string CurrentName
    {
        get => _currentName;
        set => SetProperty(ref _currentName, value);
    }
    public string CurrentUrl
    {
        get => _currentUrl;
        set => SetProperty(ref _currentUrl, value);
    }
    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }
    public string BackLabel { get; set; } = "Назад";
    public string ForwardLabel { get; set; } = "Следующая";
    public string ReloadLabel { get; set; } = "Повторить";
    public string SkipLabel { get; set; } = "Пропустить";
    public string StopLabel { get; set; } = "Прекратить обработку";

    public void Refresh() => OnPropertyChanged(string.Empty);
}

public partial class InternalBrowserWindow : Window
{
    private readonly Dictionary<string, DateTimeOffset> _recentNxm =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly InternalBrowserWindowViewModel _viewModel = new();
    private ManualBrowserQueue? _queue;
    private LocalizationService? _localization;
    private bool _allowClose;

    public InternalBrowserWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closing += OnClosing;
        Closed += (_, _) => Browser.Dispose();
    }

    public async Task InitializeAsync(
        CoreWebView2Environment environment,
        ManualBrowserQueue queue,
        LocalizationService localization)
    {
        await AttachAsync(queue, localization);
        await Browser.EnsureCoreWebView2Async(environment);
        var core = Browser.CoreWebView2 ??
            throw new InvalidOperationException(
                "WebView2 could not be initialized.");
        core.LaunchingExternalUriScheme += OnExternalScheme;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ProcessFailed += OnProcessFailed;
        NavigateCurrent();
    }

    public Task AttachAsync(
        ManualBrowserQueue queue,
        LocalizationService localization)
    {
        if (_queue is not null)
            _queue.Changed -= Queue_OnChanged;
        _queue = queue;
        _localization = localization;
        queue.Changed += Queue_OnChanged;
        Render();
        NavigateCurrent();
        return Task.CompletedTask;
    }

    private async void OnExternalScheme(
        object? sender,
        CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        var deferral = e.GetDeferral();
        try
        {
            if (IsNxm(e.Uri))
                await HandleNxmOnceAsync(e.Uri);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }
        if (IsNxm(e.Uri))
        {
            e.Cancel = true;
            _ = HandleNxmOnceAsync(e.Uri);
            return;
        }
        if (uri.Scheme is not ("http" or "https"))
            e.Cancel = true;
    }

    private async void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            if (IsNxm(e.Uri))
                await HandleNxmOnceAsync(e.Uri);
            else if (Uri.TryCreate(
                         e.Uri,
                         UriKind.Absolute,
                         out var uri) &&
                     uri.Scheme is "http" or "https")
                Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        if (_queue is null ||
            !Uri.TryCreate(
                e.DownloadOperation.Uri,
                UriKind.Absolute,
                out var uri))
        {
            return;
        }
        try
        {
            await _queue.HandleDirectAsync(uri);
        }
        catch
        {
            Render();
        }
    }

    private void OnProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs e)
    {
        _viewModel.StatusText =
            $"{L("BrowserError")}: {e.ProcessFailedKind}";
    }

    private async Task HandleNxmOnceAsync(string value)
    {
        if (_queue is null)
            return;
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in _recentNxm
                     .Where(pair =>
                         now - pair.Value > TimeSpan.FromSeconds(5))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _recentNxm.Remove(stale);
        }
        if (_recentNxm.ContainsKey(value))
            return;
        _recentNxm[value] = now;
        try
        {
            await _queue.HandleNxmAsync(value);
        }
        catch
        {
            Render();
        }
    }

    private void Queue_OnChanged(object? sender, EventArgs e)
    {
        Render();
        NavigateCurrent();
        if (_queue?.State == ManualBrowserState.Completed)
        {
            _allowClose = true;
            Close();
        }
    }

    private void Render()
    {
        _viewModel.Title = L("BrowserTitle");
        _viewModel.BackLabel = L("BrowserBack");
        _viewModel.ForwardLabel = L("BrowserForward");
        _viewModel.ReloadLabel = L("BrowserReload");
        _viewModel.SkipLabel = L("BrowserSkip");
        _viewModel.StopLabel = L("BrowserStop");
        var current = _queue?.Current;
        _viewModel.CurrentName = current?.Entry.Name ?? string.Empty;
        _viewModel.CurrentUrl = current?.PageUri.AbsoluteUri ??
            string.Empty;
        _viewModel.ProgressText = current is null
            ? string.Empty
            : string.Format(
                L("BrowserProgress"),
                current.Position,
                current.Total);
        _viewModel.StatusText = _queue is null
            ? string.Empty
            : _queue.State == ManualBrowserState.Error
                ? $"{L("BrowserError")}: {_queue.Error}"
                : L($"BrowserState{_queue.State}");
        _viewModel.Refresh();
    }

    private void NavigateCurrent()
    {
        var uri = _queue?.Current?.PageUri;
        if (uri is not null &&
            Browser.CoreWebView2 is not null &&
            !string.Equals(
                Browser.Source?.AbsoluteUri,
                uri.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase))
        {
            Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack)
            Browser.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoForward)
            Browser.GoForward();
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_queue is not null)
            await _queue.SkipAsync();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) =>
        Browser.Reload();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _queue?.Stop();
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
            _queue?.Stop();
        _allowClose = true;
        if (_queue is not null)
            _queue.Changed -= Queue_OnChanged;
    }

    private string L(string key) =>
        _localization?.Get(key) ?? key;

    private static bool IsNxm(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(
            "nxm",
            StringComparison.OrdinalIgnoreCase);
}
