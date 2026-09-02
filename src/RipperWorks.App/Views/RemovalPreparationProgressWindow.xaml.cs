using System.ComponentModel;
using System.Windows;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;

namespace RipperWorks.App.Views;

public partial class RemovalPreparationProgressWindow : Window
{
    private readonly Func<Task> _prepare;
    private readonly Action<Exception>? _exceptionLogger;
    private readonly TaskCompletionSource _contentRendered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    public RemovalPreparationProgressWindow(
        string modName,
        Func<Task> prepare,
        LocalizationService localization,
        Action<Exception>? exceptionLogger = null)
    {
        _prepare = prepare;
        _exceptionLogger = exceptionLogger;
        DataContext = new RemovalPreparationProgressViewModel(
            modName,
            localization);
        InitializeComponent();
        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        IsVisibleChanged += OnIsVisibleChanged;
        Closed += OnClosed;
        Closing += OnClosing;
        ResponsivenessTraceSession.Current?.Event(
            "WINDOW_CREATED",
            detail: "window=RemovalPreparationProgressWindow");
    }

    public Exception? Failure { get; private set; }
    internal bool HasRenderedContent { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ResponsivenessTraceSession.Current?.Event(
            "LOADED",
            detail: "window=RemovalPreparationProgressWindow");
        try
        {
            await _contentRendered.Task;
            ResponsivenessTraceSession.Current?.Event(
                "OPERATION_CALLBACK_STARTED",
                detail: "window=RemovalPreparationProgressWindow");
            try
            {
                await _prepare();
            }
            finally
            {
                ResponsivenessTraceSession.Current?.Event(
                    "OPERATION_CALLBACK_COMPLETED",
                    detail: "window=RemovalPreparationProgressWindow");
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
            detail: "window=RemovalPreparationProgressWindow");
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
            detail: "window=RemovalPreparationProgressWindow");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ResponsivenessTraceSession.Current?.Event(
            "WINDOW_CLOSED",
            detail: "window=RemovalPreparationProgressWindow");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_completed)
            e.Cancel = true;
    }
}
