using System.Windows;
using System.Windows.Threading;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Infrastructure;

namespace RipperWorks.App;

public partial class App : Application
{
    private readonly StartupExceptionLogger _exceptionLogger;
    private readonly ApplicationRuntime _runtime;
    private static readonly TimeSpan ShellShutdownTimeout =
        TimeSpan.FromSeconds(8);

    public App()
    {
        var paths = new RipperWorksPaths();
        _exceptionLogger = new StartupExceptionLogger(paths);
        _runtime = ApplicationComposition.Build(paths, _exceptionLogger);
        _runtime.ActivationRequested += Runtime_OnActivationRequested;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var argument = e.Args.FirstOrDefault(value =>
            value.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
        if (!await _runtime.StartAsync(argument))
        {
            Shutdown();
            return;
        }
        var window = new MainWindow
        {
            DataContext = _runtime.MainViewModel
        };
        MainWindow = window;
        window.Show();
        if (argument is not null)
            await _runtime.HandleExternalArgumentAsync(argument);
    }

    private void Runtime_OnActivationRequested(
        object? sender,
        InstanceMessage message)
    {
        var operation = Dispatcher.InvokeAsync(
            async () =>
        {
            if (MainWindow is { } window)
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
            }
            if (!message.IsActivationOnly)
                await _runtime.HandleExternalArgumentAsync(message.Argument);
        });
        _ = ObserveActivationAsync(operation.Task.Unwrap());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var shutdown = _runtime.DisposeAsync().AsTask();
            WpfShutdownBridge.Wait(
                Dispatcher,
                shutdown,
                ShellShutdownTimeout);
        }
        catch (TimeoutException exception)
        {
            _exceptionLogger.Log("ApplicationShutdownTimeout", exception);
        }
        catch (Exception exception)
        {
            _exceptionLogger.Log("ApplicationShutdown", exception);
        }
        finally
        {
            _runtime.ActivationRequested -= Runtime_OnActivationRequested;
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -=
                OnCurrentDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -=
                OnUnobservedTaskException;
            base.OnExit(e);
        }
    }

    private async Task ObserveActivationAsync(Task activation)
    {
        try
        {
            await activation;
        }
        catch (Exception exception)
        {
            _exceptionLogger.Log("ApplicationActivation", exception);
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        LibrarySelectionDiagnostics.Dump(
            nameof(Application.DispatcherUnhandledException),
            e.Exception);
        var logPath = _exceptionLogger.Log(
            nameof(Application.DispatcherUnhandledException),
            e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"RipperWorks could not start.{Environment.NewLine}" +
            $"Full error details: {logPath}",
            "RipperWorks",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }

    private void OnCurrentDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException(
                $"Unhandled non-Exception object: {e.ExceptionObject}");
        LibrarySelectionDiagnostics.Dump(
            nameof(AppDomain.UnhandledException),
            exception);
        _exceptionLogger.Log(
            nameof(AppDomain.UnhandledException),
            exception);
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        LibrarySelectionDiagnostics.Dump(
            nameof(TaskScheduler.UnobservedTaskException),
            e.Exception);
        _exceptionLogger.Log(
            nameof(TaskScheduler.UnobservedTaskException),
            e.Exception);
        e.SetObserved();
    }
}
