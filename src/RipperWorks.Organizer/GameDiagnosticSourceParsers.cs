using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace RipperWorks.Organizer;

public static partial class GameDiagnosticSourceParsers
{
    // CET scripting Form 1: [2026-08-06 19:40:16 UTC+10:00] [27036] [TDO] LoadLanguage() WARNING: Could not locate ...
    private static readonly Regex CetScriptingWarningRegex = new(
        @"^\[(?<time>[^\]]+)\]\s*\[\s*\d+\s*\]\s*\[(?<mod>[^\]]+)\]\s*(?<fn>.*?)\s*WARNING:\s*(?<message>.*)$",
        RegexOptions.Compiled);

    // CET scripting Form 2: [2026-08-05 20:11:44 UTC+10:00] [20360] sol: syntax error: [string "..."]:1: ...
    private static readonly Regex CetScriptingSyntaxErrorRegex = new(
        @"^\[(?<time>[^\]]+)\]\s*\[\s*\d+\s*\]\s*(?<message>sol:\s*syntax error:.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // redscript 0.5.31 header:
    // [WARN - Thu, 6 Aug 2026 19:39:15 +1000] At J:\...\healthbar.reds:3:1:
    // [ERROR - Thu, 28 Sep 2023 13:03:15 -0700] [UNRESOLVED_TYPE] At G:\...\shop.reds:1:1:
    // [ERROR - 2026-08-16 02:30:02] Could not find method ...
    private static readonly Regex RedscriptHeaderRegex = new(
        @"^\[(?<level>INFO|WARN|WARNING|ERROR|CRITICAL|FATAL|DEBUG|TRACE)\s*-\s*(?<time>[^\]]+)\](?:\s*\[(?<code>[A-Z0-9_]+)\])?(?:\s+At\s+(?<loc>[a-zA-Z]:[^\r\n:]+:\d+:\d+|[^\r\n:]+:\d+:\d+|[^\r\n]+?):?)?(?:\s+(?<msg>.*))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RedscriptLocationLineRegex = new(
        @"^\s*At\s+(?<loc>[a-zA-Z]:[^\r\n:]+:\d+:\d+|[^\r\n:]+:\d+:\d+|.+?):?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<GameDiagnosticEvent> ParseFileEvents(
        string sessionId,
        string sourceId,
        string sourceName,
        string filePath,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return sourceId switch
        {
            "redscript" => ParseRedscript(sessionId, sourceId, sourceName, filePath, text),
            "RED4ext" => ParseRed4Ext(sessionId, sourceId, sourceName, filePath, text),
            "RedFileSystem" => ParseRedFileSystem(sessionId, sourceId, sourceName, filePath, text),
            "ArchiveXL" => ParseArchiveXl(sessionId, sourceId, sourceName, filePath, text),
            "TweakXL" => ParseTweakXl(sessionId, sourceId, sourceName, filePath, text),
            "Codeware" => ParseCodeware(sessionId, sourceId, sourceName, filePath, text),
            "CyberEngineTweaks" => Path.GetFileName(filePath).Equals("scripting.log", StringComparison.OrdinalIgnoreCase)
                ? ParseCetScripting(sessionId, sourceId, sourceName, filePath, text)
                : ParseCetMain(sessionId, sourceId, sourceName, filePath, text),
            _ => [] // Unknown or unsupported SourceId: zero events, NO generic spdlog fallback!
        };
    }

    public static IReadOnlyList<GameDiagnosticEvent> ParseCrashReports(
        string sessionId,
        string sourceId,
        string sourceName,
        IReadOnlyList<DiagnosticReportDirectory> reportDirs)
    {
        var events = new List<GameDiagnosticEvent>();

        foreach (var dir in reportDirs)
        {
            var stacktracePath = Path.Combine(dir.DirectoryPath, "stacktrace.txt");
            string? errorReason = null, expression = null, message = null, fileLocation = null;
            string? rawBlock = null;

            if (File.Exists(stacktracePath))
            {
                try
                {
                    rawBlock = File.ReadAllText(stacktracePath);
                    using var reader = new StringReader(rawBlock);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Error reason:", StringComparison.OrdinalIgnoreCase))
                            errorReason = trimmed["Error reason:".Length..].Trim();
                        else if (trimmed.StartsWith("Expression:", StringComparison.OrdinalIgnoreCase))
                            expression = trimmed["Expression:".Length..].Trim();
                        else if (trimmed.StartsWith("Message:", StringComparison.OrdinalIgnoreCase))
                            message = trimmed["Message:".Length..].Trim();
                        else if (trimmed.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                            fileLocation = trimmed["File:".Length..].Trim();
                    }
                }
                catch { }
            }

            var context = !string.IsNullOrWhiteSpace(fileLocation) ? fileLocation : dir.DirectoryName;
            var summaryMessage = "Cyberpunk 2077 created a crash report.";

            events.Add(new GameDiagnosticEvent(
                SessionId: sessionId,
                SourceId: sourceId,
                SourceName: sourceName,
                NativeSeverityText: null,
                Severity: null, // Strictly null per Section 19!
                EventTimestamp: dir.CreationTimeUtc,
                ObservedSequence: 0,
                Message: summaryMessage,
                RawBlock: rawBlock,
                Context: context,
                SourcePath: dir.DirectoryPath,
                EventKind: GameDiagnosticEventKind.CrashReportCreated));
        }

        return events;
    }

    private static IReadOnlyList<GameDiagnosticEvent> ParseCetScripting(
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
            var mWarn = CetScriptingWarningRegex.Match(line);
            if (mWarn.Success)
            {
                var timestamp = TryParseTimestamp(mWarn.Groups["time"].Value);
                var mod = mWarn.Groups["mod"].Value.Trim();
                var fn = mWarn.Groups["fn"].Value.Trim();
                var msg = mWarn.Groups["message"].Value.Trim();
                var fullMsg = !string.IsNullOrWhiteSpace(fn) ? $"{fn} WARNING: {msg}" : $"WARNING: {msg}";

                events.Add(new GameDiagnosticEvent(
                    SessionId: sessionId,
                    SourceId: sourceId,
                    SourceName: sourceName,
                    NativeSeverityText: "WARNING",
                    Severity: GameDiagnosticSeverity.Warning,
                    EventTimestamp: timestamp,
                    ObservedSequence: 0,
                    Message: fullMsg,
                    RawBlock: line,
                    Context: mod,
                    SourcePath: filePath,
                    EventKind: GameDiagnosticEventKind.NativeDiagnostic));
                continue;
            }

            var mSyntax = CetScriptingSyntaxErrorRegex.Match(line);
            if (mSyntax.Success)
            {
                var timestamp = TryParseTimestamp(mSyntax.Groups["time"].Value);
                var msg = mSyntax.Groups["message"].Value.Trim();

                events.Add(new GameDiagnosticEvent(
                    SessionId: sessionId,
                    SourceId: sourceId,
                    SourceName: sourceName,
                    NativeSeverityText: "syntax error",
                    Severity: GameDiagnosticSeverity.Error,
                    EventTimestamp: timestamp,
                    ObservedSequence: 0,
                    Message: msg,
                    RawBlock: line,
                    Context: null,
                    SourcePath: filePath,
                    EventKind: GameDiagnosticEventKind.NativeDiagnostic));
            }
        }

        return events;
    }

