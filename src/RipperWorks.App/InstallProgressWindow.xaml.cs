using System.ComponentModel;
using System.Windows;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;

namespace RipperWorks.App;

public partial class InstallProgressWindow : Window
{
    private readonly Func<IProgress<ModInstallationProgress>,
        CancellationToken, Task<ModInstallationResult>> _install;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly InstallProgressViewModel _viewModel;
    private readonly Action<Exception>? _exceptionLogger;
    private readonly TaskCompletionSource _contentRendered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    public InstallProgressWindow(
        string modName,
        Func<IProgress<ModInstallationProgress>, CancellationToken,
            Task<ModInstallationResult>> install,
        LocalizationService localization,
        Action<Exception>? exceptionLogger = null)
    {
        _install = install;
        _viewModel = new InstallProgressViewModel(modName, localization);
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
            detail: "window=InstallProgressWindow");
    }

    public ModInstallationResult? Result { get; private set; }
    internal bool HasRenderedContent { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ResponsivenessTraceSession.Current?.Event(
            "LOADED",
            detail: "window=InstallProgressWindow");
        try
        {
            await _contentRendered.Task;
            IProgress<ModInstallationProgress> progress =
                new Progress<ModInstallationProgress>(
                value =>
                {
                    if (Dispatcher.CheckAccess())
                    {
                        UpdateProgressSafely(value);
                    }
                    else
                    {
                        _ = Dispatcher.InvokeAsync(
                            () => UpdateProgressSafely(value));
                    }
                });
            progress = new ResponsivenessProgress<ModInstallationProgress>(
                progress,
                () => ResponsivenessTraceSession.Current?.Event(
                    "FIRST_PROGRESS_REPORT",
                    detail: "window=InstallProgressWindow"));
            ResponsivenessTraceSession.Current?.Event(
                "OPERATION_CALLBACK_STARTED",
                detail: "window=InstallProgressWindow");
            try
            {
                Result = await _install(progress, _cancellation.Token);
            }
            finally
            {
                ResponsivenessTraceSession.Current?.Event(
                    "OPERATION_CALLBACK_COMPLETED",
                    detail: "window=InstallProgressWindow");
            }
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            _cancellation.Cancel();
            Result = new ModInstallationResult
            {
                Status = InstallOperationStatus.Blocked,
                InstallationState =
                    PackageInstallationState.NotInstalled,
                ErrorCode = "InstallationFailed",
                ErrorMessage = exception.Message
            };
        }
        finally
        {
            _completed = true;
            _viewModel.CanCancel = false;
            Close();
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        HasRenderedContent = true;
        ResponsivenessTraceSession.Current?.Event(
            "CONTENT_RENDERED",
            detail: "window=InstallProgressWindow");
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
            detail: "window=InstallProgressWindow");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ResponsivenessTraceSession.Current?.Event(
            "WINDOW_CLOSED",
            detail: "window=InstallProgressWindow");
    }

    private void CancelButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.CanCancel = false;
        _cancellation.Cancel();
    }

    private void UpdateProgressSafely(
        ModInstallationProgress progress)
    {
        try
        {
            _viewModel.Update(progress);
        }
        catch (Exception exception)
        {
            _exceptionLogger?.Invoke(exception);
            _cancellation.Cancel();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_completed)
            return;
        e.Cancel = true;
        _viewModel.CanCancel = false;
        _cancellation.Cancel();
    }
}

public sealed class InstallProgressViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private string _statusText;
    private double _progressValue;
    private bool _isIndeterminate = true;
    private bool _canCancel = true;

    public InstallProgressViewModel(
        string modName,
        LocalizationService localization)
    {
        _localization = localization;
        ModName = modName;
        Title = localization.Get("InstallationProgressTitle");
        CancelLabel = localization.Get("Cancel");
        _statusText = localization.Get("ProgressPreparingArchive");
    }

    public string Title { get; }
    public string ModName { get; }
    public string CancelLabel { get; }
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }
    public bool CanCancel
    {
        get => _canCancel;
        set => SetProperty(ref _canCancel, value);
    }

    public void Update(ModInstallationProgress progress)
    {
        ProgressValue = NormalizeProgress(
            progress.CompletedFiles,
            progress.TotalFiles);
        IsIndeterminate = progress.TotalFiles <= 0;
        StatusText = progress.Phase switch
        {
            ModInstallationProgressPhase.PreparingArchive =>
                _localization.Get("ProgressPreparingArchive"),
            ModInstallationProgressPhase.PreservingOriginals =>
                _localization.Get("ProgressPreservingOriginals"),
            ModInstallationProgressPhase.Installing => string.Format(
                _localization.Get("ProgressInstalling"),
                progress.CompletedFiles,
                progress.TotalFiles),
            ModInstallationProgressPhase.Finalizing =>
                _localization.Get("ProgressFinalizing"),
            _ => _localization.Get("ProgressRollingBack")
        };
        if (progress.Phase == ModInstallationProgressPhase.RollingBack)
            CanCancel = false;
    }

    public static double NormalizeProgress(long completed, long total)
    {
        if (total <= 0 || completed <= 0)
            return 0;
        var value = completed * 100d / total;
        if (!double.IsFinite(value))
            return 0;
        return Math.Clamp(value, 0, 100);
    }
}
