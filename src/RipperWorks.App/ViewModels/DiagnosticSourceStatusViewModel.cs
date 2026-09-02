using System;
using RipperWorks.App.Services;
using RipperWorks.Core;

namespace RipperWorks.App.ViewModels;

public enum DiagnosticSourceStatus
{
    NotInstalled,
    Installed,
    Unavailable,
    Available,
    Configured,
    NotConfigured
}

public sealed class DiagnosticSourceStatusViewModel : ObservableObject
{
    private string _displayName;
    private DiagnosticSourceStatus _status;
    private string _statusText;
    private string _statusBrushKey;

    public DiagnosticSourceStatusViewModel(
        string sourceId,
        string displayName,
        DiagnosticSourceStatus status,
        string statusText,
        string statusBrushKey)
    {
        SourceId = sourceId;
        _displayName = displayName;
        _status = status;
        _statusText = statusText;
        _statusBrushKey = statusBrushKey;
    }

    public string SourceId { get; }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public DiagnosticSourceStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string StatusBrushKey
    {
        get => _statusBrushKey;
        set => SetProperty(ref _statusBrushKey, value);
    }

    public void Update(DiagnosticSourceStatus status, string statusText, string statusBrushKey)
    {
        Status = status;
        StatusText = statusText;
        StatusBrushKey = statusBrushKey;
    }

    public void UpdateLocalization(LocalizationService localization)
    {
        StatusText = Status switch
        {
            DiagnosticSourceStatus.Installed => localization.Get("DiagnosticSourceInstalled"),
            DiagnosticSourceStatus.NotInstalled => localization.Get("DiagnosticSourceNotInstalled"),
            DiagnosticSourceStatus.Configured => localization.Get("DiagnosticSourceConfigured"),
            DiagnosticSourceStatus.NotConfigured => localization.Get("DiagnosticSourceNotConfigured"),
            DiagnosticSourceStatus.Unavailable => localization.Get("DiagnosticSourceUnavailable"),
            DiagnosticSourceStatus.Available => localization.Get("DiagnosticSourceAvailable"),
            _ => StatusText
        };
    }
}
