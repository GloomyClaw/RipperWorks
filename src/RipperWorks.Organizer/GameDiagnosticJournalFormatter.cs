using System;
using System.Collections.Generic;
using System.Text;

namespace RipperWorks.Organizer;

public static class GameDiagnosticJournalFormatter
{
    public static string FormatJournalSection(
        IReadOnlyList<GameDiagnosticEvent> events,
        IReadOnlyList<string> unavailableSourceNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("===== Diagnostics =====");

        var hasContent = false;

        foreach (var ev in events)
        {
            hasContent = true;
            if (ev.EventKind == GameDiagnosticEventKind.CrashReportCreated)
            {
                sb.AppendLine($"[Cyberpunk 2077] [CrashReportCreated]");
                sb.AppendLine($"Report: {ev.SourcePath}");
                if (!string.IsNullOrWhiteSpace(ev.RawBlock))
                {
                    using var reader = new System.IO.StringReader(ev.RawBlock);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Error reason:", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("Expression:", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("Message:", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine(trimmed);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(ev.Message))
                {
                    sb.AppendLine(ev.Message);
                }
                sb.AppendLine();
            }
            else
            {
                var timeHeader = ev.EventTimestamp.HasValue
                    ? $"[{ev.EventTimestamp.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}] "
                    : string.Empty;
                var severityToken = ev.Severity?.ToString() ?? ev.NativeSeverityText;
                var severitySuffix = !string.IsNullOrWhiteSpace(severityToken) ? $" [{severityToken}]" : string.Empty;
                sb.AppendLine($"{timeHeader}[{ev.SourceName}]{severitySuffix}");

                if (!string.IsNullOrWhiteSpace(ev.Context))
                {
                    sb.AppendLine($"At: {ev.Context}");
                }

                if (!string.IsNullOrWhiteSpace(ev.NativeDiagnosticCode))
                {
                    sb.AppendLine($"Code: {ev.NativeDiagnosticCode}");
                }

                if (!string.IsNullOrWhiteSpace(ev.Message))
                {
                    sb.AppendLine(ev.Message);
                }

                if (!string.IsNullOrWhiteSpace(ev.SourcePath))
                {
                    sb.AppendLine($"Source log: {ev.SourcePath}");
                }

                sb.AppendLine();
            }
        }

        foreach (var unavailableName in unavailableSourceNames)
        {
            hasContent = true;
            sb.AppendLine($"[RipperWorks] Diagnostic source unavailable: {unavailableName}");
        }

        if (!hasContent)
        {
            sb.AppendLine("No warnings or errors were reported by captured diagnostic sources.");
        }

        return sb.ToString();
    }
}