    private static IReadOnlyList<GameDiagnosticEvent> ParseRedscript(
        string sessionId,
        string sourceId,
        string sourceName,
        string filePath,
        string text)
    {
        var events = new List<GameDiagnosticEvent>();
        using var reader = new StringReader(text);
        string? line;

        var hasDetailedErrorInCurrentGroup = false;
        string? currentLevel = null;
        DateTimeOffset? currentTimestamp = null;
        string? currentContext = null;
        string? currentCode = null;
        string? initialHeaderMsg = null;
        var blockLines = new List<string>();
        var diagnosticTextLines = new List<string>();
        var pendingSourceLines = new List<string>();
        var hasSeenCaret = false;

        void FlushCurrent()
        {
            if (currentLevel is not null)
            {
                var severity = TryMapSupportedSeverity(sourceId, currentLevel);
                if (severity.HasValue)
                {
                    string fullMessage;
                    if (diagnosticTextLines.Count > 0)
                    {
                        fullMessage = !string.IsNullOrWhiteSpace(initialHeaderMsg)
                            ? $"{initialHeaderMsg} {string.Join(" ", diagnosticTextLines)}"
                            : string.Join(" ", diagnosticTextLines);
                    }
                    else if (pendingSourceLines.Count > 0 && !hasSeenCaret)
                    {
                        fullMessage = !string.IsNullOrWhiteSpace(initialHeaderMsg)
                            ? $"{initialHeaderMsg} {string.Join(" ", pendingSourceLines)}"
                            : string.Join(" ", pendingSourceLines);
                    }
                    else if (!string.IsNullOrWhiteSpace(initialHeaderMsg))
                    {
                        fullMessage = initialHeaderMsg;
                    }
                    else
                    {
                        fullMessage = "Redscript compilation diagnostic";
                    }

                    var isSummary = fullMessage.StartsWith("Compilation failed", StringComparison.OrdinalIgnoreCase) ||
                                    fullMessage.StartsWith("Compilation error", StringComparison.OrdinalIgnoreCase);

                    var shouldEmit = !isSummary || !hasDetailedErrorInCurrentGroup;

                    if (shouldEmit)
                    {
                        var raw = string.Join(Environment.NewLine, blockLines);
                        events.Add(new GameDiagnosticEvent(
                            SessionId: sessionId,
                            SourceId: sourceId,
                            SourceName: sourceName,
                            NativeSeverityText: currentLevel,
                            Severity: severity,
                            EventTimestamp: currentTimestamp,
                            ObservedSequence: 0,
                            Message: fullMessage,
                            RawBlock: raw,
                            Context: currentContext,
                            SourcePath: filePath,
                            EventKind: GameDiagnosticEventKind.NativeDiagnostic,
                            NativeDiagnosticCode: currentCode));
                    }

                    if (!isSummary)
                    {
                        if (severity is GameDiagnosticSeverity.Error or GameDiagnosticSeverity.Critical or GameDiagnosticSeverity.Fatal)
                        {
                            hasDetailedErrorInCurrentGroup = true;
                        }
                    }
                    else
                    {
                        hasDetailedErrorInCurrentGroup = false;
                    }
                }
            }

            currentLevel = null;
            currentTimestamp = null;
            currentContext = null;
            currentCode = null;
            initialHeaderMsg = null;
            hasSeenCaret = false;
            blockLines.Clear();
            diagnosticTextLines.Clear();
            pendingSourceLines.Clear();
        }

        while ((line = reader.ReadLine()) is not null)
        {
            var headerMatch = RedscriptHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                FlushCurrent();
                currentLevel = headerMatch.Groups["level"].Value.Trim();
                currentTimestamp = TryParseTimestamp(headerMatch.Groups["time"].Value);

                var code = headerMatch.Groups["code"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    currentCode = code;
                }

                var loc = headerMatch.Groups["loc"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(loc))
                {
                    currentContext = loc;
                }

                var msg = headerMatch.Groups["msg"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(msg) && !msg.StartsWith("At ", StringComparison.OrdinalIgnoreCase))
                {
                    initialHeaderMsg = msg;
                }

                blockLines.Add(line);
            }
            else if (currentLevel is not null)
            {
                blockLines.Add(line);

                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                var locMatch = RedscriptLocationLineRegex.Match(line);
                if (locMatch.Success && currentContext is null)
                {
                    currentContext = locMatch.Groups["loc"].Value.Trim();
                    continue;
                }

                var isCaret = Regex.IsMatch(trimmed, @"^(\^+|\|\s*\^+|\s*\^+)");
                if (isCaret)
                {
                    hasSeenCaret = true;
                    continue;
                }

                if (hasSeenCaret)
                {
                    diagnosticTextLines.Add(trimmed);
                }
                else
                {
                    pendingSourceLines.Add(trimmed);
                }
            }
        }

