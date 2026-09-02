using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed class GameDiagnosticParserService : IGameDiagnosticParserService
{
    public Task<GameDiagnosticParseResult> ParseDiagnosticsAsync(
        GameProfileRecord profile,
        GameDiagnosticCaptureResult captureResult,
        CancellationToken cancellationToken = default)
    {
        if (captureResult is null)
        {
            return Task.FromResult(new GameDiagnosticParseResult(
                string.Empty,
                [],
                [],
                string.Empty));
        }

        var sequence = 0;
        var allEvents = new List<GameDiagnosticEvent>();
        var unavailableSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceDelta in captureResult.SourceDeltas)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sourceDelta.CaptureStatus is DiagnosticCaptureStatus.Unavailable or DiagnosticCaptureStatus.CaptureError)
            {
                unavailableSources.Add(sourceDelta.SourceName);
            }

            if (sourceDelta.NewReportDirectories.Count > 0)
            {
                var crashEvents = GameDiagnosticSourceParsers.ParseCrashReports(
                    captureResult.SessionId,
                    sourceDelta.SourceId,
                    sourceDelta.SourceName,
                    sourceDelta.NewReportDirectories);
                foreach (var ev in crashEvents)
                {
                    allEvents.Add(ev with { ObservedSequence = ++sequence });
                }
            }

            foreach (var fileDelta in sourceDelta.FileDeltas)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (fileDelta.MutationKind == DiagnosticMutationKind.Unavailable)
                {
                    unavailableSources.Add(sourceDelta.SourceName);
                    continue;
                }

                if (fileDelta.MutationKind is DiagnosticMutationKind.Created or DiagnosticMutationKind.Appended or DiagnosticMutationKind.ReplacedOrTruncated)
                {
                    var (readSuccess, sliceText) = GameDiagnosticRangeReader.ReadCandidateSlice(fileDelta);
                    if (!readSuccess)
                    {
                        unavailableSources.Add(sourceDelta.SourceName);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(sliceText))
                    {
                        var fileEvents = GameDiagnosticSourceParsers.ParseFileEvents(
                            captureResult.SessionId,
                            sourceDelta.SourceId,
                            sourceDelta.SourceName,
                            fileDelta.FullPath,
                            sliceText);
                        foreach (var ev in fileEvents)
                        {
                            allEvents.Add(ev with { ObservedSequence = ++sequence });
                        }
                    }
                }
            }
        }

        var sortedEvents = allEvents
            .OrderBy(e => e.EventTimestamp.HasValue ? 0 : 1)
            .ThenBy(e => e.EventTimestamp)
            .ThenBy(e => e.ObservedSequence)
            .ToList();

        var unavailableList = unavailableSources.ToList();
        var formattedSection = GameDiagnosticJournalFormatter.FormatJournalSection(sortedEvents, unavailableList);

        return Task.FromResult(new GameDiagnosticParseResult(
            captureResult.SessionId,
            sortedEvents,
            unavailableList,
            formattedSection));
    }
}
