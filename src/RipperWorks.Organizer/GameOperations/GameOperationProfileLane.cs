namespace RipperWorks.Organizer.GameOperations;

/// <summary>Per-profile FIFO lane owned by GameOperationCoordinator.</summary>
internal sealed class GameOperationProfileLane
{
    private readonly GameOperationCoordinator _owner;
    private readonly object _laneSync = new();
    private readonly Queue<LaneItem> _queue = new();
    private Task? _worker;
    private bool _poisoned;

    public GameOperationProfileLane(GameOperationCoordinator owner)
    {
        _owner = owner;
    }

    public bool HasActiveWork
    {
        get
        {
            lock (_laneSync)
                return _worker is not null || _queue.Count > 0;
        }
    }

    public bool IsPoisoned
    {
        get
        {
            lock (_laneSync)
                return _poisoned;
        }
    }

    public Task<GameOperationResult> Enqueue(
        OperationId operationId,
        GameOperationPlan plan,
        IdempotencyKey key,
        CancellationToken cancellationToken)
    {
        lock (_laneSync)
        {
            if (!_poisoned)
            {
                var tcs = new TaskCompletionSource<GameOperationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var item = new LaneItem(
                    operationId,
                    plan,
                    key,
                    tcs,
                    cancellationToken);
                _queue.Enqueue(item);
                _worker ??= Task.Run(RunAsync);
                return tcs.Task;
            }
        }

        return _owner.BlockPoisonedAcceptedAsync(operationId);
    }

    public void CancelQueued()
    {
        lock (_laneSync)
        {
            foreach (var item in _queue)
                item.Cts.Cancel();
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        Task? worker;
        lock (_laneSync)
            worker = _worker;
        if (worker is null)
            return;

        try
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // If cancellation requested while workers may still be running,
            // do not treat as clean success.
            lock (_laneSync)
            {
                if (_worker is not null || _queue.Count > 0)
                    throw;
            }

            throw;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            LaneItem? item;
            lock (_laneSync)
            {
                if (_queue.Count == 0)
                {
                    _worker = null;
                    return;
                }

                item = _queue.Dequeue();
            }

            Exception? poison = null;
            LaneItem[] pending = [];
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    item.CallerToken,
                    item.Cts.Token);
                var result = await _owner.ExecuteOwnedAsync(
                        item.OperationId,
                        item.Plan,
                        item.Key,
                        linked.Token)
                    .ConfigureAwait(false);
                item.Completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                lock (_laneSync)
                {
                    _poisoned = true;
                    pending = _queue.ToArray();
                    _queue.Clear();
                }

                item.Completion.TrySetException(exception);
                poison = exception;
            }
            finally
            {
                item.Dispose();
            }

            if (poison is not null)
            {
                await CompletePoisonedAsync(pending, poison)
                    .ConfigureAwait(false);
                lock (_laneSync)
                    _worker = null;
                return;
            }
        }
    }

    private async Task CompletePoisonedAsync(
        IReadOnlyList<LaneItem> pending,
        Exception poison)
    {
        foreach (var item in pending)
        {
            try
            {
                var result = await _owner.BlockPoisonedAcceptedAsync(
                        item.OperationId)
                    .ConfigureAwait(false);
                item.Completion.TrySetResult(result);
            }
            catch
            {
                item.Completion.TrySetException(poison);
            }
            finally
            {
                item.Dispose();
            }
        }
    }

    private sealed class LaneItem : IDisposable
    {
        private bool _disposed;

        public LaneItem(
            OperationId operationId,
            GameOperationPlan plan,
            IdempotencyKey key,
            TaskCompletionSource<GameOperationResult> completion,
            CancellationToken callerToken)
        {
            OperationId = operationId;
            Plan = plan;
            Key = key;
            Completion = completion;
            CallerToken = callerToken;
            Cts = new CancellationTokenSource();
        }

        public OperationId OperationId { get; }
        public GameOperationPlan Plan { get; }
        public IdempotencyKey Key { get; }
        public TaskCompletionSource<GameOperationResult> Completion { get; }
        public CancellationToken CallerToken { get; }
        public CancellationTokenSource Cts { get; }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Cts.Dispose();
        }
    }
}