        FlushCurrent();
        return events;
    }

    public static GameDiagnosticSeverity? TryMapSupportedSeverity(string sourceId, string? nativeLevel)
    {
        if (string.IsNullOrWhiteSpace(nativeLevel)) return null;
        var lower = nativeLevel.Trim().ToLowerInvariant();

        return sourceId switch
        {
            "RED4ext" => lower switch
            {
                "warn" or "warning" => GameDiagnosticSeverity.Warning,
                "error" or "err" => GameDiagnosticSeverity.Error,
                _ => null
            },
            "ArchiveXL" => lower switch
            {
                "warn" or "warning" => GameDiagnosticSeverity.Warning,
                "error" or "err" => GameDiagnosticSeverity.Error,
                _ => null
            },
            "TweakXL" => lower switch
            {
                "warn" or "warning" => GameDiagnosticSeverity.Warning,
                "error" or "err" => GameDiagnosticSeverity.Error,
                _ => null
            },
            "CyberEngineTweaks" => lower switch
            {
                "warn" or "warning" => GameDiagnosticSeverity.Warning,
                "error" or "err" => GameDiagnosticSeverity.Error,
                _ => null
            },
            "redscript" => lower switch
            {
                "warn" or "warning" => GameDiagnosticSeverity.Warning,
                "error" => GameDiagnosticSeverity.Error,
                _ => null
            },
            "RedFileSystem" => null,
            "Codeware" => null,
            _ => null
        };
    }

    private static DateTimeOffset? TryParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();

        // Handle CET "UTC+10:00" format
        if (trimmed.Contains("UTC", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Replace("UTC", "", StringComparison.OrdinalIgnoreCase).Trim();
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return dto;

        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return new DateTimeOffset(dt);

        return null;
    }
}
