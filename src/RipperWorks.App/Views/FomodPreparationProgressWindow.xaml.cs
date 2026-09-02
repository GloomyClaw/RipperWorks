using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;

namespace RipperWorks.App.Views;

public partial class FomodPreparationProgressWindow : Window
{
    private readonly Func<IProgress<FomodPreparationPhase>, Task> _prepare;
    private readonly FomodPreparationProgressViewModel _viewModel;
    private readonly Action<Exception>? _exceptionLogger;
    private bool _completed;

    public FomodPreparationProgressWindow(
        string modName,
        Func<IProgress<FomodPreparationPhase>, Task> prepare,
        LocalizationService localization,
        Action<Exception>? exceptionLogger = null)
    {
        _prepare = prepare;
        _viewModel = new(modName, localization);
        _exceptionLogger = exceptionLogger;
        DataContext = _viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        IsVisibleChanged += OnIsVisibleChanged;
        Closed += OnClosed;
        Closing += OnClosing;
        ResponsivenessTraceSession.Current?.Event(
            "WINDOW_CREATED",
            detail: "window=FomodPreparationProgressWindow");
    }

    public Exception? Failure { get; private set; }
    internal bool HasRenderedContent { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ResponsivenessTraceSession.Current?.Event(
            "LOADED",
            detail: "window=FomodPreparationProgressWindow");
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            IProgress<FomodPreparationPhase> progress =
                new Progress<FomodPreparationPhase>(_viewModel.Update);
            progress = new ResponsivenessProgress<FomodPreparationPhase>(
                progress,
                () => ResponsivenessTraceSession.Current?.Event(
                    "FIRST_PROGRESS_REPORT",
                    detail: "window=FomodPreparationProgressWindow"));
            ResponsivenessTraceSession.Current?.Event(
                "OPERATION_CALLBACK_STARTED",
                detail: "window=FomodPreparationProgressWindow");
            try
            {
                await _prepare(progress);
            }
            finally
            {
                ResponsivenessTraceSession.Current?.Event(
                    "OPERATION_CALLBACK_COMPLETED",
                    detail: "window=FomodPreparationProgressWindow");
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
            detail: "window=FomodPreparationProgressWindow");
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
            detail: "window=FomodPreparationProgressWindow");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ResponsivenessTraceSession.Current?.Event(
            "WINDOW_CLOSED",
            detail: "window=FomodPreparationProgressWindow");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_completed)
            e.Cancel = true;
    }
}
