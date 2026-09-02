using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Organizer;

namespace RipperWorks.App;

public partial class BatchOperationWindow : Window
{
    private const double PlanWidth = 980;
    private const double PlanHeight = 680;
    private const double PlanMinWidth = 780;
    private const double PlanMinHeight = 520;

    private const double ProgressWidth = 720;
    private const double ProgressHeight = 330;
    private const double ProgressMinWidth = 640;
    private const double ProgressMinHeight = 280;

    private readonly Func<IProgress<BatchOperationProgress>,
        CancellationToken, Task<BatchOperationResult>> _execute;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly BatchOperationDialogViewModel _viewModel;
    private bool _running;

    public BatchOperationWindow(
        BatchOperationDialogViewModel viewModel,
        Func<IProgress<BatchOperationProgress>, CancellationToken,
            Task<BatchOperationResult>> execute)
    {
        _viewModel = viewModel;
        _execute = execute;
        DataContext = viewModel;
        InitializeComponent();
        ApplyLocalizedColumnHeaders();
        Closing += OnClosing;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public BatchOperationResult? Result { get; private set; }
    internal bool HasRenderedProgress { get; private set; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchOperationDialogViewModel.IsProgress) && _viewModel.IsProgress)
        {
            ApplyProgressDimensions();
        }
        else if (e.PropertyName == nameof(BatchOperationDialogViewModel.IsResult) && _viewModel.IsResult)
        {
            ApplyResultDimensions();
        }
    }

    private void ApplyProgressDimensions()
    {
        MinWidth = ProgressMinWidth;
        MinHeight = ProgressMinHeight;
        Width = ProgressWidth;
        Height = ProgressHeight;
    }

    private void ApplyResultDimensions()
    {
        MinWidth = PlanMinWidth;
        MinHeight = PlanMinHeight;
        Width = PlanWidth;
        Height = PlanHeight;
    }

    private void ApplyLocalizedColumnHeaders()
    {
        var common = new object[]
        {
            _viewModel.NumberHeader,
            _viewModel.NameHeader,
            _viewModel.StateHeader
        };
        for (var index = 0; index < common.Length; index++)
        {
            InstallPlanGrid.Columns[index].Header = common[index];
            RemovalPlanGrid.Columns[index].Header = common[index];
        }
        InstallPlanGrid.Columns[3].Header = _viewModel.AddHeader;
        InstallPlanGrid.Columns[4].Header = _viewModel.ReplaceHeader;
        InstallPlanGrid.Columns[5].Header = _viewModel.ConflictsHeader;
        InstallPlanGrid.Columns[6].Header = _viewModel.BlockedHeader;
        RemovalPlanGrid.Columns[3].Header =
            _viewModel.ManagedFilesHeader;
        RemovalPlanGrid.Columns[4].Header =
            _viewModel.FilesToDeleteHeader;
        RemovalPlanGrid.Columns[5].Header =
            _viewModel.FilesToRestoreHeader;
        RemovalPlanGrid.Columns[6].Header = _viewModel.RelatedHeader;
        ResultGrid.Columns[0].Header = _viewModel.NameHeader;
        ResultGrid.Columns[1].Header = _viewModel.StateHeader;
        ResultGrid.Columns[2].Header = _viewModel.ReasonHeader;
    }

    private async void Execute_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_running || !_viewModel.CanExecute)
            return;
        ResponsivenessTraceSession.Current?.Event(
            "EXECUTE_CLICKED",
            detail: "window=BatchOperationWindow");
        _running = true;
        _viewModel.Begin();
        ApplyProgressDimensions();
        await WaitForProgressRenderAsync();
        var progress = new Progress<BatchOperationProgress>(
            value => _viewModel.Update(value));
        try
        {
            Result = await _execute(progress, _cancellation.Token);
        }
        catch (Exception exception)
        {
            Result = new BatchOperationResult
            {
                Kind = _viewModel.Kind,
                Items =
                [
                    new(
                        default,
                        _viewModel.Title,
                        BatchItemState.Failed,
                        exception.Message)
                ]
            };
        }
        _running = false;
        _viewModel.Complete(Result);
        ApplyResultDimensions();
    }

    private async Task WaitForProgressRenderAsync()
    {
        var rendered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) => rendered.TrySetResult();
        CompositionTarget.Rendering += handler;
        try
        {
            await rendered.Task;
        }
        finally
        {
            CompositionTarget.Rendering -= handler;
        }
        HasRenderedProgress = true;
        ResponsivenessTraceSession.Current?.Event(
            "CONTENT_RENDERED",
            detail: "window=BatchOperationWindow;surface=progress");
    }

    private void CancelAfterCurrent_OnClick(
        object sender,
        RoutedEventArgs e) =>
        _cancellation.Cancel();

    private void Cancel_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (!_running)
            return;
        e.Cancel = true;
        _cancellation.Cancel();
    }
}
