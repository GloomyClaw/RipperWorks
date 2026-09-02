using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using RipperWorks.Core;

namespace RipperWorks.App.Services;

internal sealed class ResponsivenessTraceSession : IAsyncDisposable
{
    private const int EventCapacity = 2048;
    private static readonly AsyncLocal<ResponsivenessTraceSession?> CurrentSlot = new();
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private static int _fileInitialized;

    private readonly object _gate = new();
    private readonly List<string> _events = new(EventCapacity);
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer? _heartbeat;
    private readonly long _workflowStarted = Stopwatch.GetTimestamp();
    private readonly bool _writeToFile;
    private readonly ResponsivenessTraceSession? _previous;
    private OperationPerformanceSession? _performance;
    private long _lastHeartbeat = Stopwatch.GetTimestamp();
    private string _currentStage = "IDLE";
    private string _heartbeatStage = "IDLE";
    private PackageId? _heartbeatPackageId;
    private bool _disposed;

    private ResponsivenessTraceSession(
        Dispatcher dispatcher,
        bool enabled,
        bool writeToFile,
        TimeSpan heartbeatInterval,
        TimeSpan stallThreshold)
    {
        _dispatcher = dispatcher;
        IsEnabled = enabled;
        _writeToFile = writeToFile;
        StallThreshold = stallThreshold;
        if (!enabled)
            return;

        _previous = CurrentSlot.Value;
        CurrentSlot.Value = this;
        _heartbeat = new DispatcherTimer(
            heartbeatInterval,
            DispatcherPriority.Background,
            OnHeartbeat,
            dispatcher);
        _heartbeat.Start();
        Event("TRACE_STARTED", detail: $"path={LogPath}");
    }

    internal const string EnvironmentVariable =
        "RIPPERWORKS_RESPONSIVENESS_DIAGNOSTICS";

