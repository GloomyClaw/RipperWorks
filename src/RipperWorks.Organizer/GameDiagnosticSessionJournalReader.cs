using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RipperWorks.Organizer;

public enum GameDiagnosticSessionState
{
    Complete,
    Incomplete,
    Unsupported,
    Malformed
}

public sealed record GameDiagnosticSessionJournal(
    string FilePath,
    string FileName,
    int? FormatVersion,
    string SessionId,
    DateTimeOffset? StartUtc,
    string? StartLocal,
    DateTimeOffset? EndUtc,
    string? EndLocal,
    string? GameRoot,
    string? ExecutablePath,
    string? Result,
    int? ExitCode,
    string? ErrorMessage,
    GameDiagnosticSessionState State,
    bool HasDiagnosticsSection,
    bool HasExplicitCleanDiagnosticsStatement,
    IReadOnlyList<GameDiagnosticEvent> Events,
    IReadOnlyList<string> UnavailableSourceNames,
    string? RawText = null);

public static partial class GameDiagnosticSessionJournalReader
{
    private const string ExpectedMagicHeader = "RipperWorks Game Diagnostic Session";
    private const string CleanDiagnosticsToken = "No warnings or errors were reported by captured diagnostic sources.";

    private static readonly Regex StructuralHeaderRegex = new(
        @"^(?:\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s+)?\[(?<source>[^\]\r\n]+)\]\s+\[(?<severity>[^\]\r\n]+)\]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NativeHeaderRegex = new(
        @"^(?:\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s+)?\[(?<source>RED4ext|ArchiveXL|TweakXL|Cyber Engine Tweaks|redscript)\]\s+\[(?<severity>Warning|Error)\]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static GameDiagnosticSessionJournal ReadFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (!File.Exists(filePath))
        {
            return new GameDiagnosticSessionJournal(
                filePath, fileName, null, string.Empty, null, null, null, null, null, null, null, null,
                "File not found.", GameDiagnosticSessionState.Malformed, false, false, Array.Empty<GameDiagnosticEvent>(), Array.Empty<string>());
        }

        try
        {
            var content = File.ReadAllText(filePath);
            return Read(filePath, content);
        }
        catch (Exception ex)
        {
            return new GameDiagnosticSessionJournal(
                filePath, fileName, null, string.Empty, null, null, null, null, null, null, null, null,
                ex.Message, GameDiagnosticSessionState.Malformed, false, false, Array.Empty<GameDiagnosticEvent>(), Array.Empty<string>());
        }
    }

