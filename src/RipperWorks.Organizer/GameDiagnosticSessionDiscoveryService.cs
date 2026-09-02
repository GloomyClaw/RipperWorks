using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RipperWorks.Organizer;

public interface IGameDiagnosticSessionDiscoveryService
{
    IReadOnlyList<GameDiagnosticSessionJournal> DiscoverSessions(string logsDirectory);
    Task<IReadOnlyList<GameDiagnosticSessionJournal>> DiscoverSessionsAsync(string logsDirectory, CancellationToken cancellationToken = default);
}

public sealed class GameDiagnosticSessionDiscoveryService : IGameDiagnosticSessionDiscoveryService
{
    private readonly TimeZoneInfo? _timeZone;

    public GameDiagnosticSessionDiscoveryService(TimeZoneInfo? timeZone = null)
    {
        _timeZone = timeZone;
    }

    public IReadOnlyList<GameDiagnosticSessionJournal> DiscoverSessions(string logsDirectory)
    {
        return DiscoverSessionsInternal(logsDirectory, _timeZone, CancellationToken.None);
    }

    public Task<IReadOnlyList<GameDiagnosticSessionJournal>> DiscoverSessionsAsync(string logsDirectory, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DiscoverSessionsInternal(logsDirectory, _timeZone, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<GameDiagnosticSessionJournal> DiscoverSessionsInternal(
        string logsDirectory,
        TimeZoneInfo? timeZone,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logsDirectory) || !Directory.Exists(logsDirectory))
        {
            return Array.Empty<GameDiagnosticSessionJournal>();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = Directory.GetFiles(logsDirectory, "game-session-*.log", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                return Array.Empty<GameDiagnosticSessionJournal>();
            }

            var sessions = new List<GameDiagnosticSessionJournal>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var journal = GameDiagnosticSessionJournalReader.ReadFile(file);
                    sessions.Add(journal);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Fail-soft: continue discovering remaining sessions
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return sessions
                .OrderByDescending(j => GetSortKey(j, timeZone))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<GameDiagnosticSessionJournal>();
        }
    }

    public static DateTimeOffset GetSortKey(GameDiagnosticSessionJournal journal, TimeZoneInfo? timeZone = null)
    {
        if (journal.StartUtc.HasValue)
        {
            return journal.StartUtc.Value;
        }

        var tz = timeZone ?? TimeZoneInfo.Local;

        // Fallback 1: extract local timestamp from filename: game-session-YYYYMMDD-HHmmss-fff.log
        var name = Path.GetFileNameWithoutExtension(journal.FilePath);
        if (name.StartsWith("game-session-", StringComparison.OrdinalIgnoreCase))
        {
            var raw = name.Substring("game-session-".Length);
            if (raw.Length >= 19 &&
                DateTime.TryParseExact(raw.Substring(0, 19), "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                var offset = tz.GetUtcOffset(dt);
                return new DateTimeOffset(dt, offset);
            }

            if (raw.Length >= 15 &&
                DateTime.TryParseExact(raw.Substring(0, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtShort))
            {
                var offset = tz.GetUtcOffset(dtShort);
                return new DateTimeOffset(dtShort, offset);
            }
        }

        // Fallback 2: file LastWriteTimeUtc
        try
        {
            if (File.Exists(journal.FilePath))
            {
                return new DateTimeOffset(File.GetLastWriteTimeUtc(journal.FilePath), TimeSpan.Zero);
            }
        }
        catch { }

        return DateTimeOffset.MinValue;
    }
}
