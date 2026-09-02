using System.Globalization;
using System.Windows;
using System.Windows.Data;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;

namespace RipperWorks.App;

public sealed class DownloaderStatusConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is DownloaderStatus status
            ? PublicationStatusPolicy.IsPending(status)
                ? Localize("DownloaderStatusDownloaded")
                : Localize($"DownloaderStatus{status}")
            : value?.ToString() ?? string.Empty;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        Binding.DoNothing;

    private static string Localize(string key) =>
        System.Windows.Application.Current?.MainWindow?.DataContext is
            MainWindowViewModel main
                ? main.Settings.Localization.Get(key)
                : key;
}

public sealed class DownloaderSourceConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is DownloaderSource source
            ? Localize($"DownloaderSource{source}")
            : value?.ToString() ?? string.Empty;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        Binding.DoNothing;

    private static string Localize(string key) =>
        System.Windows.Application.Current?.MainWindow?.DataContext is
            MainWindowViewModel main
                ? main.Settings.Localization.Get(key)
                : key;
}

public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var bytes = value switch
        {
            long number => number,
            double number => (long)number,
            _ => 0
        };
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)Math.Max(0, bytes);
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }
        return $"{amount:0.##} {units[unit]}";
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NexusUpdateStatusConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var status = values.ElementAtOrDefault(0) is
            NexusUpdateCheckStatus value
                ? value
                : NexusUpdateCheckStatus.NotChecked;
        var available = values.ElementAtOrDefault(1)?.ToString() ??
            string.Empty;
        var localization = values.ElementAtOrDefault(2) as
            LocalizationService;
        return DownloaderUpdateStatusPresentation.GetText(
            status,
            available,
            localization);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();

}

public sealed class NexusUpdateTooltipConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var current = Display(values.ElementAtOrDefault(0));
        var available = Display(values.ElementAtOrDefault(1));
        var checkedAt = values.ElementAtOrDefault(2) is
            DateTimeOffset timestamp
                ? timestamp.ToLocalTime().ToString(
                    "g",
                    CultureInfo.CurrentCulture)
                : "—";
        var message = values.ElementAtOrDefault(3)?.ToString() ??
            string.Empty;
        var localization = values.ElementAtOrDefault(4) as
            LocalizationService;
        return string.Format(
            culture,
            localization?.Get("DownloaderUpdateTooltip") ??
            "Текущая версия: {0}\nДоступная версия: {1}\n" +
            "Последняя проверка: {2}\n{3}",
            current,
            available,
            checkedAt,
            message);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();

    private static string Display(object? value) =>
        string.IsNullOrWhiteSpace(value?.ToString())
            ? "—"
            : value.ToString()!;

}

public static class DownloaderUpdateStatusPresentation
{
    public static string GetText(
        NexusUpdateCheckStatus status,
        string? availableVersion,
        LocalizationService? localization)
    {
        var presentationStatus = status == NexusUpdateCheckStatus.OlderVersion
            ? NexusUpdateCheckStatus.UpToDate
            : status;
        var key = $"DownloaderUpdate{presentationStatus}";
        var fallback = presentationStatus switch
        {
            NexusUpdateCheckStatus.NotChecked => "Не проверено",
            NexusUpdateCheckStatus.Checking => "Проверяется…",
            NexusUpdateCheckStatus.UpToDate => "Актуален",
            NexusUpdateCheckStatus.UpdateAvailable => "Доступно {0}",
            NexusUpdateCheckStatus.ManualReviewRequired =>
                "Проверить вручную",
            NexusUpdateCheckStatus.CurrentFileUnavailable =>
                "Файл недоступен",
            NexusUpdateCheckStatus.MissingIdentity =>
                "Недостаточно данных",
            NexusUpdateCheckStatus.ApiError => "Ошибка проверки",
            NexusUpdateCheckStatus.DifferentComponent =>
                "Другой компонент",
            NexusUpdateCheckStatus.UnknownComparison =>
                "Нельзя безопасно сравнить",
            _ => "Не проверено"
        };
        var template = localization?.Get(key);
        if (string.IsNullOrWhiteSpace(template) ||
            string.Equals(template, key, StringComparison.Ordinal))
        {
            template = fallback;
        }
        return presentationStatus == NexusUpdateCheckStatus.UpdateAvailable
            ? string.Format(
                CultureInfo.CurrentCulture,
                template,
                string.IsNullOrWhiteSpace(availableVersion)
                    ? "—"
                    : availableVersion.Trim())
            : template;
    }
}
