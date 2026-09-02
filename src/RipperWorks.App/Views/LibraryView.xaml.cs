using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;

namespace RipperWorks.App.Views;

public partial class LibraryView : UserControl
{
    private readonly Dictionary<ContextMenu, BatchMenuItems>
        _batchMenuItems = [];
    private readonly HashSet<PackageRowViewModel> _observedRows = [];
    private INotifyCollectionChanged? _observedCollection;
    private ICollectionView? _observedView;
    private Window? _hostWindow;
    private bool _diagnosticsAttached;
    private bool _settledSelectionCheckPending;
    private RipperWorks.Core.LibraryModId? _firstClickLibraryModId;
    private bool _repairPending;
    private bool _isRepairing;

    private LibraryViewModel? ViewModel =>
        DataContext as LibraryViewModel;

    public int DiagnosticLoadingRowCount { get; private set; }

    public LibraryView()
    {
        InitializeComponent();
        Loaded += LibraryView_OnLoaded;
        Unloaded += LibraryView_OnUnloaded;
    }

    private void LibraryView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsAttached)
            return;
        _diagnosticsAttached = true;
        LibrarySelectionDiagnostics.RegisterGrid(LibraryGrid);
        DependencyPropertyDescriptor
            .FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))
            .AddValueChanged(LibraryGrid, LibraryGrid_OnItemsSourceChanged);
        LibraryGrid.SelectionChanged += LibraryGrid_OnSelectionChanged;
        LibraryGrid.PreviewMouseDown += LibraryGrid_OnPreviewMouseDown;
        LibraryGrid.MouseDown += LibraryGrid_OnMouseDown;
        LibraryGrid.LoadingRow += LibraryGrid_OnLoadingRow;
        AttachCollectionDiagnostics();
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.Activated += HostWindow_OnActivated;
            _hostWindow.Deactivated += HostWindow_OnDeactivated;
        }
        LibrarySelectionDiagnostics.Record("LibraryLoaded", detail: "LibraryView loaded", verifyInvariants: true);
    }

    private void LibraryView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_diagnosticsAttached)
            return;
        _diagnosticsAttached = false;
        DependencyPropertyDescriptor
            .FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))
            .RemoveValueChanged(LibraryGrid, LibraryGrid_OnItemsSourceChanged);
        LibraryGrid.SelectionChanged -= LibraryGrid_OnSelectionChanged;
        LibraryGrid.PreviewMouseDown -= LibraryGrid_OnPreviewMouseDown;
        LibraryGrid.MouseDown -= LibraryGrid_OnMouseDown;
        LibraryGrid.LoadingRow -= LibraryGrid_OnLoadingRow;
        DetachCollectionDiagnostics();
        if (_hostWindow is not null)
        {
            _hostWindow.Activated -= HostWindow_OnActivated;
            _hostWindow.Deactivated -= HostWindow_OnDeactivated;
            _hostWindow = null;
        }
        LibrarySelectionDiagnostics.UnregisterGrid(LibraryGrid);
    }

    private void LibraryGrid_OnItemsSourceChanged(object? sender, EventArgs e)
    {
        DetachCollectionDiagnostics();
        AttachCollectionDiagnostics();
        LibrarySelectionDiagnostics.Record("ItemsSourceChanged", verifyInvariants: true);
    }

    private void AttachCollectionDiagnostics()
    {
        _observedView = LibraryGrid.ItemsSource as ICollectionView ??
            (LibraryGrid.ItemsSource is null
                ? null
                : CollectionViewSource.GetDefaultView(
                    LibraryGrid.ItemsSource));
        _observedCollection =
            _observedView?.SourceCollection as INotifyCollectionChanged ??
            LibraryGrid.ItemsSource as INotifyCollectionChanged;
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged +=
                ObservedCollection_OnCollectionChanged;
        }
        if (_observedView is not null)
            _observedView.CurrentChanged += ObservedView_OnCurrentChanged;
        RefreshObservedRows();
    }

    private void DetachCollectionDiagnostics()
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -=
                ObservedCollection_OnCollectionChanged;
            _observedCollection = null;
        }
        if (_observedView is not null)
        {
            _observedView.CurrentChanged -= ObservedView_OnCurrentChanged;
            _observedView = null;
        }
        foreach (var row in _observedRows)
            row.PropertyChanged -= ObservedRow_OnPropertyChanged;
        _observedRows.Clear();
    }

    private void RefreshObservedRows()
    {
        var currentRows = LibraryGrid.ItemsSource?
            .OfType<PackageRowViewModel>()
            .ToHashSet() ?? [];
        foreach (var stale in _observedRows.Except(currentRows).ToArray())
        {
            stale.PropertyChanged -= ObservedRow_OnPropertyChanged;
            _observedRows.Remove(stale);
        }
        foreach (var row in currentRows)
        {
            if (_observedRows.Add(row))
                row.PropertyChanged += ObservedRow_OnPropertyChanged;
        }
    }

    private void ObservedCollection_OnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        RefreshObservedRows();
        LibrarySelectionDiagnostics.Record(
            "CollectionChanged",
            detail:
                $"action={e.Action}; oldIndex={e.OldStartingIndex}; " +
                $"newIndex={e.NewStartingIndex}; " +
                $"oldCount={e.OldItems?.Count ?? 0}; " +
                $"newCount={e.NewItems?.Count ?? 0}",
            verifyInvariants: true);
    }

    private void ObservedView_OnCurrentChanged(
        object? sender,
        EventArgs e) =>
        LibrarySelectionDiagnostics.Record(
            "CurrentChanged",
            verifyInvariants: true);

    private void ObservedRow_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PackageRowViewModel.Group))
            return;
        var row = sender as PackageRowViewModel;
        LibrarySelectionDiagnostics.Record(
            "GroupChanged",
            row?.LibraryModId.Value,
            row is null
                ? null
                : $"row={ReferenceId(row)}; group={row.Group.Id}",
            verifyInvariants: true);
    }

    private void LibraryGrid_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.ObserveSelectedPackage(
                LibraryGrid.SelectedItem as PackageRowViewModel);
        }
        LibrarySelectionDiagnostics.RecordLightweight(
            "SelectionChanged",
            detail:
                $"added={e.AddedItems.Count}; " +
                $"removed={e.RemovedItems.Count}");
        if (_settledSelectionCheckPending)
            return;
        _settledSelectionCheckPending = true;
        _ = LibraryGrid.Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                _settledSelectionCheckPending = false;
                ScheduleLibrarySelectionInvariantRepair("SelectionSettled");
                LibrarySelectionDiagnostics.Record(
                    "SelectionSettled",
                    verifyInvariants: true);
            }));
    }

    private void LibraryGrid_OnPreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(
            e.OriginalSource as DependencyObject);
        if (e.ChangedButton == MouseButton.Left &&
            e.ClickCount == 1)
        {
            _firstClickLibraryModId =
                (row?.DataContext as PackageRowViewModel)
                ?.LibraryModId;
        }
        LibrarySelectionDiagnostics.RecordLightweight(
            "PreviewMouseDown",
            (row?.DataContext as PackageRowViewModel)
                ?.LibraryModId.Value,
            $"button={e.ChangedButton}; clicks={e.ClickCount}");
    }

    private void LibraryGrid_OnMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(
            e.OriginalSource as DependencyObject);
        LibrarySelectionDiagnostics.RecordLightweight(
            "MouseDown",
            (row?.DataContext as PackageRowViewModel)
                ?.LibraryModId.Value,
            $"button={e.ChangedButton}; clicks={e.ClickCount}");
    }

    private void LibraryGrid_OnLoadingRow(
        object? sender,
        DataGridRowEventArgs e)
    {
        DiagnosticLoadingRowCount++;
    }

    private void HostWindow_OnActivated(
        object? sender,
        EventArgs e)
    {
        LibrarySelectionDiagnostics.Record(
            "WindowActivated",
            verifyInvariants: true);
        ScheduleLibrarySelectionInvariantRepair("WindowActivated");
    }

    private static void HostWindow_OnDeactivated(
        object? sender,
        EventArgs e) =>
        LibrarySelectionDiagnostics.Record(
            "WindowDeactivated",
            verifyInvariants: true);

    private void MoreActionsButton_OnClick(
        object sender,
        System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: not null } button)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }

    private void RowSelectionCheckBox_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel ||
            sender is not CheckBox
            {
                DataContext: PackageRowViewModel row
            })
        {
            return;
        }
        viewModel.ToggleCheckbox(row);
        e.Handled = true;
    }

    private void SelectAllVisible_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        ViewModel?.ToggleAllVisibleSelection();
        e.Handled = true;
    }

    private void GroupSelectionCheckBox_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is CheckBox
            {
                DataContext: CollectionViewGroup
                {
                    Name: LibraryGroupViewModel group
                }
            })
        {
            group.ToggleBatchSelection();
        }
        e.Handled = true;
    }

    private void LibraryGrid_OnMouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        var row = FindAncestor<DataGridRow>(
            e.OriginalSource as DependencyObject);
        if (row?.DataContext is not PackageRowViewModel item)
            return;
        if (_firstClickLibraryModId != item.LibraryModId)
        {
            LibrarySelectionDiagnostics.Record(
                "DoubleClickTargetChanged",
                item.LibraryModId.Value,
                "first=" +
                (_firstClickLibraryModId?.Value ?? "<null>"));
            _firstClickLibraryModId = null;
            e.Handled = true;
            return;
        }
        _firstClickLibraryModId = null;
        LibrarySelectionDiagnostics.Record(
            "DoubleClick",
            item.LibraryModId.Value,
            verifyInvariants: true);
        e.Handled = true;
        OpenModDetailsFromRow(item);
    }

    internal void OpenModDetailsFromRow(PackageRowViewModel item)
    {
        if (ViewModel is not { } viewModel ||
            !viewModel.Packages.Contains(item))
        {
            return;
        }
        var libraryModId = item.LibraryModId;
        _ = LibraryGrid.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
                OpenModDetailsAfterInput(
                    viewModel,
                    libraryModId)));
    }

    private void OpenModDetailsAfterInput(
        LibraryViewModel viewModel,
        RipperWorks.Core.LibraryModId libraryModId)
    {
        LibrarySelectionDiagnostics.Record(
            "CardOpening",
            libraryModId.Value,
            "queued from LibraryGrid input",
            verifyInvariants: true);
        TraceSelectionState("before-open");
        var row = viewModel.Packages.FirstOrDefault(item =>
            item.LibraryModId == libraryModId);
        if (row is null ||
            !LibraryGrid.ItemsSource.Cast<object>()
                .Any(item => ReferenceEquals(item, row)))
        {
            return;
        }
        viewModel.OpenModDetailsNow(libraryModId);
        TraceSelectionState("after-close");
        TraceSelectionState("before-next-input");
        ScheduleLibrarySelectionInvariantRepair("CardClosed");
    }

    internal static bool IsSelectionContradictory(
        object? selectedItem,
        int selectedIndex,
        int selectedItemsCount) =>
        selectedItem is null &&
        selectedIndex == -1 &&
        selectedItemsCount > 0;

    internal bool ScheduleLibrarySelectionInvariantRepair(string reason)
    {
        if (_repairPending || LibraryGrid is null)
            return false;
        _repairPending = true;
        _ = LibraryGrid.Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => ExecuteLibrarySelectionInvariantRepair(reason)));
        return true;
    }

    internal bool ExecuteLibrarySelectionInvariantRepair(string reason)
    {
        if (_isRepairing)
            return false;
        _repairPending = false;
        _isRepairing = true;
        try
        {
            if (LibraryGrid is null)
                return false;

            var selectedItem = LibraryGrid.SelectedItem;
            var selectedIndex = LibraryGrid.SelectedIndex;
            var selectedItemsCount = LibraryGrid.SelectedItems.Count;

            if (!IsSelectionContradictory(
                    selectedItem,
                    selectedIndex,
                    selectedItemsCount))
            {
                return false;
            }

            var staleCount = selectedItemsCount;
            LibraryGrid.UnselectAll();

            var repairedItem = LibraryGrid.SelectedItem;
            var repairedIndex = LibraryGrid.SelectedIndex;
            var repairedCount = LibraryGrid.SelectedItems.Count;

            var repaired = repairedItem is null &&
                           repairedIndex == -1 &&
                           repairedCount == 0;

            LibrarySelectionDiagnostics.Record(
                "SelectionInvariantRepaired",
                detail:
                    $"reason={reason}; staleCount={staleCount}; " +
                    $"repaired={repaired}",
                verifyInvariants: true);

            return repaired;
        }
        finally
        {
            _isRepairing = false;
        }
    }

    [Conditional("DEBUG")]
    private void TraceSelectionState(string stage)
    {
        var itemsSource = LibraryGrid.ItemsSource;
        var view = itemsSource as ICollectionView ??
            CollectionViewSource.GetDefaultView(itemsSource);
        var sourceCollection = view?.SourceCollection;
        var items = itemsSource?.Cast<object?>().ToArray() ?? [];
        var selectedItems = LibraryGrid.SelectedItems
            .Cast<object?>()
            .ToArray();
        var selected = LibraryGrid.SelectedItem;
        var current = view?.CurrentItem;
        Debug.WriteLine(
            $"{DateTimeOffset.Now:O} LibraryGridSelection " +
            $"stage={stage}; " +
            $"itemsSourceRef={ReferenceId(itemsSource)}; " +
            $"sourceCollectionRef={ReferenceId(sourceCollection)}; " +
            $"count={items.Length}; " +
            $"nullItems={items.Count(item => item is null)}; " +
            $"selected={ReferenceId(selected)}; " +
            $"selectedIndex={LibraryGrid.SelectedIndex}; " +
            $"current={ReferenceId(current)}; " +
            $"selectedCount={selectedItems.Length}; " +
            $"nullSelected={selectedItems.Count(item => item is null)}; " +
            $"selectedInItems={selected is null || items.Any(item => ReferenceEquals(item, selected))}; " +
            $"currentInItems={current is null || items.Any(item => ReferenceEquals(item, current))}; " +
            $"libraryModId={(selected as PackageRowViewModel)?.LibraryModId.Value ?? "<null>"}; " +
            $"selectedType={selected?.GetType().FullName ?? "<null>"}; " +
            $"beforeFirst={view?.IsCurrentBeforeFirst}; " +
            $"afterLast={view?.IsCurrentAfterLast}");
    }

    private static string ReferenceId(object? value) =>
        value is null
            ? "<null>"
            : $"{value.GetType().Name}@" +
              RuntimeHelpers.GetHashCode(value)
                  .ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void LibraryGrid_OnContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(
            e.OriginalSource as DependencyObject);
        if (row?.ContextMenu is not { } menu)
            return;
        UpdateBatchMenu(menu, row);
    }

    internal void UpdateBatchMenu(
        ContextMenu menu,
        FrameworkElement placementTarget)
    {
        menu.PlacementTarget = placementTarget;
        if (!_batchMenuItems.TryGetValue(menu, out var batch))
        {
            batch = CaptureBatchMenuItems(menu);
            if (batch is null)
                return;
            _batchMenuItems.Add(menu, batch);
        }

        var hasSelection =
            placementTarget.Tag is LibraryViewModel rowLibrary
                ? rowLibrary.HasBatchSelection
                : ViewModel?.HasBatchSelection == true;
        if (!hasSelection)
        {
            foreach (var item in batch.Items)
                menu.Items.Remove(item);
            return;
        }

        var insertAt = Math.Min(batch.InsertionIndex, menu.Items.Count);
        foreach (var item in batch.Items)
        {
            if (menu.Items.Contains(item))
                continue;
            menu.Items.Insert(insertAt++, item);
        }
    }

    private static BatchMenuItems? CaptureBatchMenuItems(
        ContextMenu menu)
    {
        var tagged = menu.Items
            .OfType<FrameworkElement>()
            .Where(item => item.Tag is string)
            .ToDictionary(
                item => (string)item.Tag,
                StringComparer.Ordinal);
        string[] tags =
        [
            "BatchCommandsSeparator",
            "InstallSelectedMenuItem",
            "RemoveSelectedMenuItem",
            "DeleteSelectedCompletelyMenuItem",
            "ClearSelectionMenuItem"
        ];
        if (tags.Any(tag => !tagged.ContainsKey(tag)))
            return null;
        var items = tags.Select(tag => tagged[tag]).ToArray();
        return new BatchMenuItems(
            menu.Items.IndexOf(items[0]),
            items);
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T value)
                return value;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private sealed record BatchMenuItems(
        int InsertionIndex,
        IReadOnlyList<FrameworkElement> Items);

}
