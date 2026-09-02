namespace RipperWorks.Core;

public static class Cyberpunk2077Profile
{
    public const string ExecutableRelativePath =
        @"bin\x64\Cyberpunk2077.exe";
    public const string ProcessName = "Cyberpunk2077";

    public static string GetExecutablePath(string gameRoot) =>
        Path.Combine(
            gameRoot,
            "bin",
            "x64",
            "Cyberpunk2077.exe");
}

public enum GameProfileValidationState
{
    NotValidated = 0,
    Valid = 1,
    Invalid = 2
}

public static class GameProfileValidationCodes
{
    public const string GameFolderNotFound = "GameFolderNotFound";
    public const string GameExecutableNotFound = "GameExecutableNotFound";
    public const string GameExecutableUnreadable = "GameExecutableUnreadable";
    public const string PathsOverlap = "PathsOverlap";
    public const string GameRunning = "GameRunning";
    public const string GameFolderNotWritable = "GameFolderNotWritable";
    public const string ForeignFilesFound = "ForeignFilesFound";
    public const string ValidationFailed = "ValidationFailed";
}

public sealed record GameProfileRecord
{
    public required string GameRoot { get; init; }
    public required string ExecutablePath { get; init; }
    public DateTime InitializedAtUtc { get; init; }
    public DateTime LastValidatedAtUtc { get; init; }
    public GameProfileValidationState ValidationState { get; init; }
}

public sealed record GameProfileValidationResult
{
    public bool IsValid { get; init; }
    public string GameRoot { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public string? ErrorDetail { get; init; }
    public bool GameFolderFound { get; init; }
    public bool ExecutableFound { get; init; }
    public bool WriteAccessConfirmed { get; init; }
    public bool NoForeignModifications { get; init; }
    public IReadOnlyList<string> ForeignFiles { get; init; } = [];
}

public enum GameLaunchStatus
{
    Started,
    ProfileMissing,
    ExecutableMissing,
    AlreadyRunning,
    AlreadyLaunching,
    Failed
}

public sealed record GameLaunchResult(
    GameLaunchStatus Status,
    string? ErrorMessage = null);

public interface IGameProcessHandle : IDisposable
{
    int Id { get; }
    int? ExitCode { get; }
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

public interface IGameProcessAdapter
{
    bool IsGameRunning();

    IGameProcessHandle? Start(string executablePath, string workingDirectory);
}

public enum InstallPlanAction
{
    Add,
    ReplaceExisting,
    Conflict,
    Blocked,
    AlreadyInstalled,
    OverlayMod
}

public sealed record InstallPlanEntry
{
    public required string RelativeGamePath { get; init; }
    public InstallPlanAction Action { get; init; }
    public string? Reason { get; init; }
    public PackageId? OwnerPackageId { get; init; }
    public string? OwnerDisplayName { get; init; }
}

public sealed record InstallPlan
{
    public required PackageId PackageId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorDetail { get; init; }
    /// <summary>
    /// RF-05: expected content SHA-256 (64 lowercase hex) bound to this plan.
    /// </summary>
    public string? ExpectedArchiveSha256 { get; init; }
    public int? ExpectedAnalyzerVersion { get; init; }
    public int? ExpectedPolicyVersion { get; init; }
    public string? ExpectedSelectedRoot { get; init; }
    public IReadOnlyList<InstallPlanEntry> Entries { get; init; } = [];
    public int AddCount =>
        Entries.Count(entry => entry.Action == InstallPlanAction.Add);
    public int ReplaceExistingCount =>
        Entries.Count(entry =>
            entry.Action == InstallPlanAction.ReplaceExisting);
    public int ConflictCount =>
        Entries.Count(entry => entry.Action == InstallPlanAction.Conflict);
    public int BlockedCount =>
        Entries.Count(entry => entry.Action == InstallPlanAction.Blocked);
    public int OverlayCount =>
        Entries.Count(entry => entry.Action == InstallPlanAction.OverlayMod);
}
