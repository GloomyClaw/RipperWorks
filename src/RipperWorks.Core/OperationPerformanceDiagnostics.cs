using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RipperWorks.Core;

public enum OperationPerformanceContext
{
    InstallPreflight,
    Install,
    RemovePreflight,
    Remove
}

/// <summary>
/// Optional ambient timing sink for one install/remove request. It is a no-op
/// unless the App-owned diagnostics session explicitly starts it.
/// </summary>
public static class OperationPerformanceDiagnostics
{
    private static readonly AsyncLocal<AmbientState?> StateSlot = new();

    public static bool IsEnabled => StateSlot.Value?.Session is not null;
    public static OperationPerformanceContext? CurrentContext =>
        StateSlot.Value?.Context;

    public static OperationPerformanceSession Start(
        string operation,
        PackageId packageId,
        Action<string, string?> observer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(observer);
        var state = StateSlot.Value;
        if (state is null)
        {
            state = new AmbientState();
            StateSlot.Value = state;
        }
        var session = new OperationPerformanceSession(
            operation,
            packageId,
            observer,
            state.Session,
            state.Context);
        state.Session = session;
        state.Context = null;
        return session;
    }

    public static IDisposable UseContext(
        OperationPerformanceContext context)
    {
        var state = StateSlot.Value;
        if (state?.Session is null)
            return EmptyScope.Instance;
        var previous = state.Context;
        state.Context = context;
        return new ContextScope(state, context, previous);
    }

    public static IDisposable MeasureMetric(
        string metric,
        string? repeatedOperation = null)
    {
        var state = StateSlot.Value;
        var current = state?.Session;
        var context = state?.Context;
        if (current is null || context is null)
            return EmptyScope.Instance;
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        return current.Measure(
            BuildStageName(context.Value, metric),
            repeatedOperation);
    }

    public static void AddCounter(string name, long value = 1)
    {
        if (value == 0)
            return;
        StateSlot.Value?.Session?.AddCounter(name, value);
    }

    public static void ObserveMaximum(string name, long value) =>
        StateSlot.Value?.Session?.ObserveMaximum(name, value);

    public static async Task<T> MeasureDatabaseAsync<T>(
        Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!IsEnabled)
            return await operation().ConfigureAwait(false);
        AddCounter("database_operation_count");
        using var timing = MeasureMetric("DATABASE", "database_operation");
        return await operation().ConfigureAwait(false);
    }

    public static async Task MeasureDatabaseAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!IsEnabled)
        {
            await operation().ConfigureAwait(false);
            return;
        }
        AddCounter("database_operation_count");
        using var timing = MeasureMetric("DATABASE", "database_operation");
        await operation().ConfigureAwait(false);
    }

    internal static void Restore(
        OperationPerformanceSession session,
        OperationPerformanceSession? previous,
        OperationPerformanceContext? previousContext)
    {
        var state = StateSlot.Value;
        if (state is null || !ReferenceEquals(state.Session, session))
            return;
        state.Session = previous;
        state.Context = previousContext;
    }

    private static string BuildStageName(
        OperationPerformanceContext context,
        string metric) => context switch
        {
            OperationPerformanceContext.InstallPreflight =>
                $"PERF_INSTALL_PREFLIGHT_{metric}",
            OperationPerformanceContext.Install =>
                $"PERF_INSTALL_{metric}",
            OperationPerformanceContext.RemovePreflight =>
                $"PERF_REMOVE_PREFLIGHT_{metric}",
            OperationPerformanceContext.Remove =>
                $"PERF_REMOVE_{metric}",
            _ => throw new ArgumentOutOfRangeException(nameof(context))
        };

    private sealed class AmbientState
    {
        internal OperationPerformanceSession? Session { get; set; }
        internal OperationPerformanceContext? Context { get; set; }
    }

    private sealed class ContextScope(
        AmbientState state,
        OperationPerformanceContext context,
        OperationPerformanceContext? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (state.Context == context)
                state.Context = previous;
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}

public sealed class OperationPerformanceSession : IDisposable
{
    private readonly object _gate = new();
    private readonly string _operation;
    private readonly PackageId _packageId;
    private readonly Action<string, string?> _observer;
    private readonly OperationPerformanceSession? _previous;
    private readonly OperationPerformanceContext? _previousContext;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, long> _counters =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Aggregate> _stages =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Aggregate> _operations =
        new(StringComparer.Ordinal);
    private int _disposed;

