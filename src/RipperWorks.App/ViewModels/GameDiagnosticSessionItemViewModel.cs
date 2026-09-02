using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Organizer;

namespace RipperWorks.App.ViewModels;

public sealed class GameDiagnosticSessionItemViewModel : ObservableObject
{
    private string _resultText;
    private string _displayLabel;
    private string _startLocalFormatted;
    private string _endLocalFormatted;

    public GameDiagnosticSessionItemViewModel(
        GameDiagnosticSessionJournal journal,
        IReadOnlyList<GameDiagnosticEventViewModel> events,
        LocalizationService localization)
    {
        Journal = journal;
        Events = events;
        ExitCodeText = journal.ExitCode.HasValue ? journal.ExitCode.Value.ToString(CultureInfo.InvariantCulture) : "—";
        _resultText = ComputeResultText(journal, localization);
        _displayLabel = ComputeDisplayLabel(journal, _resultText, localization);
        _startLocalFormatted = FormatDateTime(journal.StartLocal, journal.StartUtc, localization, withSeconds: true);
        _endLocalFormatted = FormatDateTime(journal.EndLocal, journal.EndUtc, localization, withSeconds: true);

        WarningCount = events.Count(e => e.Severity == GameDiagnosticSeverity.Warning);
        ErrorCount = events.Count(e => e.Severity == GameDiagnosticSeverity.Error);
        CrashReportCount = events.Count(e => e.IsCrashReport);
        AllEventsCount = events.Count;
    }

    public GameDiagnosticSessionJournal Journal { get; }
    public IReadOnlyList<GameDiagnosticEventViewModel> Events { get; }

    public string FilePath => Journal.FilePath;
    public string FileName => Journal.FileName;
    public string SessionId => Journal.SessionId;
    public string DisplayLabel => _displayLabel;

    public string StartLocalFormatted => _startLocalFormatted;
    public string EndLocalFormatted => _endLocalFormatted;
    public string ResultText => _resultText;
    public string ExitCodeText { get; }

    public int WarningCount { get; }
    public int ErrorCount { get; }
    public int CrashReportCount { get; }
    public int AllEventsCount { get; }

    public GameDiagnosticSessionState State => Journal.State;
    public bool IsUnsupported => State == GameDiagnosticSessionState.Unsupported;
    public bool IsIncomplete => State == GameDiagnosticSessionState.Incomplete;
    public bool IsMalformed => State == GameDiagnosticSessionState.Malformed;
    public bool IsComplete => State == GameDiagnosticSessionState.Complete;

    public bool HasCleanDiagnostics => Journal.HasExplicitCleanDiagnosticsStatement && AllEventsCount == 0 && !HasUnavailableSources;
    public bool HasDiagnosticsSection => Journal.HasDiagnosticsSection;
    public bool HasUnavailableSources => Journal.UnavailableSourceNames.Count > 0;
    public string UnavailableSourcesText => string.Join(", ", Journal.UnavailableSourceNames);

    public void UpdateLocalization(LocalizationService localization)
    {
        var newResult = ComputeResultText(Journal, localization);
        var newLabel = ComputeDisplayLabel(Journal, newResult, localization);
        var newStart = FormatDateTime(Journal.StartLocal, Journal.StartUtc, localization, withSeconds: true);
        var newEnd = FormatDateTime(Journal.EndLocal, Journal.EndUtc, localization, withSeconds: true);

        if (_resultText != newResult)
        {
            _resultText = newResult;
            OnPropertyChanged(nameof(ResultText));
        }

        if (_displayLabel != newLabel)
        {
            _displayLabel = newLabel;
            OnPropertyChanged(nameof(DisplayLabel));
        }

        if (_startLocalFormatted != newStart)
        {
            _startLocalFormatted = newStart;
            OnPropertyChanged(nameof(StartLocalFormatted));
        }

        if (_endLocalFormatted != newEnd)
        {
            _endLocalFormatted = newEnd;
            OnPropertyChanged(nameof(EndLocalFormatted));
        }

        foreach (var ev in Events)
        {
            ev.UpdateLocalization(localization);
        }
    }

    private static string ComputeResultText(GameDiagnosticSessionJournal journal, LocalizationService localization)
    {
        if (journal.State == GameDiagnosticSessionState.Incomplete)
        {
            return localization.Get("DiagnosticsSessionStateIncomplete");
        }
        if (journal.State == GameDiagnosticSessionState.Unsupported)
        {
            return localization.Get("DiagnosticsSessionStateUnsupported");
        }
        if (journal.State == GameDiagnosticSessionState.Malformed)
        {
            return localization.Get("DiagnosticsSessionStateMalformed");
        }

        if (string.Equals(journal.Result, "Exited", StringComparison.OrdinalIgnoreCase))
        {
            return localization.Get("DiagnosticsSessionStateExited");
        }
        if (string.Equals(journal.Result, "LaunchFailed", StringComparison.OrdinalIgnoreCase))
        {
            return localization.Get("DiagnosticsSessionStateLaunchFailed");
        }

        return journal.Result ?? string.Empty;
    }

    private static string ComputeDisplayLabel(GameDiagnosticSessionJournal journal, string resultText, LocalizationService localization)
    {
        var timePart = FormatDateTime(journal.StartLocal, journal.StartUtc, localization, withSeconds: false);
        if (string.IsNullOrWhiteSpace(timePart) || timePart == "—")
        {
            timePart = journal.FileName;
        }
        return $"{timePart} — {resultText}";
    }

    private static string FormatDateTime(string? localStr, DateTimeOffset? utcTime, LocalizationService localization, bool withSeconds)
    {
        var formats = new[] { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyyMMdd-HHmmss-fff", "yyyyMMdd-HHmmss" };
        var culture = localization.CurrentLanguage == SupportedLanguages.Russian
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-US");

        if (!string.IsNullOrWhiteSpace(localStr) &&
            DateTime.TryParseExact(localStr, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return withSeconds ? dt.ToString("G", culture) : dt.ToString("g", culture);
        }

        if (utcTime.HasValue)
        {
            var ldt = utcTime.Value.LocalDateTime;
            return withSeconds ? ldt.ToString("G", culture) : ldt.ToString("g", culture);
        }

        return localStr ?? "—";
    }
}
