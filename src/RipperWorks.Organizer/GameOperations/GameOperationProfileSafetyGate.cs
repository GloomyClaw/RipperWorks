namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Durable profile-safety policy shared by admission and queued execution.
/// </summary>
internal sealed class GameOperationProfileSafetyGate
{
    private readonly GameOperationSubmissionOwnership _submissionOwnership;

    public GameOperationProfileSafetyGate(
        GameOperationSubmissionOwnership submissionOwnership) =>
        _submissionOwnership = submissionOwnership;

    public async Task<bool> IsBlockedAsync(
        IGameOperationJournal journal,
        CanonicalProfileKey profileKey,
        OperationId? currentOperationId,
        CancellationToken cancellationToken)
    {
        var rows = await journal.LoadByProfileAsync(profileKey, cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (currentOperationId == row.OperationId)
                continue;

            if (row.State is GameOperationState.RecoveryRequired)
                return true;

            if (GameOperationExecutionSession.IsTerminalState(row.State))
                continue;

            // Provisional or identity-mismatched ownership cannot mask an orphan.
            if (_submissionOwnership.HasDurableOwner(
                    profileKey,
                    new IdempotencyKey(row.IdempotencyKey),
                    row.OperationId,
                    row.PlanHash))
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
