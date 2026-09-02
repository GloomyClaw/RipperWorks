using System;
using System.Collections.Generic;

namespace RipperWorks.Organizer;

public enum DiagnosticMutationKind
{
    Unchanged,
    Created,
    Appended,
    ReplacedOrTruncated,
    Deleted,
    Unavailable
}

public enum DiagnosticCaptureStatus
{
    Success,
    Missing,
    Unavailable,
    CaptureError
}

public sealed record DiagnosticFileSnapshot(
    string FullPath,
    bool Exists,
    long Length,
    DateTimeOffset? CreationTimeUtc,
    DateTimeOffset? LastWriteUtc,
    string? HeadHash,
    int HeadLength,
    string? TailHash,
    int TailLength,
    bool FingerprintAvailable);

public sealed record DiagnosticReportDirectory(
    string DirectoryPath,
    string DirectoryName,
    DateTimeOffset? CreationTimeUtc,
    DateTimeOffset? LastWriteUtc);

public sealed record DiagnosticSourceSnapshot(
    string SourceId,
    string SourceName,
    DiagnosticCaptureStatus Status,
    IReadOnlyList<DiagnosticFileSnapshot> Files,
    IReadOnlyList<string> ExistingReportDirectories);

public sealed record DiagnosticSessionSnapshot(
    string SessionId,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<DiagnosticSourceSnapshot> Sources);

public sealed record DiagnosticFileDelta(
    string FullPath,
    DiagnosticMutationKind MutationKind,
    long StartOffset,
    long Length);

public sealed record DiagnosticSourceDelta(
    string SourceId,
    string SourceName,
    DiagnosticCaptureStatus CaptureStatus,
    IReadOnlyList<DiagnosticFileDelta> FileDeltas,
    IReadOnlyList<DiagnosticReportDirectory> NewReportDirectories);

public sealed record GameDiagnosticCaptureResult(
    string SessionId,
    IReadOnlyList<string> ActiveSourceIds,
    IReadOnlyList<DiagnosticSourceDelta> SourceDeltas);

public sealed record InstalledNexusModIdentity(string GameDomain, long ModId)
{
    public bool Matches(string gameDomain, long modId) =>
        ModId == modId && string.Equals(GameDomain, gameDomain, StringComparison.OrdinalIgnoreCase);
}

public sealed record InstalledFrameworkState(
    IReadOnlySet<string> InstalledRelativeGamePaths,
    IReadOnlySet<InstalledNexusModIdentity> InstalledNexusIdentities)
{
    public static readonly InstalledFrameworkState Empty =
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<InstalledNexusModIdentity>());
}

public interface IInstalledFrameworkStateProvider
{
    Task<InstalledFrameworkState> GetInstalledFrameworkStateAsync(
        CancellationToken cancellationToken = default);
}

public interface IGameDiagnosticCaptureService
{
    Task<DiagnosticSessionSnapshot> CapturePreLaunchSnapshotAsync(
        Core.GameProfileRecord profile,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<GameDiagnosticCaptureResult> CapturePostSessionSnapshotAsync(
        Core.GameProfileRecord profile,
        DiagnosticSessionSnapshot preLaunchSnapshot,
        CancellationToken cancellationToken = default);
}
