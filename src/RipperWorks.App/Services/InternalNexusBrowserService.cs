using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RipperWorks.Downloader;

namespace RipperWorks.App.Services;

public interface IInternalBrowserWindowService
{
    Task ShowAsync(
        ManualBrowserQueue queue,
        LocalizationService localization);
}

public interface IExternalNexusBrowserService
{
    Task OpenAsync(ManualBrowserItem item);
}

public sealed class ExternalNexusBrowserService(
    IDownloaderDialogService dialogs)
    : IExternalNexusBrowserService
{
    public Task OpenAsync(ManualBrowserItem item)
    {
        dialogs.OpenUri(item.PageUri.AbsoluteUri);
        return Task.CompletedTask;
    }
}

public sealed class InternalBrowserWindowService(string userDataFolder)
    : IInternalBrowserWindowService
{
    private CoreWebView2Environment? _environment;
    private InternalBrowserWindow? _window;

    public Task ShowAsync(
        ManualBrowserQueue queue,
        LocalizationService localization)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(
                    () => ShowCoreAsync(queue, localization))
                .Task
                .Unwrap();
        }
        return ShowCoreAsync(queue, localization);
    }

    private async Task ShowCoreAsync(
        ManualBrowserQueue queue,
        LocalizationService localization)
    {
        Directory.CreateDirectory(userDataFolder);
        _environment ??= await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: null);
        if (_window is null)
        {
            _window = new InternalBrowserWindow
            {
                Owner = Application.Current?.MainWindow
            };
            _window.Closed += (_, _) => _window = null;
            _window.Show();
            try
            {
                await _window.InitializeAsync(
                    _environment,
                    queue,
                    localization);
            }
            catch
            {
                _window.Close();
                throw;
            }
        }
        else
        {
            await _window.AttachAsync(queue, localization);
            if (!_window.IsVisible)
                _window.Show();
        }
        _window.Activate();
    }
}
