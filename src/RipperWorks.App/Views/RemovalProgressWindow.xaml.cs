using System.ComponentModel;
using System.Windows;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;

namespace RipperWorks.App.Views;

public partial class RemovalProgressWindow : Window
{
    private readonly Func<Task> _remove;
    private readonly Action<Exception>? _exceptionLogger;
    private readonly TaskCompletionSource _contentRendered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    public RemovalProgressWindow(
        string modName,
        Func<Task> remove,
        LocalizationService localization,
        Action<Exception>? exceptionLogger = null,
        string? titleKey = null,
        string? statusKey = null)
        : this(
            new RemovalProgressViewModel(
                modName,
                localization,
                titleKey,
                statusKey),
            remove,
            exceptionLogger)
    {
    }

    internal RemovalProgressWindow(
        RemovalProgressViewModel viewModel,
        Func<Task> remove,
        Action<Exception>? exceptionLogger = null)
    {
        _remove = remove;
        _exceptionLogger = exceptionLogger;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        IsVisibleChanged += OnIsVisibleChanged;
        Closed += OnClosed;
        Closing += OnClosing;
        ResponsivenessTraceSession.Current?.Event(
            "WINDOW_CREATED",
            detail: "window=RemovalProgressWindow");
    }

    public Exception? Failure { get; private set; }
    internal bool HasRenderedContent { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ResponsivenessTraceSession.Current?.Event(
            "LOADED",
            detail: "window=RemovalProgressWindow");
        try
        {
            await _contentRendered.Task;
            ResponsivenessTraceSession.Current?.Event(
                "OPERATION_CALLBACK_STARTED",
                detail: "window=RemovalProgressWindow");
            try
            {
                await _remove();
            }
            finally
            {
                ResponsivenessTraceSession.Current?.Event(
                    "OPERATION_CALLBACK_COMPLETED",
                    detail: "window=RemovalProgressWindow");
            }
        }
        catch (Exception exception)
        {
            Failure = exception;
            _exceptionLogger?.Invoke(exception);
        }
        finally
        {
            _completed = true;
            Close();
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        HasRenderedContent = true;
        ResponsivenessTraceSession.Current?.Event(
            "CONTENT_RENDERED",
            detail: "window=RemovalProgressWindow");
        _contentRendered.TrySetResult();
    }

    private void OnIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
            return;
        IsVisibleChanged -= OnIsVisibleChanged;
        ResponsivenessTraceSession.Current?.Event(
            "WINDOW_SHOWN",
            detail: "window=RemovalProgressWindow");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ResponsivenessTraceSession.Current?.Event(
            "WINDOW_CLOSED",
            detail: "window=RemovalProgressWindow");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_completed)
            e.Cancel = true;
    }
}
