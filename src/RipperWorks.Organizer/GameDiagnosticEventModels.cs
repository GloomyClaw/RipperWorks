using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RipperWorks.Organizer;

public enum GameDiagnosticSeverity
{
    Warning,
    Error,
    Critical,
    Fatal
}

public enum GameDiagnosticEventKind
{
    NativeDiagnostic,
    CrashReportCreated
}

public sealed record GameDiagnosticEvent(
    string SessionId,
    string SourceId,
    string SourceName,
    string? NativeSeverityText,
    GameDiagnosticSeverity? Severity,
    DateTimeOffset? EventTimestamp,
    int ObservedSequence,
    string Message,
    string? RawBlock,
    string? Context,
    string SourcePath,
    GameDiagnosticEventKind EventKind,
    string? NativeDiagnosticCode = null);

public sealed record GameDiagnosticParseResult(
    string SessionId,
    IReadOnlyList<GameDiagnosticEvent> Events,
    IReadOnlyList<string> UnavailableSourceIds,
    string FormattedJournalSection);

public interface IGameDiagnosticParserService
{
    Task<GameDiagnosticParseResult> ParseDiagnosticsAsync(
        Core.GameProfileRecord profile,
        GameDiagnosticCaptureResult captureResult,
        CancellationToken cancellationToken = default);
}
