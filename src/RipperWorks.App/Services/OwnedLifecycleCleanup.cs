using System.Diagnostics;
using RipperWorks.Core;

namespace RipperWorks.App.Services;

public sealed class ApplicationCleanupPendingException(
    TimeSpan shutdownBudget)
    : TimeoutException(
        $"Application cleanup did not complete within the shared " +
        $"{shutdownBudget.TotalMilliseconds:F0} ms shutdown budget.")
{
    public TimeSpan ShutdownBudget { get; } = shutdownBudget;
}

internal readonly struct ShutdownDeadline
{
    private readonly long _deadline;

    public ShutdownDeadline(TimeSpan budget)
    {
        var ticks = budget.TotalSeconds * Stopwatch.Frequency;
        _deadline = Stopwatch.GetTimestamp() +
            (long)Math.Ceiling(ticks);
    }

    public TimeSpan Remaining
    {
        get
        {
            var ticks = _deadline - Stopwatch.GetTimestamp();
            return ticks <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(
                    ticks / (double)Stopwatch.Frequency);
        }
    }

    public TimeSpan FairShare(int ownersRemaining)
    {
        var remaining = Remaining;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;
        return TimeSpan.FromTicks(
            Math.Max(1, remaining.Ticks / Math.Max(1, ownersRemaining)));
    }
}

internal sealed class OwnedStopAttempt
{
    private readonly Action<string, Exception> _observe;
    private int _late;

    public OwnedStopAttempt(
        string ownerName,
        Func<Task> operation,
        Action<string, Exception> observe)
    {
        OwnerName = ownerName;
        _observe = observe;
        Completion = RunAsync(operation);
    }

    public string OwnerName { get; }
    public Task<Exception?> Completion { get; }

    public TimeoutException? MarkLate()
    {
        if (Interlocked.Exchange(ref _late, 1) != 0)
            return null;
        var timeout = new TimeoutException(
            $"{OwnerName} did not stop within its share of the " +
            "application shutdown budget.");
        _observe($"{OwnerName}.StopBounded", timeout);
        return timeout;
    }

    private async Task<Exception?> RunAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            var stage = Volatile.Read(ref _late) == 0
                ? $"{OwnerName}.Stop"
                : $"{OwnerName}.LateStop";
            _observe(stage, exception);
            return exception;
        }
    }
}

internal sealed class OwnedLifecycleCleanup
{
    private readonly Action<string, Exception> _observe;
    private readonly Dictionary<IApplicationModule, OwnedStopAttempt>
        _moduleStops = [];
    private readonly HashSet<OwnedStopAttempt> _lateStopAttempts = [];
    private readonly List<Exception> _immediateStopFailures = [];
    private readonly List<Exception> _policyFailures = [];
    private OwnedStopAttempt? _singleInstanceStop;
    private Task<IReadOnlyList<Exception>>? _completion;

    public OwnedLifecycleCleanup(
        Action<string, Exception> observe)
    {
        _observe = observe;
    }

    public IReadOnlyList<Exception> PolicyFailures =>
        _policyFailures;
    public bool HasPendingTerminalStops =>
        _lateStopAttempts.Any(
            attempt => !attempt.Completion.IsCompleted);

    public OwnedStopAttempt GetModuleStop(
        IApplicationModule module,
        CancellationToken cancellationToken)
    {
        if (_moduleStops.TryGetValue(module, out var attempt))
            return attempt;
        attempt = new OwnedStopAttempt(
            module.Name,
            () => module.StopAsync(cancellationToken),
            _observe);
        _moduleStops.Add(module, attempt);
        return attempt;
    }

    public OwnedStopAttempt GetSingleInstanceStop(
        Func<Task> stop) =>
        _singleInstanceStop ??= new OwnedStopAttempt(
            "SingleInstance",
            stop,
            _observe);

    public void MarkTerminalLate(
        OwnedStopAttempt attempt,
        TimeoutException timeout)
    {
        _lateStopAttempts.Add(attempt);
        _policyFailures.Add(timeout);
    }

    public void RecordImmediateStopFailure(Exception exception) =>
        _immediateStopFailures.Add(exception);

    public Task<IReadOnlyList<Exception>> Start(
        IReadOnlyList<IApplicationModule> modules,
        ISingleInstanceCoordinator? singleInstance,
        Action disposeShell,
        CancellationTokenSource applicationCancellation)
    {
        _completion ??= CompleteAsync(
            modules,
            singleInstance,
            disposeShell,
            applicationCancellation);
        return _completion;
    }

    private async Task<IReadOnlyList<Exception>> CompleteAsync(
        IReadOnlyList<IApplicationModule> modules,
        ISingleInstanceCoordinator? singleInstance,
        Action disposeShell,
        CancellationTokenSource applicationCancellation)
    {
        var errors = new List<Exception>(_immediateStopFailures);
        for (var index = modules.Count - 1; index >= 0; index--)
        {
            var module = modules[index];
            if (_moduleStops.TryGetValue(module, out var stop))
            {
                var stopError = await stop.Completion
                    .ConfigureAwait(false);
                if (stopError is not null &&
                    _lateStopAttempts.Contains(stop))
                {
                    errors.Add(stopError);
                }
            }
            try
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _observe($"{module.Name}.Dispose", exception);
                errors.Add(exception);
            }
        }

        if (_singleInstanceStop is not null)
        {
            var stopError = await _singleInstanceStop.Completion
                .ConfigureAwait(false);
            if (stopError is not null &&
                _lateStopAttempts.Contains(_singleInstanceStop))
            {
                errors.Add(stopError);
            }
        }
        if (singleInstance is not null)
        {
            try
            {
                await singleInstance.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _observe("SingleInstance.Dispose", exception);
                errors.Add(exception);
            }
        }

        disposeShell();
        applicationCancellation.Dispose();
        return errors;
    }
}
