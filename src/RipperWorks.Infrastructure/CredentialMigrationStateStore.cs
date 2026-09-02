using System.Text.Json;

namespace RipperWorks.Infrastructure;

/// <summary>
/// Marker file persistence and validation for credential migration.
/// Does not perform decrypt/delete of credential blobs.
/// </summary>
internal sealed class CredentialMigrationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _migrationStatePath;

    public CredentialMigrationStateStore(string migrationStatePath) =>
        _migrationStatePath = migrationStatePath;

    public async Task<MigrationStateRead> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_migrationStatePath))
            return MigrationStateRead.Missing;

        try
        {
            await using var stream = File.OpenRead(_migrationStatePath);
            var state = await JsonSerializer.DeserializeAsync<MigrationState>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (state is null)
            {
                return MigrationStateRead.Blocked(
                    "MarkerCorrupt",
                    "Deserialized null migration state.");
            }

            if (!string.Equals(
                    state.MigrationId,
                    DpapiProtectedCredentialStore.MigrationId,
                    StringComparison.Ordinal))
            {
                return MigrationStateRead.Blocked(
                    "UnknownMigrationId",
                    state.MigrationId);
            }

            if (string.IsNullOrWhiteSpace(state.Status) ||
                !CredentialMigrationStatuses.All.Contains(state.Status))
            {
                return MigrationStateRead.Blocked(
                    "UnknownStatus",
                    state.Status);
            }

            if (!string.IsNullOrWhiteSpace(state.Phase) &&
                !CredentialMigrationPhases.All.Contains(state.Phase))
            {
                return MigrationStateRead.Blocked(
                    "UnknownPhase",
                    state.Phase);
            }

            return MigrationStateRead.Valid(state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return MigrationStateRead.Blocked(
                "MarkerCorrupt",
                "JSON parse failed.");
        }
        catch (IOException ex)
        {
            return MigrationStateRead.Blocked(
                "MarkerUnreadable",
                ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            return MigrationStateRead.Blocked(
                "MarkerUnreadable",
                ex.GetType().Name);
        }
    }

    public async Task WriteAsync(
        string status,
        string phase,
        string? currentHash,
        string? legacyHash,
        string? failureKind,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task> hitBarrier)
    {
        await hitBarrier(
                CredentialMigrationPhases.BeforeMarker,
                cancellationToken)
            .ConfigureAwait(false);

        Directory.CreateDirectory(
            Path.GetDirectoryName(_migrationStatePath)!);
        var state = new MigrationState
        {
            MigrationId = DpapiProtectedCredentialStore.MigrationId,
            Status = status,
            Phase = phase,
            AppliedUtc = DateTime.UtcNow,
            CurrentCiphertextHash = currentHash,
            LegacyCiphertextHash = legacyHash,
            FailureKind = failureKind
        };
        var temporary = _migrationStatePath + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _migrationStatePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            DpapiCredentialBlobOperations.DeleteIfExists(temporary);
            throw;
        }

        await hitBarrier(
                CredentialMigrationPhases.AfterMarker,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class MigrationState
{
    public string MigrationId { get; set; } =
        DpapiProtectedCredentialStore.MigrationId;
    public string Status { get; set; } = CredentialMigrationStatuses.Pending;
    public string? Phase { get; set; }
    public DateTime? AppliedUtc { get; set; }
    public string? CurrentCiphertextHash { get; set; }
    public string? LegacyCiphertextHash { get; set; }
    public string? FailureKind { get; set; }
}

internal sealed class MigrationStateRead
{
    private MigrationStateRead(
        bool isMissing,
        bool isBlocked,
        MigrationState? state,
        string? failureKind)
    {
        IsMissing = isMissing;
        IsBlocked = isBlocked;
        State = state;
        FailureKind = failureKind;
    }

    public bool IsMissing { get; }
    public bool IsBlocked { get; }
    public MigrationState? State { get; }
    public string? FailureKind { get; }

    public static MigrationStateRead Missing { get; } =
        new(true, false, null, null);

    public static MigrationStateRead Valid(MigrationState state) =>
        new(false, false, state, null);

    public static MigrationStateRead Blocked(
        string failureKind,
        string? detail) =>
        new(false, true, null, failureKind);
}
