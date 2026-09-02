using System.Text;

namespace RipperWorks.Organizer.GameOperations;

internal interface IGameOperationPlanContractValidator
{
    Task<PreconditionOutcome> ValidateAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default);
}

internal sealed class SyntheticGameOperationPlanContractValidator
    : IGameOperationPlanContractValidator
{
    public Task<PreconditionOutcome> ValidateAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var step in plan.Steps)
        {
            var kindMatches = plan.OperationKind switch
            {
                GameOperationKind.InstallLike =>
                    step.StepKind is GameOperationStepKind.WriteFile
                        or GameOperationStepKind.VerifyFile,
                GameOperationKind.RemoveLike =>
                    step.StepKind is GameOperationStepKind.DeleteFile
                        or GameOperationStepKind.VerifyFile,
                _ => false
            };
            if (!kindMatches)
                return Task.FromResult(PreconditionOutcome.Block("ExecutorPlanKindMismatch"));
        }

        return Task.FromResult(PreconditionOutcome.Allow());
    }
}

internal sealed class ProductionGameOperationPlanContractValidator
    : IGameOperationPlanContractValidator
{
    private readonly OrganizerManagedPathAuthority _pathAuthority;
    private readonly ContentStoreService _contentStore;
    private readonly RipperWorks.Core.PackageId? _owningPackageId;
    private readonly ShadowHarnessPermit _permit;
    private readonly IGameOperationResourceStateReader _resourceReader;

    public ProductionGameOperationPlanContractValidator(
        OrganizerManagedPathAuthority pathAuthority,
        ContentStoreService contentStore,
        RipperWorks.Core.PackageId? owningPackageId,
        ShadowHarnessPermit permit,
        IGameOperationResourceStateReader resourceReader)
    {
        _pathAuthority = pathAuthority;
        _contentStore = contentStore;
        _owningPackageId = owningPackageId;
        _permit = permit;
        _resourceReader = resourceReader;
    }

    public async Task<PreconditionOutcome> ValidateAsync(
        GameOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreconditionOutcome outcome;
            if (step.StepKind == GameOperationStepKind.WriteFile)
                outcome = await ValidateWriteAsync(plan, step, cancellationToken)
                    .ConfigureAwait(false);
            else if (step.StepKind == GameOperationStepKind.DeleteFile)
                outcome = await ValidateDeleteAsync(plan, step, cancellationToken)
                    .ConfigureAwait(false);
            else
                outcome = PreconditionOutcome.Block("ExecutorPlanKindMismatch");

            if (!outcome.IsAllowed)
                return outcome;
        }

        return PreconditionOutcome.Allow();
    }

    private async Task<PreconditionOutcome> ValidateWriteAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken)
    {
        if (plan.OperationKind != GameOperationKind.InstallLike)
            return PreconditionOutcome.Block("ExecutorPlanKindMismatch");
        if (!TrySha(step.ContentIdentity, out var contentSha) ||
            !TryMissingOrSha(step.ExpectedBeforeIdentity, out _) ||
            !TrySha(step.ExpectedAfterIdentity, out var afterSha) ||
            !string.Equals(contentSha, afterSha, StringComparison.Ordinal))
        {
            return PreconditionOutcome.Block("ExecutorPlanContractInvalid");
        }

        var objectPath = _contentStore.GetObjectPath(contentSha);
        return await ValidateContentObjectAsync(
                objectPath,
                contentSha,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PreconditionOutcome> ValidateDeleteAsync(
        GameOperationPlan plan,
        GameOperationStepSpec step,
        CancellationToken cancellationToken)
    {
        if (plan.OperationKind != GameOperationKind.RemoveLike ||
            !TrySha(step.ExpectedBeforeIdentity, out var beforeSha) ||
            !TrySha(step.ContentIdentity, out var contentSha) ||
            !string.Equals(beforeSha, contentSha, StringComparison.Ordinal) ||
            !TryMissingOrSha(step.ExpectedAfterIdentity, out var afterSha))
        {
            return PreconditionOutcome.Block("ExecutorPlanContractInvalid");
        }

        var top = await _pathAuthority.GetTopLayerAsync(
                step.RelativePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (top is null ||
            !string.Equals(top.ContentHash, beforeSha, StringComparison.OrdinalIgnoreCase) ||
            (_owningPackageId is not null && top.PackageId != _owningPackageId.Value))
        {
            return PreconditionOutcome.Block("ExecutorPlanContractInvalid");
        }

        var expectedRestore = await _pathAuthority.GetPlannedRestoreIdentityAsync(
                step.RelativePath,
                top,
                cancellationToken)
            .ConfigureAwait(false);
        if (expectedRestore is null)
        {
            return GameOperationStepIdentity.IsMissing(step.ExpectedAfterIdentity)
                ? PreconditionOutcome.Allow()
                : PreconditionOutcome.Block("ExecutorPlanContractInvalid");
        }

        if (!string.Equals(
                expectedRestore,
                afterSha,
                StringComparison.OrdinalIgnoreCase))
        {
            return PreconditionOutcome.Block("ExecutorPlanContractInvalid");
        }

        return await ValidateContentObjectAsync(
                _contentStore.GetObjectPath(expectedRestore),
                expectedRestore,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PreconditionOutcome> ValidateContentObjectAsync(
        string objectPath,
        string expectedSha,
        CancellationToken cancellationToken)
    {
        if (!_permit.IsAllowedPath(objectPath) ||
            _permit.HasReparseAncestorUnderHarness(objectPath) ||
            ShadowHarnessPermit.IsReparsePoint(objectPath))
        {
            return PreconditionOutcome.Block("ExecutorPlanContractInvalid");
        }

        var state = await _resourceReader.ReadAsync(
                objectPath,
                readSha256: true,
                readSyntheticText: false,
                cancellationToken)
            .ConfigureAwait(false);
        return state.Kind == GameOperationResourceKind.RegularFile &&
               string.Equals(
                   state.Sha256,
                   expectedSha,
                   StringComparison.OrdinalIgnoreCase)
            ? PreconditionOutcome.Allow()
            : PreconditionOutcome.Block("ExecutorPlanContractInvalid");
    }

    private static bool TrySha(string? identity, out string sha) =>
        GameOperationStepIdentity.TryGetSha256(identity, out sha);

    private static bool TryMissingOrSha(string? identity, out string sha)
    {
        sha = string.Empty;
        return GameOperationStepIdentity.IsMissing(identity) ||
               GameOperationStepIdentity.TryGetSha256(identity, out sha);
    }
}

internal static class GameOperationExecutorContract
{
    internal sealed record PersistedValidation(
        PreconditionOutcome Outcome,
        GameOperationPlan? Plan);

    public static async Task<PreconditionOutcome> ValidateBeforeAcceptanceAsync(
        IGameOperationExecutor executor,
        ShadowHarnessPermit permit,
        GameOperationPlan plan,
        CancellationToken cancellationToken)
    {
        var binding = ValidateBinding(executor, permit);
        if (!binding.IsAllowed)
            return binding;
        var bound = (IShadowHarnessBoundExecutor)executor;
        return await bound.PlanContractValidator
            .ValidateAsync(plan, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<PersistedValidation> ValidatePersistedAsync(
        IGameOperationJournal journal,
        OperationId operationId,
        IGameOperationExecutor executor,
        ShadowHarnessPermit permit,
        GameOperationPlan callerPlan,
        CancellationToken cancellationToken)
    {
        var binding = ValidateBinding(executor, permit);
        if (!binding.IsAllowed)
            return new(binding, null);

        var row = await journal.LoadAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
            return new(
                PreconditionOutcome.Block("PersistedPlanMissing"),
                null);

        GameOperationPlan persisted;
        try
        {
            persisted = GameOperationPlan.Deserialize(row.SerializedPlan);
        }
        catch
        {
            return new(
                PreconditionOutcome.Block("PersistedPlanContractMismatch"),
                null);
        }

        if (!string.Equals(persisted.PlanHash, row.PlanHash, StringComparison.Ordinal) ||
            !string.Equals(persisted.PlanHash, callerPlan.PlanHash, StringComparison.Ordinal) ||
            !string.Equals(persisted.Serialize(), callerPlan.Serialize(), StringComparison.Ordinal))
        {
            return new(
                PreconditionOutcome.Block("PersistedPlanContractMismatch"),
                null);
        }

        var outcome = await ((IShadowHarnessBoundExecutor)executor)
            .PlanContractValidator
            .ValidateAsync(persisted, cancellationToken)
            .ConfigureAwait(false);
        return new(outcome, outcome.IsAllowed ? persisted : null);
    }

    public static PreconditionOutcome ValidateBinding(
        IGameOperationExecutor executor,
        ShadowHarnessPermit permit)
    {
        if (executor is not IShadowHarnessBoundExecutor bound ||
            bound.BoundPermit is null)
        {
            return PreconditionOutcome.Block("ExecutorNotHarnessBound");
        }

        if (!ReferenceEquals(bound.BoundPermit, permit))
            return PreconditionOutcome.Block("ExecutorPermitMismatch");
        return bound.ValidateHarness(permit);
    }
}
