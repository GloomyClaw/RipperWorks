using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace RipperWorks.Organizer;

public static partial class GameDiagnosticSourceParsers
{
    // RED4ext: [2026-08-06 19:38:34.611] [info    ] [ 23428] [RED4ext] message
    private static readonly Regex Red4ExtRegex = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]\s*\[(?<level>trace|debug|info|warning|warn|error|critical|fatal)\s*\]\s*\[\s*\d+\s*\]\s*\[RED4ext\]\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // RedFileSystem: [2026-08-06 19:38:41.496] [info    ] [ 23428] [RedFileSystem] message
    private static readonly Regex RedFileSystemRegex = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]\s*\[(?<level>trace|debug|info|warning|warn|error|critical|fatal)\s*\]\s*\[\s*\d+\s*\]\s*\[RedFileSystem\]\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ArchiveXL: [2026-08-06 19:38:47.345] [23428] [error] message
    // and [2026-08-06 19:39:33.582] [23428] [warning] [Localization] message
    private static readonly Regex ArchiveXlRegex = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]\s*\[\s*\d+\s*\]\s*\[(?<level>trace|debug|info|warning|warn|error|critical|fatal)\]\s*(?:\[(?<subsystem>[^\]]+)\]\s*)?(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // TweakXL: [2026-08-06 19:39:59.136] [16968] [error] Cannot modify ...
    private static readonly Regex TweakXlRegex = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]\s*\[\s*\d+\s*\]\s*\[(?<level>trace|debug|info|warning|warn|error|critical|fatal)\]\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Codeware: [2026-08-06 19:38:39.553] [23428] [info] Codeware ...
    private static readonly Regex CodewareRegex = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]\s*\[\s*\d+\s*\]\s*\[(?<level>trace|debug|info|warning|warn|error|critical|fatal)\]\s*(?:\[(?<subsystem>[^\]]+)\]\s*)?(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // CET main: [2026-08-05 11:31:29 UTC+10:00] [info] [__cdecl Options::Options(struct Paths &)] [28532] message
    private static readonly Regex CetMainRegex = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?\s+UTC[+-]\d{1,2}:\d{2})\]\s*\[(?<level>trace|debug|info|warning|warn|error|critical)\]\s*(?:\[(?<func>[^\]]+)\]\s*)?\[\s*\d+\s*\]\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IReadOnlyList<GameDiagnosticEvent> ParseRed4Ext(
        string sessionId,
        string sourceId,
        string sourceName,
        string filePath,
        string text)
    {
        var events = new List<GameDiagnosticEvent>();
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            var match = Red4ExtRegex.Match(line);
            if (!match.Success) continue;

            var levelStr = match.Groups["level"].Value.Trim();
            var severity = TryMapSupportedSeverity(sourceId, levelStr);
            if (!severity.HasValue) continue;

            var timestamp = TryParseTimestamp(match.Groups["time"].Value);
            var message = match.Groups["message"].Value.Trim();

            events.Add(new GameDiagnosticEvent(
                SessionId: sessionId,
                SourceId: sourceId,
                SourceName: sourceName,
                NativeSeverityText: levelStr,
                Severity: severity,
                EventTimestamp: timestamp,
                ObservedSequence: 0,
                Message: message,
                RawBlock: line,
                Context: null,
                SourcePath: filePath,
                EventKind: GameDiagnosticEventKind.NativeDiagnostic));
        }

        return events;
    }

    private static IReadOnlyList<GameDiagnosticEvent> ParseRedFileSystem(
        string sessionId,
        string sourceId,
        string sourceName,
        string filePath,
        string text)
    {
        var events = new List<GameDiagnosticEvent>();
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            var match = RedFileSystemRegex.Match(line);
            if (!match.Success) continue;

            var levelStr = match.Groups["level"].Value.Trim();
            var severity = TryMapSupportedSeverity(sourceId, levelStr);
            if (!severity.HasValue) continue;

            var timestamp = TryParseTimestamp(match.Groups["time"].Value);
            var message = match.Groups["message"].Value.Trim();

            events.Add(new GameDiagnosticEvent(
                SessionId: sessionId,
                SourceId: sourceId,
                SourceName: sourceName,
                NativeSeverityText: levelStr,
                Severity: severity,
                EventTimestamp: timestamp,
                ObservedSequence: 0,
                Message: message,
                RawBlock: line,
                Context: null,
                SourcePath: filePath,
                EventKind: GameDiagnosticEventKind.NativeDiagnostic));
        }

        return events;
    }

    private static IReadOnlyList<GameDiagnosticEvent> ParseArchiveXl(
        string sessionId,
        string sourceId,
        string sourceName,
        string filePath,
        string text)
    {
        var events = new List<GameDiagnosticEvent>();
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            var match = ArchiveXlRegex.Match(line);
            if (!match.Success) continue;

            var levelStr = match.Groups["level"].Value.Trim();
            var severity = TryMapSupportedSeverity(sourceId, levelStr);
            if (!severity.HasValue) continue;

            var timestamp = TryParseTimestamp(match.Groups["time"].Value);
            var subsystem = match.Groups["subsystem"].Value.Trim();
            var message = match.Groups["message"].Value.Trim();

            events.Add(new GameDiagnosticEvent(
                SessionId: sessionId,
                SourceId: sourceId,
                SourceName: sourceName,
                NativeSeverityText: levelStr,
                Severity: severity,
                EventTimestamp: timestamp,
                ObservedSequence: 0,
                Message: message,
                RawBlock: line,
                Context: !string.IsNullOrWhiteSpace(subsystem) ? subsystem : null,
                SourcePath: filePath,
                EventKind: GameDiagnosticEventKind.NativeDiagnostic));
        }

        return events;
    }

    private static IReadOnlyList<GameDiagnosticEvent> ParseTweakXl(
        string sessionId,
        string sourceId,
        string sourceName,
        string filePath,
        string text)
    {
        var events = new List<GameDiagnosticEvent>();
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            var match = TweakXlRegex.Match(line);
            if (!match.Success) continue;

            var levelStr = match.Groups["level"].Value.Trim();
            var severity = TryMapSupportedSeverity(sourceId, levelStr);
            if (!severity.HasValue) continue;

            var timestamp = TryParseTimestamp(match.Groups["time"].Value);
            var message = match.Groups["message"].Value.Trim();

            string? recordContext = null;
            var colonIdx = message.IndexOf(':');
            if (colonIdx > 0)
            {
                var candidate = message[..colonIdx].Trim();
                if (!candidate.Contains(' ') && candidate.Contains('.'))
                {
                    recordContext = candidate;
                }
            }

            events.Add(new GameDiagnosticEvent(
                SessionId: sessionId,
                SourceId: sourceId,
                SourceName: sourceName,
                NativeSeverityText: levelStr,
                Severity: severity,
                EventTimestamp: timestamp,
                ObservedSequence: 0,
                Message: message,
                RawBlock: line,
                Context: recordContext,
                SourcePath: filePath,
                EventKind: GameDiagnosticEventKind.NativeDiagnostic));
        }

        return events;
    }

    private static IReadOnlyList<GameDiagnosticEvent> ParseCodeware(
        string sessionId,
        string sourceId,
        string sourceName,
        string filePath,
        string text)
    {
        var events = new List<GameDiagnosticEvent>();
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            var match = CodewareRegex.Match(line);
            if (!match.Success) continue;

            var levelStr = match.Groups["level"].Value.Trim();
            var severity = TryMapSupportedSeverity(sourceId, levelStr);
            if (!severity.HasValue) continue;

            var timestamp = TryParseTimestamp(match.Groups["time"].Value);
            var subsystem = match.Groups["subsystem"].Value.Trim();
            var message = match.Groups["message"].Value.Trim();

            events.Add(new GameDiagnosticEvent(
                SessionId: sessionId,
                SourceId: sourceId,
                SourceName: sourceName,
                NativeSeverityText: levelStr,
                Severity: severity,
                EventTimestamp: timestamp,
                ObservedSequence: 0,
                Message: message,
                RawBlock: line,
                Context: !string.IsNullOrWhiteSpace(subsystem) ? subsystem : null,
                SourcePath: filePath,
                EventKind: GameDiagnosticEventKind.NativeDiagnostic));
        }

        return events;
    }

    private static IReadOnlyList<GameDiagnosticEvent> ParseCetMain(
        string sessionId,
        string sourceId,
        string sourceName,
        string filePath,
        string text)
    {
        var events = new List<GameDiagnosticEvent>();
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            var match = CetMainRegex.Match(line);
            if (!match.Success) continue;

            var levelStr = match.Groups["level"].Value.Trim();
            var severity = TryMapSupportedSeverity(sourceId, levelStr);
            if (!severity.HasValue) continue;

            var timestamp = TryParseTimestamp(match.Groups["time"].Value);
            var func = match.Groups["func"].Value.Trim();
            var message = match.Groups["message"].Value.Trim();

            events.Add(new GameDiagnosticEvent(
                SessionId: sessionId,
                SourceId: sourceId,
                SourceName: sourceName,
                NativeSeverityText: levelStr,
                Severity: severity,
                EventTimestamp: timestamp,
                ObservedSequence: 0,
                Message: message,
                RawBlock: line,
                Context: !string.IsNullOrWhiteSpace(func) ? func : null,
                SourcePath: filePath,
                EventKind: GameDiagnosticEventKind.NativeDiagnostic));
        }

        return events;
    }
}
