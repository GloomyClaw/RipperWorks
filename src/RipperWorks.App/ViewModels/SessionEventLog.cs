using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using RipperWorks.App.Services;
using RipperWorks.Core;

namespace RipperWorks.App.ViewModels;

public sealed class SessionEventLog : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;

    public SessionEventLog(LocalizationService localization)
    {
        _localization = localization;
        Events.CollectionChanged += EventsOnCollectionChanged;
    }

    public ObservableCollection<SessionEventItem> Events { get; } = [];
    public bool HasEvents => Events.Count > 0;

    public void Record(string localizationKey, string? detail = null)
    {
        var message = _localization.Get(localizationKey);
        if (!string.IsNullOrWhiteSpace(detail))
            message += $" — {detail}";
        Events.Insert(0, new SessionEventItem(
            DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            message));
    }

    private void EventsOnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasEvents));

    public void Dispose() =>
        Events.CollectionChanged -= EventsOnCollectionChanged;
}

public sealed record SessionEventItem(string Time, string Message);
