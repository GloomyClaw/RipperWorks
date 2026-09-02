using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using RipperWorks.App.ViewModels;
using RipperWorks.Infrastructure;

namespace RipperWorks.App.Services;

public static class LibrarySelectionDiagnostics
{
    private const int Capacity = 750;
    private static readonly object Gate = new();
    private static readonly Queue<string> Events = new(Capacity);
    private static WeakReference<DataGrid>? _grid;
    private static long _sequence;
    private static int _dumping;

    public static string? LastDumpPath { get; private set; }

    public static void RegisterGrid(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        lock (Gate)
            _grid = new WeakReference<DataGrid>(grid);
        Record("ItemsSourceChanged", detail: "grid registered",
            verifyInvariants: true);
    }

    public static void UnregisterGrid(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        lock (Gate)
        {
            if (_grid is not null &&
                _grid.TryGetTarget(out var current) &&
                ReferenceEquals(current, grid))
            {
                _grid = null;
            }
        }
    }

    internal const int BufferCapacity = Capacity;

    internal static int SnapshotCount => Volatile.Read(ref _snapshotCount);
    private static int _snapshotCount;

    internal static int BufferedEventCount
    {
        get
        {
            lock (Gate)
                return Events.Count;
        }
    }

    public static void RecordLightweight(
        string eventName,
        string? cardLibraryModId = null,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var sequence = Interlocked.Increment(ref _sequence);
        var timestamp = DateTimeOffset.Now;
        DataGrid? grid;
        lock (Gate)
        {
            grid = _grid is not null && _grid.TryGetTarget(out var current)
                ? current
                : null;
        }
        var threadId = Environment.CurrentManagedThreadId;
        var dispatcherAccess = grid?.Dispatcher.CheckAccess() ?? false;
        var line = $"{timestamp:O} seq={sequence}; event={eventName}; " +
                   $"thread={threadId}; dispatcherAccess={dispatcherAccess}; " +
                   $"cardLibraryModId={cardLibraryModId ?? "<null>"}; " +
                   $"detail={Sanitize(detail)}";

        lock (Gate)
        {
            Append(line);
        }
    }

    public static void Record(
        string eventName,
        string? cardLibraryModId = null,
        string? detail = null,
        bool verifyInvariants = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var sequence = Interlocked.Increment(ref _sequence);
        var timestamp = DateTimeOffset.Now;
        var snapshot = CaptureSnapshot();
        var violations = verifyInvariants
            ? FindInvariantViolations(snapshot)
            : [];
        var line = Format(
            timestamp,
            sequence,
            eventName,
            cardLibraryModId,
            detail,
            snapshot);

        lock (Gate)
        {
            Append(line);
            if (violations.Count > 0)
            {
                Append(Format(
                    timestamp,
                    sequence,
                    "INVARIANT VIOLATION",
                    cardLibraryModId,
                    string.Join(" | ", violations),
                    snapshot));
            }
        }

        if (violations.Count > 0)
        {
            _ = Dump(
                "INVARIANT VIOLATION: " +
                string.Join(" | ", violations));
        }
    }