    public static GameDiagnosticSessionJournal Read(string filePath, string content)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new GameDiagnosticSessionJournal(
                filePath, fileName, null, string.Empty, null, null, null, null, null, null, null, null,
                "Empty session log.", GameDiagnosticSessionState.Malformed, false, false, Array.Empty<GameDiagnosticEvent>(), Array.Empty<string>(), content);
        }

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var firstNonEmpty = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (firstNonEmpty == null || !firstNonEmpty.Trim().Equals(ExpectedMagicHeader, StringComparison.Ordinal))
        {
            return new GameDiagnosticSessionJournal(
                filePath, fileName, null, string.Empty, null, null, null, null, null, null, null, null,
                "Missing RipperWorks magic header.", GameDiagnosticSessionState.Malformed, false, false, Array.Empty<GameDiagnosticEvent>(), Array.Empty<string>(), content);
        }

        int? formatVersion = null;
        string sessionId = string.Empty;
        string? startLocal = null;
        DateTimeOffset? startUtc = null;
        string? endLocal = null;
        DateTimeOffset? endUtc = null;
        string? gameRoot = null;
        string? executable = null;
        string? result = null;
        int? exitCode = null;
        string? error = null;

        var inDiagnostics = false;
        var diagLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.Equals("===== Diagnostics =====", StringComparison.OrdinalIgnoreCase))
            {
                inDiagnostics = true;
                continue;
            }

            if (inDiagnostics)
            {
                diagLines.Add(line);
                continue;
            }

            if (trimmed.StartsWith("FormatVersion:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed.Substring("FormatVersion:".Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    formatVersion = v;
                }
            }
            else if (trimmed.StartsWith("SessionId:", StringComparison.OrdinalIgnoreCase))
            {
                sessionId = trimmed.Substring("SessionId:".Length).Trim();
            }
            else if (trimmed.StartsWith("StartLocal:", StringComparison.OrdinalIgnoreCase))
            {
                startLocal = trimmed.Substring("StartLocal:".Length).Trim();
            }
            else if (trimmed.StartsWith("StartUtc:", StringComparison.OrdinalIgnoreCase))
            {
                var val = trimmed.Substring("StartUtc:".Length).Trim();
                if (DateTimeOffset.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                {
                    startUtc = dt;
                }
            }
            else if (trimmed.StartsWith("EndLocal:", StringComparison.OrdinalIgnoreCase))
            {
                endLocal = trimmed.Substring("EndLocal:".Length).Trim();
            }
            else if (trimmed.StartsWith("EndUtc:", StringComparison.OrdinalIgnoreCase))
            {
                var val = trimmed.Substring("EndUtc:".Length).Trim();
                if (DateTimeOffset.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                {
                    endUtc = dt;
                }
            }
            else if (trimmed.StartsWith("GameRoot:", StringComparison.OrdinalIgnoreCase))
            {
                gameRoot = trimmed.Substring("GameRoot:".Length).Trim();
            }
            else if (trimmed.StartsWith("Executable:", StringComparison.OrdinalIgnoreCase))
            {
                executable = trimmed.Substring("Executable:".Length).Trim();
            }
            else if (trimmed.StartsWith("Result:", StringComparison.OrdinalIgnoreCase))
            {
                result = trimmed.Substring("Result:".Length).Trim();
            }
            else if (trimmed.StartsWith("ExitCode:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed.Substring("ExitCode:".Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                {
                    exitCode = code;
                }
            }
            else if (trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                error = trimmed.Substring("Error:".Length).Trim();
            }
        }

        GameDiagnosticSessionState state;
        if (!formatVersion.HasValue || string.IsNullOrWhiteSpace(sessionId))
        {
            state = GameDiagnosticSessionState.Malformed;
        }
        else if (formatVersion.Value != 1)
        {
            state = GameDiagnosticSessionState.Unsupported;
        }
        else if (!string.IsNullOrWhiteSpace(result))
        {
            state = GameDiagnosticSessionState.Complete;
        }
        else
        {
            state = GameDiagnosticSessionState.Incomplete;
        }

        var events = new List<GameDiagnosticEvent>();
        var unavailableSources = new List<string>();
        var hasExplicitClean = false;

        if (state == GameDiagnosticSessionState.Malformed || state == GameDiagnosticSessionState.Unsupported)
        {
            return new GameDiagnosticSessionJournal(
                filePath, fileName, formatVersion, sessionId, startUtc, startLocal, endUtc, endLocal,
                gameRoot, executable, result, exitCode, error, state, inDiagnostics, false, events, unavailableSources, content);
        }

        TimeSpan? sessionOffset = null;
        if (!string.IsNullOrWhiteSpace(startLocal) && startUtc.HasValue &&
            DateTime.TryParseExact(startLocal, new[] { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sldt))
        {
            sessionOffset = sldt - startUtc.Value.UtcDateTime;
        }

        if (inDiagnostics)
        {
            (events, unavailableSources, hasExplicitClean) = ParseDiagnosticsSection(sessionId, diagLines, sessionOffset);
        }

        return new GameDiagnosticSessionJournal(
            filePath, fileName, formatVersion, sessionId, startUtc, startLocal, endUtc, endLocal,
            gameRoot, executable, result, exitCode, error, state, inDiagnostics, hasExplicitClean, events, unavailableSources, content);
    }

    private static (List<GameDiagnosticEvent> Events, List<string> UnavailableSources, bool HasExplicitClean) ParseDiagnosticsSection(
        string sessionId,
        List<string> lines,
        TimeSpan? sessionOffset)
    {
        var events = new List<GameDiagnosticEvent>();
        var unavailableSources = new List<string>();
        var hasExplicitClean = false;
        var block = new List<string>();

        void FlushBlock()
        {
            if (block.Count == 0) return;
            if (ParseDiagnosticBlock(sessionId, block, events, unavailableSources, sessionOffset))
            {
                hasExplicitClean = true;
            }
            block.Clear();
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                FlushBlock();
                continue;
            }

            if (trimmed.Equals(CleanDiagnosticsToken, StringComparison.OrdinalIgnoreCase))
            {
                FlushBlock();
                hasExplicitClean = true;
                continue;
            }

            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                (trimmed.StartsWith("[Cyberpunk 2077] [CrashReportCreated]", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("[RipperWorks] Diagnostic source unavailable:", StringComparison.OrdinalIgnoreCase) ||
                 StructuralHeaderRegex.IsMatch(trimmed)))
            {
                FlushBlock();
            }

            block.Add(rawLine);
        }

        FlushBlock();
        return (events, unavailableSources, hasExplicitClean);
    }

    private static bool ParseDiagnosticBlock(
        string sessionId,
        List<string> blockLines,
        List<GameDiagnosticEvent> events,
        List<string> unavailableSources,
        TimeSpan? sessionOffset)
    {
        if (blockLines.Count == 0) return false;
        var header = blockLines[0].Trim();

        if (header.Equals(CleanDiagnosticsToken, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (header.StartsWith("[RipperWorks] Diagnostic source unavailable:", StringComparison.OrdinalIgnoreCase))
        {
            var sourceName = header.Substring("[RipperWorks] Diagnostic source unavailable:".Length).Trim();
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                unavailableSources.Add(sourceName);
            }
            return false;
        }

        if (header.StartsWith("[Cyberpunk 2077] [CrashReportCreated]", StringComparison.OrdinalIgnoreCase))
        {
            string? reportPath = null;
            string? fileContext = null;
            var rawLines = new List<string>();

            for (var i = 1; i < blockLines.Count; i++)
            {
                var line = blockLines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Report:", StringComparison.OrdinalIgnoreCase))
                {
                    reportPath = trimmed.Substring("Report:".Length).Trim();
                }
                else
                {
                    rawLines.Add(line);
                    if (trimmed.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                    {
                        fileContext = trimmed.Substring("File:".Length).Trim();
                    }
                }
            }

            var rawBlock = rawLines.Count > 0 ? string.Join(Environment.NewLine, rawLines) : null;
            var context = fileContext ?? (reportPath is not null ? Path.GetFileName(reportPath) : null);

            events.Add(new GameDiagnosticEvent(
                sessionId,
                "REDengine",
                "Cyberpunk 2077",
                null,
                null,
                null,
                events.Count + 1,
                "Cyberpunk 2077 created a crash report.",
                rawBlock,
                context,
                reportPath ?? string.Empty,
                GameDiagnosticEventKind.CrashReportCreated));
            return false;
        }

        var match = NativeHeaderRegex.Match(header);
        if (!match.Success) return false;

        var timeStr = match.Groups["time"].Value;
        var sourceNameText = match.Groups["source"].Value.Trim();
        var severityStr = match.Groups["severity"].Value.Trim();

        var sourceId = MapSourceNameToId(sourceNameText);
        var severity = MapNativeSeverity(severityStr);

        if (sourceId == null || severity == null)
        {
            return false;
        }

        DateTimeOffset? eventTime = null;
        if (!string.IsNullOrWhiteSpace(timeStr) &&
            DateTime.TryParseExact(timeStr, new[] { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var evDt))
        {
            var offset = sessionOffset ?? TimeZoneInfo.Local.GetUtcOffset(evDt);
            eventTime = new DateTimeOffset(evDt, offset);
        }

        string? contextLine = null;
        string? codeLine = null;
        string? sourceLogPath = null;
        var messageLines = new List<string>();

        for (var i = 1; i < blockLines.Count; i++)
        {
            var line = blockLines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("At:", StringComparison.OrdinalIgnoreCase))
            {
                contextLine = trimmed.Substring("At:".Length).Trim();
            }
            else if (trimmed.StartsWith("Code:", StringComparison.OrdinalIgnoreCase))
            {
                codeLine = trimmed.Substring("Code:".Length).Trim();
            }
            else if (trimmed.StartsWith("Source log:", StringComparison.OrdinalIgnoreCase))
            {
                sourceLogPath = trimmed.Substring("Source log:".Length).Trim();
            }
            else
            {
                messageLines.Add(line);
            }
        }

        var message = string.Join(Environment.NewLine, messageLines).Trim();
        var rawAllBlock = string.Join(Environment.NewLine, blockLines);

        events.Add(new GameDiagnosticEvent(
            sessionId,
            sourceId,
            sourceNameText,
            severityStr,
            severity,
            eventTime,
            events.Count + 1,
            message,
            rawAllBlock,
            contextLine,
            sourceLogPath ?? string.Empty,
            GameDiagnosticEventKind.NativeDiagnostic,
            codeLine));
        return false;
    }

    private static string? MapSourceNameToId(string sourceName)
    {
        return sourceName switch
        {
            "RED4ext" => "RED4ext",
            "ArchiveXL" => "ArchiveXL",
            "TweakXL" => "TweakXL",
            "Cyber Engine Tweaks" => "CyberEngineTweaks",
            "redscript" => "redscript",
            _ => null
        };
    }

    private static GameDiagnosticSeverity? MapNativeSeverity(string severityStr)
    {
        if (string.IsNullOrWhiteSpace(severityStr)) return null;

        if (severityStr.Equals("Warning", StringComparison.OrdinalIgnoreCase))
        {
            return GameDiagnosticSeverity.Warning;
        }

        if (severityStr.Equals("Error", StringComparison.OrdinalIgnoreCase))
        {
            return GameDiagnosticSeverity.Error;
        }

        return null;
    }
}