    internal static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "RipperWorks",
        "Diagnostics",
        "responsiveness-current.log");

    internal static ResponsivenessTraceSession? Current =>
        CurrentSlot.Value is { IsEnabled: true } current
            ? current
            : null;

    internal bool IsEnabled { get; }
    internal TimeSpan StallThreshold { get; }

    internal static ResponsivenessTraceSession Start(
        Dispatcher dispatcher) =>
        new(
            dispatcher,
            string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentVariable),
                "1",
                StringComparison.Ordinal),
            writeToFile: true,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250));

    internal static ResponsivenessTraceSession StartForTest(
        Dispatcher dispatcher,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? stallThreshold = null) =>
        new(
            dispatcher,
            enabled: true,
            writeToFile: false,
            heartbeatInterval ?? TimeSpan.FromMilliseconds(25),
            stallThreshold ?? TimeSpan.FromMilliseconds(250));

    internal IDisposable Stage(
        string stage,
        PackageId? packageId = null)
    {
        if (!IsEnabled)
            return EmptyScope.Instance;
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (_performance is null && packageId is not null &&
            stage is "INSTALL_REQUEST" or "REMOVE_REQUEST")
        {
            var operation = stage == "INSTALL_REQUEST"
                ? "install"
                : "remove";
            _performance = OperationPerformanceDiagnostics.Start(
                operation,
                packageId.Value,
                (eventName, detail) => Event(
                    eventName,
                    packageId,
                    detail));
        }
        string previous;
        lock (_gate)
        {
            previous = _currentStage;
            _currentStage = stage;
            _heartbeatStage = stage;
            _heartbeatPackageId = packageId;
        }
        var started = Stopwatch.GetTimestamp();
        Record(
            "STAGE",
            stage,
            "BEGIN",
            packageId,
            started,
            detail: null);
        return new StageScope(this, stage, previous, packageId, started);
    }

    internal T RunStage<T>(
        string stage,
        PackageId? packageId,
        Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var scope = Stage(stage, packageId);
        return operation();
    }

    internal void RunStage(
        string stage,
        PackageId? packageId,
        Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var scope = Stage(stage, packageId);
        operation();
    }

    internal async Task<T> RunStageAsync<T>(
        string stage,
        PackageId? packageId,
        Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var scope = Stage(stage, packageId);
        return await operation();
    }

    internal async Task RunStageAsync(
        string stage,
        PackageId? packageId,
        Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var scope = Stage(stage, packageId);
        await operation();
    }

    internal void Event(
        string eventName,
        PackageId? packageId = null,
        string? detail = null)
    {
        if (!IsEnabled)
            return;
        Record(
            eventName,
            CurrentStage,
            boundary: null,
            packageId,
            Stopwatch.GetTimestamp(),
            detail);
    }

    internal IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!IsEnabled)
            return;

        _heartbeat!.Stop();
        _heartbeat.Tick -= OnHeartbeat;
        _performance?.Dispose();
        _performance = null;
        Event("TRACE_COMPLETED", detail: $"path={LogPath}");
        if (ReferenceEquals(CurrentSlot.Value, this))
            CurrentSlot.Value = _previous;
        if (_writeToFile)
            await PersistAsync().ConfigureAwait(false);
    }

    private string CurrentStage
    {
        get
        {
            lock (_gate)
                return _currentStage;
        }
    }

    private void OnHeartbeat(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var gap = Stopwatch.GetElapsedTime(_lastHeartbeat, now);
        _lastHeartbeat = now;
        string stalledStage;
        PackageId? stalledPackageId;
        lock (_gate)
        {
            stalledStage = _heartbeatStage;
            stalledPackageId = _heartbeatPackageId;
            _heartbeatStage = _currentStage;
        }
        if (gap <= StallThreshold)
            return;
        Record(
            "UI_STALL",
            stalledStage,
            boundary: null,
            stalledPackageId,
            now,
            $"gap_ms={gap.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} " +
            $"current_stage={stalledStage}");
    }

    private void EndStage(
        string stage,
        string previous,
        PackageId? packageId,
        long started)
    {
        Record(
            "STAGE",
            stage,
            "END",
            packageId,
            started,
            detail: null);
        lock (_gate)
        {
            if (string.Equals(_currentStage, stage, StringComparison.Ordinal))
                _currentStage = previous;
        }
    }

    private void Record(
        string eventName,
        string stage,
        string? boundary,
        PackageId? packageId,
        long stageStarted,
        string? detail)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            var workflowElapsed = Stopwatch.GetElapsedTime(
                _workflowStarted,
                now).TotalMilliseconds;
            var stageElapsed = Stopwatch.GetElapsedTime(
                stageStarted,
                now).TotalMilliseconds;
            var line =
                $"{DateTimeOffset.UtcNow:O} event={eventName}" +
                $" stage={stage}" +
                (boundary is null ? string.Empty : $" boundary={boundary}") +
                $" elapsed_ms={workflowElapsed.ToString("F1", CultureInfo.InvariantCulture)}" +
                $" stage_elapsed_ms={stageElapsed.ToString("F1", CultureInfo.InvariantCulture)}" +
                $" thread={Environment.CurrentManagedThreadId}" +
                $" dispatcher_access={_dispatcher.CheckAccess()}" +
                $" package_id={packageId?.Value ?? "<none>"}" +
                (string.IsNullOrWhiteSpace(detail)
                    ? string.Empty
                    : $" {Sanitize(detail)}");
            lock (_gate)
            {
                if (_events.Count >= EventCapacity)
                    _events.RemoveAt(0);
                _events.Add(line);
            }
        }
        catch
        {
            // Diagnostic observation must never affect product behavior.
        }
    }

    private async Task PersistAsync()
    {
        var snapshot = Snapshot();
        try
        {
            await FileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    var append = Interlocked.Exchange(
                        ref _fileInitialized,
                        1) != 0;
                    using var stream = new FileStream(
                        LogPath,
                        append ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read);
                    using var writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(false));
                    foreach (var line in snapshot)
                        writer.WriteLine(line);
                }).ConfigureAwait(false);
            }
            finally
            {
                FileGate.Release();
            }
        }
        catch
        {
            // Best-effort diagnostic output is non-authoritative.
        }
    }

    private static string Sanitize(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed class StageScope(
        ResponsivenessTraceSession owner,
        string stage,
        string previous,
        PackageId? packageId,
        long started) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndStage(stage, previous, packageId, started);
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}

internal sealed class ResponsivenessProgress<T>(
    IProgress<T> inner,
    Action firstReport) : IProgress<T>
{
    private int _reported;

    public void Report(T value)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
            firstReport();
        inner.Report(value);
    }
}
