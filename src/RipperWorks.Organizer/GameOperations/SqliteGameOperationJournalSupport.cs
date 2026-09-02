using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RipperWorks.Organizer.GameOperations;

/// <summary>
/// Load, step, and transition helpers for <see cref="SqliteGameOperationJournal"/>.
/// Schema open/create lives in <see cref="SqliteGameOperationJournalSchema"/>.
/// </summary>
internal static class SqliteGameOperationJournalSupport
{
    public static async Task ApplyTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        OperationId operationId,
        GameOperationState from,
        GameOperationState to,
        GameOperationPhase phase,
        string? errorCode,
        string? redactedDetail,
        CancellationToken cancellationToken)
    {
        if (from == GameOperationState.Finalizing &&
            to == GameOperationState.Completed)
        {
            await AssertAllStepsVerifiedForCompletionAsync(
                    connection,
                    tx,
                    operationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            UPDATE Operations
            SET State = $to,
                Phase = $phase,
                ErrorCode = COALESCE($error, ErrorCode),
                RedactedErrorDetail = COALESCE($detail, RedactedErrorDetail),
                StartedAtUtc = CASE
                    WHEN $to = 'Validating' AND StartedAtUtc IS NULL
                    THEN $now ELSE StartedAtUtc END,
                CompletedAtUtc = CASE
                    WHEN $terminal = 1 THEN $now ELSE CompletedAtUtc END
            WHERE OperationId = $id AND State = $from;
            """;
        command.Parameters.AddWithValue("$to", to.ToString());
        command.Parameters.AddWithValue("$phase", phase.ToString());
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)redactedDetail ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$terminal", IsTerminal(to) ? 1 : 0);
        command.Parameters.AddWithValue("$id", operationId.ToString());
        command.Parameters.AddWithValue("$from", from.ToString());
        var updated = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
            throw new InvalidOperationException("StateTransitionConflict");
    }

    public static async Task RecordStepIntentCoreAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        OperationId operationId,
        GameOperationStepSpec step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);

        string stateText;
        int lastSequence;
        string serializedPlan;
        string planHash;
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText =
                """
                SELECT State, LastSequence, SerializedPlan, PlanHash
                FROM Operations WHERE OperationId = $id;
                """;
            check.Parameters.AddWithValue("$id", operationId.ToString());
            await using var reader =
                await check.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("StepIntentMissingOperation");
            stateText = reader.GetString(0);
            lastSequence = reader.GetInt32(1);
            serializedPlan = reader.GetString(2);
            planHash = reader.GetString(3);
        }

        if (!string.Equals(
                stateText,
                GameOperationState.Executing.ToString(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("StepIntentRequiresExecuting");
        }

        if (step.Sequence != lastSequence + 1)
            throw new InvalidOperationException("StepIntentSequenceOrder");

        // Authority is the durable SerializedPlan + PlanHash, not a caller plan.
        GameOperationPlan persistedPlan;
        try
        {
            persistedPlan = GameOperationPlan.Deserialize(serializedPlan);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "StepIntentPersistedPlanInvalid",
                exception);
        }

        // Deserialize recomputes PlanHash; require match with durable column.
        if (!string.Equals(
                persistedPlan.PlanHash,
                planHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("StepIntentPlanHashMismatch");
        }

        var planned = persistedPlan.Steps
            .FirstOrDefault(s => s.Sequence == step.Sequence);
        if (planned is null)
            throw new InvalidOperationException("StepIntentNotInPlan");

        if (planned.Sequence != step.Sequence ||
            !string.Equals(planned.StepKey, step.StepKey, StringComparison.Ordinal) ||
            planned.StepKind != step.StepKind ||
            !string.Equals(
                planned.RelativePath,
                step.RelativePath,
                StringComparison.Ordinal) ||
            !string.Equals(
                planned.ContentIdentity,
                step.ContentIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                planned.ExpectedBeforeIdentity,
                step.ExpectedBeforeIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                planned.ExpectedAfterIdentity,
                step.ExpectedAfterIdentity,
                StringComparison.Ordinal))
        {
            // Rollback is automatic when the transaction is not committed.
            throw new InvalidOperationException("StepIntentPlanMismatch");
        }

        // Explicit per-resource fields from the immutable plan step.
        var expectedBefore = planned.ExpectedBeforeIdentity;
        var expectedAfter = planned.ExpectedAfterIdentity;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText =
                """
                INSERT INTO OperationSteps (
                    OperationId, Sequence, StepKey, StepKind, State,
                    IntentRecordedAtUtc, ExpectedBeforeIdentity,
                    ExpectedAfterIdentity)
                VALUES (
                    $id, $seq, $key, $kind, $state, $now, $before, $after);
                """;
            command.Parameters.AddWithValue("$id", operationId.ToString());
            command.Parameters.AddWithValue("$seq", step.Sequence);
            command.Parameters.AddWithValue("$key", step.StepKey);
            command.Parameters.AddWithValue("$kind", step.StepKind.ToString());
            command.Parameters.AddWithValue(
                "$state",
                GameOperationStepState.IntentRecorded.ToString());
            command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
            command.Parameters.AddWithValue(
                "$before",
                (object?)expectedBefore ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$after",
                (object?)expectedAfter ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE Operations SET LastSequence = $seq
                WHERE OperationId = $id;
                """;
            update.Parameters.AddWithValue("$seq", step.Sequence);
            update.Parameters.AddWithValue("$id", operationId.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public static async Task AdvanceStepStateAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        OperationId operationId,
        int sequence,
        GameOperationStepState from,
        GameOperationStepState to,
        string timeColumn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            $"""
            UPDATE OperationSteps
            SET State = $state, {timeColumn} = $now
            WHERE OperationId = $id
              AND Sequence = $seq
              AND State = $from;
            """;
        command.Parameters.AddWithValue("$state", to.ToString());
        command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", operationId.ToString());
        command.Parameters.AddWithValue("$seq", sequence);
        command.Parameters.AddWithValue("$from", from.ToString());
        var n = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (n != 1)
        {
            throw new InvalidOperationException(
                to == GameOperationStepState.Applied
                    ? "StepAppliedMissing"
                    : "StepVerifiedMissing");
        }
    }

    public static async Task AssertAllStepsVerifiedForCompletionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        int planStepCount;
        await using (var planCmd = connection.CreateCommand())
        {
            planCmd.Transaction = transaction;
            planCmd.CommandText =
                "SELECT SerializedPlan FROM Operations WHERE OperationId = $id;";
            planCmd.Parameters.AddWithValue("$id", operationId.ToString());
            var planJson = await planCmd.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (planJson is null or DBNull)
                throw new InvalidOperationException("CompletionMissingPlan");
            var plan = GameOperationPlan.Deserialize(Convert.ToString(planJson)!);
            planStepCount = plan.Steps.Count;
        }

        int total;
        int verified;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.Transaction = transaction;
            countCmd.CommandText =
                """
                SELECT
                    COUNT(*),
                    SUM(CASE WHEN State = $verified THEN 1 ELSE 0 END)
                FROM OperationSteps
                WHERE OperationId = $id;
                """;
            countCmd.Parameters.AddWithValue("$id", operationId.ToString());
            countCmd.Parameters.AddWithValue(
                "$verified",
                GameOperationStepState.Verified.ToString());
            await using var reader =
                await countCmd.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("CompletionStepCountFailed");
            total = reader.GetInt32(0);
            verified = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
        }

        if (total != planStepCount || verified != planStepCount || total == 0)
        {
            throw new InvalidOperationException("CompletionStepsIncomplete");
        }

        // Sequences must be exactly 1..n.
        await using (var seqCmd = connection.CreateCommand())
        {
            seqCmd.Transaction = transaction;
            seqCmd.CommandText =
                """
                SELECT Sequence FROM OperationSteps
                WHERE OperationId = $id
                ORDER BY Sequence ASC;
                """;
            seqCmd.Parameters.AddWithValue("$id", operationId.ToString());
            await using var reader =
                await seqCmd.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            var expected = 1;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt32(0) != expected)
                {
                    throw new InvalidOperationException(
                        "CompletionStepSequenceMismatch");
                }

                expected++;
            }

            if (expected - 1 != planStepCount)
            {
                throw new InvalidOperationException(
                    "CompletionStepSequenceMismatch");
            }
        }
    }

    public static string FormatUtc(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTime ParseUtc(string value) =>
        DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    public static bool IsTerminal(GameOperationState state) =>
        state is GameOperationState.Completed
            or GameOperationState.CancelledBeforeCommit
            or GameOperationState.BlockedBeforeCommit
            or GameOperationState.FailedBeforeCommit
            or GameOperationState.RecoveryRequired;

    public static bool IsValidTransition(
        GameOperationState from,
        GameOperationState to)
    {
        if (from == to)
            return false;

        return from switch
        {
            GameOperationState.Accepted =>
                to is GameOperationState.Queued
                    or GameOperationState.CancelledBeforeCommit
                    or GameOperationState.BlockedBeforeCommit
                    or GameOperationState.FailedBeforeCommit,
            GameOperationState.Queued =>
                to is GameOperationState.Validating
                    or GameOperationState.CancelledBeforeCommit
                    or GameOperationState.BlockedBeforeCommit
                    or GameOperationState.FailedBeforeCommit,
            GameOperationState.Validating =>
                to is GameOperationState.ReadyToCommit
                    or GameOperationState.BlockedBeforeCommit
                    or GameOperationState.CancelledBeforeCommit
                    or GameOperationState.FailedBeforeCommit,
            GameOperationState.ReadyToCommit =>
                to is GameOperationState.MutationStarted
                    or GameOperationState.CancelledBeforeCommit
                    or GameOperationState.BlockedBeforeCommit
                    or GameOperationState.FailedBeforeCommit,
            GameOperationState.MutationStarted =>
                to is GameOperationState.Executing
                    or GameOperationState.RecoveryRequired,
            GameOperationState.Executing =>
                to is GameOperationState.Finalizing
                    or GameOperationState.RecoveryRequired,
            GameOperationState.Finalizing =>
                to is GameOperationState.Completed
                    or GameOperationState.RecoveryRequired,
            _ => false
        };
    }

    public static async Task<GameOperationRecord?> LoadCoreAsync(
        SqliteConnection connection,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        GameOperationRecord? header = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT OperationId, CanonicalProfileKey, IdempotencyKey,
                       OperationKind, PlanVersion, PlanHash, SerializedPlan,
                       State, Phase, CreatedAtUtc, StartedAtUtc, CompletedAtUtc,
                       CancellationRequested, ErrorCode, RedactedErrorDetail,
                       LastSequence, JournalFormatVersion
                FROM Operations
                WHERE OperationId = $id;
                """;
            command.Parameters.AddWithValue("$id", operationId.ToString());
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            header = new GameOperationRecord
            {
                OperationId = OperationId.Parse(reader.GetString(0)),
                CanonicalProfileKey = reader.GetString(1),
                IdempotencyKey = reader.GetString(2),
                OperationKind = Enum.Parse<GameOperationKind>(reader.GetString(3)),
                PlanVersion = reader.GetInt32(4),
                PlanHash = reader.GetString(5),
                SerializedPlan = reader.GetString(6),
                State = Enum.Parse<GameOperationState>(reader.GetString(7)),
                Phase = Enum.Parse<GameOperationPhase>(reader.GetString(8)),
                CreatedAtUtc = ParseUtc(reader.GetString(9)),
                StartedAtUtc = reader.IsDBNull(10)
                    ? null
                    : ParseUtc(reader.GetString(10)),
                CompletedAtUtc = reader.IsDBNull(11)
                    ? null
                    : ParseUtc(reader.GetString(11)),
                CancellationRequested = reader.GetInt64(12) != 0,
                ErrorCode = reader.IsDBNull(13) ? null : reader.GetString(13),
                RedactedErrorDetail =
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                LastSequence = reader.GetInt32(15),
                JournalFormatVersion = reader.GetInt32(16)
            };
        }

        var steps = new List<GameOperationStepRecord>();
        await using (var stepCommand = connection.CreateCommand())
        {
            stepCommand.CommandText =
                """
                SELECT OperationId, Sequence, StepKey, StepKind, State,
                       IntentRecordedAtUtc, AppliedAtUtc, VerifiedAtUtc,
                       ExpectedBeforeIdentity, ExpectedAfterIdentity,
                       ErrorCode, RedactedErrorDetail
                FROM OperationSteps
                WHERE OperationId = $id
                ORDER BY Sequence ASC;
                """;
            stepCommand.Parameters.AddWithValue("$id", operationId.ToString());
            await using var reader =
                await stepCommand.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                steps.Add(new GameOperationStepRecord
                {
                    OperationId = OperationId.Parse(reader.GetString(0)),
                    Sequence = reader.GetInt32(1),
                    StepKey = reader.GetString(2),
                    StepKind = Enum.Parse<GameOperationStepKind>(reader.GetString(3)),
                    State = Enum.Parse<GameOperationStepState>(reader.GetString(4)),
                    IntentRecordedAtUtc = reader.IsDBNull(5)
                        ? null
                        : ParseUtc(reader.GetString(5)),
                    AppliedAtUtc = reader.IsDBNull(6)
                        ? null
                        : ParseUtc(reader.GetString(6)),
                    VerifiedAtUtc = reader.IsDBNull(7)
                        ? null
                        : ParseUtc(reader.GetString(7)),
                    ExpectedBeforeIdentity =
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                    ExpectedAfterIdentity =
                        reader.IsDBNull(9) ? null : reader.GetString(9),
                    ErrorCode =
                        reader.IsDBNull(10) ? null : reader.GetString(10),
                    RedactedErrorDetail =
                        reader.IsDBNull(11) ? null : reader.GetString(11)
                });
            }
        }

        return header with { Steps = steps };
    }
}
