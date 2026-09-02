using System;
using RipperWorks.App.Services;
using RipperWorks.Core;
using RipperWorks.Organizer;

namespace RipperWorks.App.ViewModels;

public sealed class GameDiagnosticEventViewModel : ObservableObject
{
    private LocalizationService _localization;
    private string _severityText;
    private string _severityBrushKey;

    public GameDiagnosticEventViewModel(GameDiagnosticEvent domainEvent, LocalizationService localization)
    {
        DomainEvent = domainEvent;
        _localization = localization;
        (_severityText, _severityBrushKey) = ComputeSeverity(domainEvent, localization);
    }

    public GameDiagnosticEvent DomainEvent { get; }

    public string SessionId => DomainEvent.SessionId;
    public string SourceId => DomainEvent.SourceId;
    public string SourceName => DomainEvent.SourceName;
    public string? NativeSeverityText => DomainEvent.NativeSeverityText;
    public GameDiagnosticSeverity? Severity => DomainEvent.Severity;
    public string SeverityText => _severityText;
    public string SeverityBrushKey => _severityBrushKey;
    public DateTimeOffset? EventTimestamp => DomainEvent.EventTimestamp;

    public string EventTimestampText => EventTimestamp.HasValue
        ? EventTimestamp.Value.DateTime.ToString("HH:mm:ss.fff")
        : string.Empty;

    public string Message => IsCrashReport
        ? _localization.Get("DiagnosticsCrashReportMessage")
        : DomainEvent.Message;

    public string? RawBlock => DomainEvent.RawBlock;
    public string? Context => DomainEvent.Context;
    public string? NativeDiagnosticCode => DomainEvent.NativeDiagnosticCode;
    public string SourcePath => DomainEvent.SourcePath;
    public GameDiagnosticEventKind EventKind => DomainEvent.EventKind;

    public string? AdditionalDetails => IsCrashReport ? DomainEvent.RawBlock : null;
    public bool HasAdditionalDetails => !string.IsNullOrWhiteSpace(AdditionalDetails);

    public bool IsCrashReport => EventKind == GameDiagnosticEventKind.CrashReportCreated;
    public bool HasContext => !string.IsNullOrWhiteSpace(Context);
    public bool HasDiagnosticCode => !string.IsNullOrWhiteSpace(NativeDiagnosticCode);
    public bool HasSourcePath => !string.IsNullOrWhiteSpace(SourcePath);

    public void UpdateLocalization(LocalizationService localization)
    {
        _localization = localization;
        var (text, brush) = ComputeSeverity(DomainEvent, localization);
        if (_severityText != text || _severityBrushKey != brush)
        {
            _severityText = text;
            _severityBrushKey = brush;
            OnPropertyChanged(nameof(SeverityText));
            OnPropertyChanged(nameof(SeverityBrushKey));
        }

        if (IsCrashReport)
        {
            OnPropertyChanged(nameof(Message));
        }
    }

    private static (string Text, string Brush) ComputeSeverity(GameDiagnosticEvent e, LocalizationService localization)
    {
        if (e.EventKind == GameDiagnosticEventKind.CrashReportCreated)
        {
            return (localization.Get("DiagnosticsSeverityCrashReport"), "TextSecondaryBrush");
        }

        if (e.Severity == GameDiagnosticSeverity.Warning)
        {
            return (localization.Get("DiagnosticsSeverityWarning"), "WarningBrush");
        }

        if (e.Severity == GameDiagnosticSeverity.Error)
        {
            return (localization.Get("DiagnosticsSeverityError"), "ErrorBrush");
        }

        return (e.NativeSeverityText ?? string.Empty, "TextSecondaryBrush");
    }
}
