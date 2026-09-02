using System.Diagnostics;
using System.Text;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed class SystemGameProcessHandle(Process process) : IGameProcessHandle
{
    public int Id => process.Id;

    public int? ExitCode
    {
        get
        {
            try
            {
                return process.HasExited ? process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        process.WaitForExitAsync(cancellationToken);

    public void Dispose()
    {
        try
        {
            process.Dispose();
        }
        catch
        {
        }
    }
}

public sealed class SystemGameProcessAdapter : IGameProcessAdapter
{
    public bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName(
            Cyberpunk2077Profile.ProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public IGameProcessHandle? Start(string executablePath, string workingDirectory)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        });
        return process is not null ? new SystemGameProcessHandle(process) : null;
    }
}

public sealed class GameLauncherService(
    IGameProcessAdapter processAdapter,
    string? logsDirectory = null,
    IGameDiagnosticCaptureService? diagnosticCaptureService = null,
    IGameDiagnosticParserService? diagnosticParserService = null) : IAsyncDisposable
{
    private static readonly TimeSpan RapidLaunchCooldown =
        TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly object _trackersGate = new();
    private readonly List<Task> _activeTrackers = [];
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _isLaunching;
    private bool _disposed;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public GameDiagnosticCaptureResult? LastCaptureResult { get; private set; }
    public GameDiagnosticParseResult? LastSessionDiagnostics { get; private set; }

    public bool CanLaunch(GameProfileRecord? profile)
    {
        if (profile is not
            {
                ValidationState: GameProfileValidationState.Valid
            } ||
            !File.Exists(profile.ExecutablePath) ||
            processAdapter.IsGameRunning())
        {
            return false;
        }
        lock (_gate)
        {
            return !_isLaunching &&
                DateTime.UtcNow - _lastAttemptUtc >= RapidLaunchCooldown;
        }
    }

    public async Task<GameLaunchResult> LaunchAsync(
        GameProfileRecord? profile,
        CancellationToken cancellationToken = default)
    {
        if (profile is null ||
            profile.ValidationState != GameProfileValidationState.Valid)
        {
            return new GameLaunchResult(GameLaunchStatus.ProfileMissing);
        }
        if (!File.Exists(profile.ExecutablePath))
        {
            return new GameLaunchResult(GameLaunchStatus.ExecutableMissing);
        }
        if (processAdapter.IsGameRunning())
        {
            return new GameLaunchResult(GameLaunchStatus.AlreadyRunning);
        }

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_isLaunching ||
                now - _lastAttemptUtc < RapidLaunchCooldown)
            {
                return new GameLaunchResult(
                    GameLaunchStatus.AlreadyLaunching);
            }
            _isLaunching = true;
            _lastAttemptUtc = now;
        }

        string? journalPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (path, sessionId) = InitializeSessionJournal(profile);
            journalPath = path;

            DiagnosticSessionSnapshot? preSnapshot = null;
            if (diagnosticCaptureService is not null && !string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    preSnapshot = await diagnosticCaptureService.CapturePreLaunchSnapshotAsync(
                        profile,
                        sessionId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Fail-soft: diagnostic capture failure must never block launch
                }
            }

            var handle = processAdapter.Start(
                profile.ExecutablePath,
                Path.GetDirectoryName(profile.ExecutablePath)!);

            if (handle is null)
            {
                RecordLaunchFailed(journalPath, "Process.Start returned null.");
                return new GameLaunchResult(GameLaunchStatus.Failed);
            }

            RecordProcessStarted(journalPath);
            StartTrackingProcessLifetime(handle, profile, journalPath, preSnapshot);

            return new GameLaunchResult(GameLaunchStatus.Started);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            RecordLaunchFailed(journalPath, exception.Message);
            return new GameLaunchResult(
                GameLaunchStatus.Failed,
                exception.Message);
        }
        finally
        {
            lock (_gate)
                _isLaunching = false;
        }
    }

    private (string? filePath, string? sessionId) InitializeSessionJournal(GameProfileRecord profile)
    {
        if (string.IsNullOrWhiteSpace(logsDirectory))
            return (null, null);

        Directory.CreateDirectory(logsDirectory);
        var nowLocal = DateTime.Now;
        var nowUtc = DateTime.UtcNow;
        var sessionId = $"{nowLocal:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}";
        var fileName = $"game-session-{nowLocal:yyyyMMdd-HHmmss-fff}.log";
        var filePath = Path.Combine(logsDirectory, fileName);

        if (File.Exists(filePath))
        {
            fileName = $"game-session-{sessionId}.log";
            filePath = Path.Combine(logsDirectory, fileName);
        }

        var builder = new StringBuilder();
        builder.AppendLine("RipperWorks Game Diagnostic Session");
        builder.AppendLine("FormatVersion: 1");
        builder.AppendLine($"SessionId: {sessionId}");
        builder.AppendLine();
        builder.AppendLine($"StartLocal: {nowLocal:yyyy-MM-dd HH:mm:ss.fff}");
        builder.AppendLine($"StartUtc: {nowUtc:yyyy-MM-ddTHH:mm:ss.fffZ}");
        builder.AppendLine();
        builder.AppendLine($"GameRoot: {profile.GameRoot}");
        builder.AppendLine($"Executable: {profile.ExecutablePath}");
        builder.AppendLine();
        builder.AppendLine($"[{nowLocal:HH:mm:ss.fff}] [RipperWorks] Launch requested.");
        builder.AppendLine();

        File.WriteAllText(filePath, builder.ToString(), Encoding.UTF8);
        return (filePath, sessionId);
    }

    private static void RecordProcessStarted(string? journalPath)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
            return;

        var nowLocal = DateTime.Now;
        var nowUtc = DateTime.UtcNow;
        var builder = new StringBuilder();
        builder.AppendLine($"ProcessStartedLocal: {nowLocal:yyyy-MM-dd HH:mm:ss.fff}");
        builder.AppendLine($"ProcessStartedUtc: {nowUtc:yyyy-MM-ddTHH:mm:ss.fffZ}");
        builder.AppendLine();
        builder.AppendLine($"[{nowLocal:HH:mm:ss.fff}] [RipperWorks] Cyberpunk 2077 process started.");
        builder.AppendLine();

        AppendToFileWithRetry(journalPath, builder.ToString());
    }

    private static void RecordProcessExited(string? journalPath, int? exitCode)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
            return;

        var nowLocal = DateTime.Now;
        var nowUtc = DateTime.UtcNow;
        var builder = new StringBuilder();
        builder.AppendLine($"EndLocal: {nowLocal:yyyy-MM-dd HH:mm:ss.fff}");
        builder.AppendLine($"EndUtc: {nowUtc:yyyy-MM-ddTHH:mm:ss.fffZ}");
        builder.AppendLine();
        builder.AppendLine("Result: Exited");
        if (exitCode.HasValue)
        {
            builder.AppendLine($"ExitCode: {exitCode.Value}");
        }
        builder.AppendLine();
        builder.AppendLine($"[{nowLocal:HH:mm:ss.fff}] [RipperWorks] Cyberpunk 2077 process exited.");
        builder.AppendLine();

        AppendToFileWithRetry(journalPath, builder.ToString());
    }

    private static void RecordLaunchFailed(string? journalPath, string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
            return;

        var nowLocal = DateTime.Now;
        var nowUtc = DateTime.UtcNow;
        var builder = new StringBuilder();
        builder.AppendLine($"EndLocal: {nowLocal:yyyy-MM-dd HH:mm:ss.fff}");
        builder.AppendLine($"EndUtc: {nowUtc:yyyy-MM-ddTHH:mm:ss.fffZ}");
        builder.AppendLine();
        builder.AppendLine("Result: LaunchFailed");
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            builder.AppendLine($"Error: {errorMessage.Trim()}");
        }
        builder.AppendLine();

        AppendToFileWithRetry(journalPath, builder.ToString());
    }

    private static void AppendToFileWithRetry(string? path, string content)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                stream.Seek(0, SeekOrigin.End);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(content);
                writer.Flush();
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(20);
            }
            catch
            {
                return;
            }
        }
    }

    public static void AppendDiagnosticsToJournal(string? journalPath, string? diagnosticsSection)
    {
        if (string.IsNullOrWhiteSpace(journalPath) || string.IsNullOrWhiteSpace(diagnosticsSection))
            return;

        try
        {
            if (!File.Exists(journalPath))
                return;

            var existingText = File.ReadAllText(journalPath);
            if (existingText.Contains("===== Diagnostics ====="))
                return;

            AppendToFileWithRetry(journalPath, diagnosticsSection);
        }
        catch
        {
            // Fail-soft
        }
    }

    private void StartTrackingProcessLifetime(
        IGameProcessHandle handle,
        GameProfileRecord profile,
        string? journalPath,
        DiagnosticSessionSnapshot? preSnapshot)
    {
        lock (_trackersGate)
        {
            if (_disposed)
            {
                handle.Dispose();
                return;
            }

            _activeTrackers.RemoveAll(t => t.IsCompleted);
            var trackingTask = TrackProcessLifetimeAsync(
                handle,
                profile,
                journalPath,
                preSnapshot,
                _shutdownCts.Token);
            _activeTrackers.Add(trackingTask);
        }
    }

    private async Task TrackProcessLifetimeAsync(
        IGameProcessHandle handle,
        GameProfileRecord profile,
        string? journalPath,
        DiagnosticSessionSnapshot? preSnapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await handle.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            int? exitCode = handle.ExitCode;
            RecordProcessExited(journalPath, exitCode);

            if (diagnosticCaptureService is not null && preSnapshot is not null)
            {
                try
                {
                    LastCaptureResult = await diagnosticCaptureService.CapturePostSessionSnapshotAsync(
                        profile,
                        preSnapshot,
                        cancellationToken).ConfigureAwait(false);

                    if (LastCaptureResult is not null && diagnosticParserService is not null)
                    {
                        var parsedDiagnostics = await diagnosticParserService.ParseDiagnosticsAsync(
                            profile,
                            LastCaptureResult,
                            cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(parsedDiagnostics.FormattedJournalSection) && journalPath is not null)
                        {
                            AppendDiagnosticsToJournal(journalPath, parsedDiagnostics.FormattedJournalSection);
                        }

                        LastSessionDiagnostics = parsedDiagnostics;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Fail-soft: diagnostic capture failure must never disrupt exit flow
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown was requested while process was still running.
            // Incomplete session evidence: do NOT record Exited/Crash.
        }
        catch
        {
        }
        finally
        {
            handle.Dispose();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _shutdownCts.Cancel();
        Task[] pending;
        lock (_trackersGate)
        {
            pending = [.. _activeTrackers];
        }

        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAny(
                    Task.WhenAll(pending),
                    Task.Delay(500, cancellationToken)).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_trackersGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
    }
}