    public static string? Dump(
        string reason,
        Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _dumping, 1) != 0)
            return LastDumpPath;
        try
        {
            string[] events;
            lock (Gate)
                events = Events.ToArray();

            var paths = new RipperWorksPaths();
            Directory.CreateDirectory(paths.LogsDirectory);
            var timestamp = DateTimeOffset.Now;
            var path = Path.Combine(
                paths.LogsDirectory,
                $"library-selection-trace-" +
                $"{timestamp:yyyyMMdd-HHmmss-fff}.log");
            var report = new StringBuilder()
                .AppendLine($"Timestamp: {timestamp:O}")
                .AppendLine($"Reason: {reason}")
                .AppendLine($"EventCount: {events.Length}")
                .AppendLine();
            foreach (var traceEvent in events)
                report.AppendLine(traceEvent);
            if (exception is not null)
            {
                report
                    .AppendLine()
                    .AppendLine("Exception:")
                    .AppendLine(exception.ToString());
            }
            File.WriteAllText(
                path,
                report.ToString(),
                new UTF8Encoding(false));
            LastDumpPath = path;
            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            Volatile.Write(ref _dumping, 0);
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            Events.Clear();
            _sequence = 0;
            _snapshotCount = 0;
            LastDumpPath = null;
        }
    }

    private static SelectionSnapshot CaptureSnapshot()
    {
        Interlocked.Increment(ref _snapshotCount);
        DataGrid? grid;
        lock (Gate)
        {
            grid = _grid is not null &&
                   _grid.TryGetTarget(out var current)
                ? current
                : null;
        }
        if (grid is null)
            return SelectionSnapshot.NoGrid;

        var dispatcherAccess = grid.Dispatcher.CheckAccess();
        if (!dispatcherAccess)
        {
            return SelectionSnapshot.NoGrid with
            {
                DispatcherAccess = false,
                ItemsSourceId = ReferenceId(grid)
            };
        }

        try
        {
            var itemsSource = grid.ItemsSource;
            var view = itemsSource as ICollectionView ??
                (itemsSource is null
                    ? null
                    : CollectionViewSource.GetDefaultView(itemsSource));
            var sourceCollection = view?.SourceCollection;
            var items = itemsSource?.Cast<object?>().ToArray() ?? [];
            var selectedItems = grid.SelectedItems
                .Cast<object?>()
                .ToArray();
            var selectedItem = grid.SelectedItem;
            var currentItem = view?.CurrentItem;
            var invalidSelected = selectedItems.Count(item =>
                item is null ||
                !ContainsReference(items, item));
            var duplicateIds = items
                .OfType<PackageRowViewModel>()
                .GroupBy(row => row.LibraryModId)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key.Value)
                .ToArray();
            return new SelectionSnapshot(
                true,
                dispatcherAccess,
                ReferenceId(itemsSource),
                ReferenceId(sourceCollection),
                items.Length,
                items.Count(item => item is null),
                ReferenceId(selectedItem),
                GetLibraryModId(selectedItem),
                grid.SelectedIndex,
                ReferenceId(currentItem),
                GetLibraryModId(currentItem),
                selectedItems.Length,
                invalidSelected,
                view?.CurrentPosition ?? -1,
                selectedItem is null ||
                ContainsReference(items, selectedItem),
                grid.SelectionMode ==
                DataGridSelectionMode.Single,
                items.Any(item => item is CollectionViewGroup),
                duplicateIds,
                selectedItems.Any(item => item is null),
                items);
        }
        catch (Exception exception)
        {
            return SelectionSnapshot.NoGrid with
            {
                HasGrid = true,
                DispatcherAccess = dispatcherAccess,
                CaptureError = exception.GetType().Name + ": " +
                               exception.Message
            };
        }
    }

    private static IReadOnlyList<string> FindInvariantViolations(
        SelectionSnapshot snapshot)
    {
        if (!snapshot.HasGrid || !snapshot.DispatcherAccess)
            return [];
        var violations = new List<string>();
        if (snapshot.NullItemCount > 0)
            violations.Add("ItemsSource contains null");
        if (!snapshot.SelectedItemInSource)
            violations.Add("SelectedItem is not in current ItemsSource");
        if (snapshot.SelectedItemsContainNull)
            violations.Add("SelectedItems contains null");
        if (snapshot.InvalidSelectedItemsCount > 0)
            violations.Add("SelectedItems contains stale item");
        if (snapshot.IsSingleSelectionMode &&
            snapshot.SelectedItemsCount > 1)
        {
            violations.Add(
                "SelectionMode=Single has multiple selected items");
        }
        if (snapshot.DuplicateLibraryModIds.Length > 0)
        {
            violations.Add(
                "duplicate LibraryModId: " +
                string.Join(",", snapshot.DuplicateLibraryModIds));
        }
        if (snapshot.GroupRowInItemsSource)
            violations.Add("CollectionViewGroup is used as a DataGrid row");
        if (!string.IsNullOrWhiteSpace(snapshot.CaptureError))
            violations.Add("snapshot failed: " + snapshot.CaptureError);
        return violations;
    }

    private static string Format(
        DateTimeOffset timestamp,
        long sequence,
        string eventName,
        string? cardLibraryModId,
        string? detail,
        SelectionSnapshot snapshot) =>
        $"{timestamp:O} seq={sequence}; event={eventName}; " +
        $"thread={Environment.CurrentManagedThreadId}; " +
        $"dispatcherAccess={snapshot.DispatcherAccess}; " +
        $"itemsSource={snapshot.ItemsSourceId}; " +
        $"sourceCollection={snapshot.SourceCollectionId}; " +
        $"itemCount={snapshot.ItemCount}; " +
        $"nullItems={snapshot.NullItemCount}; " +
        $"selectedItem={snapshot.SelectedItemId}; " +
        $"selectedLibraryModId={snapshot.SelectedLibraryModId}; " +
        $"selectedIndex={snapshot.SelectedIndex}; " +
        $"currentItem={snapshot.CurrentItemId}; " +
        $"currentLibraryModId={snapshot.CurrentLibraryModId}; " +
        $"selectedItemsCount={snapshot.SelectedItemsCount}; " +
        $"invalidSelectedItems={snapshot.InvalidSelectedItemsCount}; " +
        $"selectionMode={(snapshot.IsSingleSelectionMode ? "Single" : "Extended")}; " +
        $"currentPosition={snapshot.CurrentPosition}; " +
        $"cardLibraryModId={cardLibraryModId ?? "<null>"}; " +
        $"detail={Sanitize(detail)}; " +
        $"captureError={Sanitize(snapshot.CaptureError)}";

    private static void Append(string value)
    {
        while (Events.Count >= Capacity)
            Events.Dequeue();
        Events.Enqueue(value);
    }

    private static bool ContainsReference(
        IReadOnlyList<object?> items,
        object value) =>
        items.Any(item => ReferenceEquals(item, value));

    private static string GetLibraryModId(object? value) =>
        value is PackageRowViewModel row
            ? row.LibraryModId.Value
            : "<null>";

    private static string ReferenceId(object? value) =>
        value is null
            ? "<null>"
            : $"{value.GetType().Name}@" +
              RuntimeHelpers.GetHashCode(value)
                  .ToString(CultureInfo.InvariantCulture);

    private static string Sanitize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "<null>"
            : value
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed record SelectionSnapshot(
        bool HasGrid,
        bool DispatcherAccess,
        string ItemsSourceId,
        string SourceCollectionId,
        int ItemCount,
        int NullItemCount,
        string SelectedItemId,
        string SelectedLibraryModId,
        int SelectedIndex,
        string CurrentItemId,
        string CurrentLibraryModId,
        int SelectedItemsCount,
        int InvalidSelectedItemsCount,
        int CurrentPosition,
        bool SelectedItemInSource,
        bool IsSingleSelectionMode,
        bool GroupRowInItemsSource,
        string[] DuplicateLibraryModIds,
        bool SelectedItemsContainNull,
        object?[] Items)
    {
        public static SelectionSnapshot NoGrid { get; } = new(
            false,
            false,
            "<null>",
            "<null>",
            0,
            0,
            "<null>",
            "<null>",
            -1,
            "<null>",
            "<null>",
            0,
            0,
            -1,
            true,
            false,
            false,
            [],
            false,
            []);

        public string? CaptureError { get; init; }
    }
}
