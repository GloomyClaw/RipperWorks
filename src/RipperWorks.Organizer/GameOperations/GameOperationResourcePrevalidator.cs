namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Read-only ExpectedBefore validation against live destinations before
/// MutationStarted. No filesystem writes.
/// </summary>
internal static class GameOperationResourcePrevalidator
{
    public static async Task<PreconditionOutcome> ValidateExpectedBeforeAsync(
        GameOperationPlan plan,
        ShadowHarnessPermit? permit,
        CancellationToken cancellationToken = default,
        IGameOperationResourceStateReader? resourceReader = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (permit is null)
            return PreconditionOutcome.Block("ShadowRootNotAllowed");

        var rootCheck = permit.ValidateMutationRoots(plan, journalPath: null);
        if (!rootCheck.IsAllowed)
            return rootCheck;

        foreach (var step in plan.Steps.OrderBy(s => s.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination;
            try
            {
                destination = ArchivePathSafety.ResolveSafeGamePath(
                    plan.ProfileKey.Value,
                    step.RelativePath);
            }
            catch (Exception)
            {
                return PreconditionOutcome.Block("UnsafeRelativePath");
            }

            if (!permit.IsAllowedPath(destination) ||
                permit.HasReparseAncestorUnderHarness(destination) ||
                ShadowHarnessPermit.IsReparsePoint(destination))
            {
                return PreconditionOutcome.Block("UnsafeReparsePath");
            }

            try
            {
                SyntheticGameOperationExecutor.RejectReparseOnPath(
                    Path.GetFullPath(plan.ProfileKey.Value),
                    step.RelativePath);
            }
            catch (Exception)
            {
                return PreconditionOutcome.Block("UnsafeReparsePath");
            }

            var outcome = await GameOperationResourceIdentityValidator.ValidateAsync(
                    resourceReader ?? FileSystemGameOperationResourceStateReader.Shared,
                    destination,
                    step.ExpectedBeforeIdentity,
                    allowSyntheticText: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.IsAllowed)
                return outcome;
        }

        return PreconditionOutcome.Allow();
    }
}