    internal OperationPerformanceSession(
        string operation,
        PackageId packageId,
        Action<string, string?> observer,
        OperationPerformanceSession? previous,
        OperationPerformanceContext? previousContext)
    {
        _operation = operation;
        _packageId = packageId;
        _observer = observer;
        _previous = previous;
        _previousContext = previousContext;
    }

    internal IDisposable Measure(
        string stage,
        string? repeatedOperation) =>
        new TimingScope(this, stage, repeatedOperation);

    internal void AddCounter(string name, long value)
    {
        try
        {
            lock (_gate)
            {
                _counters.TryGetValue(name, out var current);
                _counters[name] = value > 0 && current > long.MaxValue - value
                    ? long.MaxValue
                    : current + value;
            }
        }
        catch
        {
            // Diagnostics are best-effort and never affect product behavior.
        }
    }

    internal void ObserveMaximum(string name, long value)
    {
        try
        {
            lock (_gate)
            {
                if (!_counters.TryGetValue(name, out var current) ||
                    value > current)
                {
                    _counters[name] = value;
                }
            }
        }
        catch
        {
            // Diagnostics are best-effort and never affect product behavior.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            var requestElapsed = Stopwatch.GetElapsedTime(_started);
            KeyValuePair<string, Aggregate>[] repeated;
            KeyValuePair<string, long>[] counters;
            KeyValuePair<string, Aggregate>[] stages;
            lock (_gate)
            {
                repeated = _operations
                    .Where(item => item.Value.Count > 1)
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToArray();
                counters = _counters
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToArray();
                stages = _stages
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToArray();
            }
            foreach (var item in repeated)
            {
                Observe(
                    "REPEATED_OPERATION",
                    $"name={item.Key} count={item.Value.Count} " +
                    $"total_elapsed_ms={Milliseconds(item.Value.Elapsed)}");
            }
            var measuredTotal = stages
                .Where(item => IsPipelineTotal(item.Key))
                .Aggregate(
                    TimeSpan.Zero,
                    (elapsed, item) => elapsed + item.Value.Elapsed);
            if (measuredTotal == TimeSpan.Zero)
                measuredTotal = requestElapsed;
            var summary = new StringBuilder()
                .Append("operation=").Append(_operation)
                .Append(" package_id=").Append(_packageId.Value)
                .Append(" total_ms=").Append(Milliseconds(measuredTotal))
                .Append(" request_elapsed_ms=")
                .Append(Milliseconds(requestElapsed));
            foreach (var item in counters)
            {
                summary.Append(' ').Append(item.Key).Append('=')
                    .Append(item.Value);
            }
            foreach (var item in stages)
            {
                summary.Append(' ').Append(item.Key.ToLowerInvariant())
                    .Append("_count=").Append(item.Value.Count)
                    .Append(' ').Append(item.Key.ToLowerInvariant())
                    .Append("_ms=").Append(Milliseconds(item.Value.Elapsed));
            }
            Observe("PERF_SUMMARY", summary.ToString());
        }
        catch
        {
            // Diagnostics are best-effort and never affect product behavior.
        }
        finally
        {
            OperationPerformanceDiagnostics.Restore(
                this,
                _previous,
                _previousContext);
        }
    }

    private void EndTiming(
        string stage,
        string? repeatedOperation,
        long started)
    {
        try
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            lock (_gate)
            {
                Add(_stages, stage, elapsed);
                if (!string.IsNullOrWhiteSpace(repeatedOperation))
                    Add(_operations, repeatedOperation, elapsed);
            }
            Observe(stage, $"duration_ms={Milliseconds(elapsed)}");
        }
        catch
        {
            // Diagnostics are best-effort and never affect product behavior.
        }
    }

    private void Observe(string eventName, string detail)
    {
        try
        {
            _observer(eventName, detail);
        }
        catch
        {
            // Diagnostics are best-effort and never affect product behavior.
        }
    }

    private static void Add(
        IDictionary<string, Aggregate> target,
        string name,
        TimeSpan elapsed)
    {
        target.TryGetValue(name, out var current);
        target[name] = new(
            current.Count + 1,
            current.Elapsed + elapsed);
    }

    private static string Milliseconds(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    private bool IsPipelineTotal(string stage) =>
        stage == $"PERF_{_operation.ToUpperInvariant()}_TOTAL" ||
        stage == $"PERF_{_operation.ToUpperInvariant()}_PREFLIGHT_TOTAL";

    private readonly record struct Aggregate(int Count, TimeSpan Elapsed);

    private sealed class TimingScope(
        OperationPerformanceSession owner,
        string stage,
        string? repeatedOperation) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndTiming(stage, repeatedOperation, _started);
        }
    }
}
