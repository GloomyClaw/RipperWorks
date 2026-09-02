using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed partial class UnifiedModMutationCoordinator
{
    internal Func<RecoveryPlan, CancellationToken, Task>?
        TestOnlyBeforeRecoveryMutation
    {
        get => _recoveryExecutor.TestOnlyBeforeMutation;
        set => _recoveryExecutor.TestOnlyBeforeMutation = value;
    }

    internal Func<RecoveryPathAction, int, Task>?
        TestOnlyAfterRecoveryPathMutation
    {
        get => _recoveryExecutor.TestOnlyAfterPathMutation;
        set => _recoveryExecutor.TestOnlyAfterPathMutation = value;
    }

    internal Func<RecoveryPlan, Task>? TestOnlyBeforeRecoveryVerification
    {
        get => _recoveryExecutor.TestOnlyBeforeVerification;
        set => _recoveryExecutor.TestOnlyBeforeVerification = value;
    }

    public Task<RecoveryPlan> PrepareRecoveryAsync(
        Guid operationId,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default) =>
        _recoveryPlanner.PrepareAsync(
            operationId, profile, libraryRoot, cancellationToken);

    public async Task<RecoveryResult> ExecuteRecoveryAsync(
        RecoveryPlan plan,
        GameProfileRecord? profile,
        string? libraryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.AlreadyRecovered)
        {
            return new RecoveryResult
            {
                OperationId = plan.OperationId,
                Success = true,
                AlreadyRecovered = true,
                Plan = plan
            };
        }
        if (!plan.IsDeterministic || profile is null)
            return BlockedRecovery(plan);
        if (TestOnlyBeforeExecutionGateWait is not null)
            await TestOnlyBeforeExecutionGateWait("Recovery");
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            if (TestOnlyAfterExecutionGateEntered is not null)
                await TestOnlyAfterExecutionGateEntered("Recovery");
            var fresh = await _recoveryPlanner.PrepareAsync(
                plan.OperationId, profile, libraryRoot, cancellationToken);
            if (!fresh.IsDeterministic ||
                !string.Equals(
                    fresh.ValidationToken,
                    plan.ValidationToken,
                    StringComparison.Ordinal))
            {
                return new RecoveryResult
                {
                    OperationId = plan.OperationId,
                    ErrorCode = "RecoveryPlanInvalidated",
                    Plan = fresh
                };
            }
            return await _recoveryExecutor.ExecuteAsync(
                fresh, profile, libraryRoot, cancellationToken);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private static RecoveryResult BlockedRecovery(RecoveryPlan plan) => new()
    {
        OperationId = plan.OperationId,
        ErrorCode = plan.Blockers.FirstOrDefault() ?? "RecoveryUnsupported",
        Plan = plan
    };
}
