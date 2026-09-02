using System.IO;
using System.Windows;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Downloader;

namespace RipperWorks.App;

public partial class DownloaderImportSummaryWindow : Window
{
    private readonly int _currentCount;
    private readonly DownloaderImportSummary _summary;
    private readonly LocalizationService _localization;

    public DownloaderImportSummaryWindow(
        string filePath,
        IReadOnlyList<string> sheets,
        DownloaderImportSummary summary,
        int currentCount,
        LocalizationService localization)
    {
        InitializeComponent();
        _currentCount = currentCount;
        _summary = summary;
        _localization = localization;
        DataContext = new DownloaderImportSummaryViewModel(
            filePath,
            sheets,
            summary,
            localization);
    }

    public bool ReplaceCatalog { get; private set; }

    private void ImportButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        ReplaceCatalog = false;
        DialogResult = true;
    }

    private void ReplaceButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        var confirmation = new DownloaderReplaceConfirmationWindow(
            _currentCount,
            _summary.FinalReplaceTotal,
            _localization)
        {
            Owner = this
        };
        if (confirmation.ShowDialog() != true)
            return;
        ReplaceCatalog = true;
        DialogResult = true;
    }
}

public sealed class DownloaderImportSummaryViewModel
{
    private readonly LocalizationService _localization;

    public DownloaderImportSummaryViewModel(
        string filePath,
        IReadOnlyList<string> sheets,
        DownloaderImportSummary summary,
        LocalizationService localization)
    {
        _localization = localization;
        SummaryText = string.Join(
            Environment.NewLine,
            string.Format(L("DownloaderSummaryFile"), Path.GetFileName(filePath)),
            string.Format(
                L("DownloaderSummarySheets"),
                string.Join(", ", sheets)),
            string.Empty,
            string.Format(L("DownloaderSummaryFound"), summary.FoundRows),
            string.Format(L("DownloaderSummaryNew"), summary.NewRecords),
            string.Format(L("DownloaderSummaryUpdated"), summary.UpdatedRecords),
            string.Format(
                L("DownloaderSummaryDuplicates"),
                summary.UnchangedDuplicates),
            string.Format(L("DownloaderSummarySkipped"), summary.Skipped),
            string.Format(
                L("DownloaderSummaryMissingNames"),
                summary.MissingNames),
            string.Format(
                L("DownloaderSummaryMissingUrls"),
                summary.MissingUrls),
            string.Format(
                L("DownloaderSummaryFinal"),
                summary.FinalMergeTotal));
    }

    public string Title => L("DownloaderImportSummaryTitle");
    public string SummaryText { get; }
    public string ImportLabel => L("DownloaderImportAction");
    public string ReplaceLabel => L("DownloaderReplaceCatalog");
    public string CancelLabel => L("Cancel");
    private string L(string key) => _localization.Get(key);
}
