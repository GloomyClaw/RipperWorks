using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Downloader;

namespace RipperWorks.App;

public sealed record DownloaderImportPreviewSelection(
    IReadOnlyList<string> SheetNames,
    IReadOnlyDictionary<string, DownloaderTableOptions> Options);

public partial class DownloaderImportWindow : Window
{
    private readonly DownloaderImportPreviewViewModel _viewModel;

    public DownloaderImportWindow(
        string filePath,
        DownloaderTableImporter importer,
        LocalizationService localization)
    {
        InitializeComponent();
        _viewModel = new(
            filePath,
            importer,
            localization);
        DataContext = _viewModel;
        _viewModel.RefreshPreview(true);
    }

    public DownloaderImportPreviewSelection? Selection { get; private set; }

    private void Sheet_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        _viewModel.RefreshPreview(true);

    private void RefreshButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        _viewModel.RefreshPreview(false);

    private void ContinueButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (!_viewModel.RefreshPreview(false))
            return;
        var names = _viewModel.MultiSheetMode
            ? _viewModel.Sheets
                .Where(sheet => sheet.IsSelected)
                .Select(sheet => sheet.Name)
                .ToArray()
            : _viewModel.SelectedSheet is null
                ? []
                : [_viewModel.SelectedSheet.Name];
        if (names.Length == 0)
        {
            _viewModel.DetectionSummary =
                _viewModel.SelectSheetMessage;
            return;
        }
        var options = names.ToDictionary(
            name => name,
            name => name == _viewModel.SelectedSheet?.Name
                ? _viewModel.CurrentOptions
                : new DownloaderTableOptions(),
            StringComparer.OrdinalIgnoreCase);
        Selection = new(names, options);
        DialogResult = true;
    }
}

internal sealed class DownloaderImportPreviewViewModel : ObservableObject
{
    private readonly string _filePath;
    private readonly DownloaderTableImporter _importer;
    private readonly LocalizationService _localization;
    private ImportSheetChoice? _selectedSheet;
    private bool _multiSheetMode;
    private string _headerRowText = string.Empty;
    private string _dataStartText = string.Empty;
    private string _detectionSummary = string.Empty;
    private DataView? _previewRows;

    public DownloaderImportPreviewViewModel(
        string filePath,
        DownloaderTableImporter importer,
        LocalizationService localization)
    {
        _filePath = filePath;
        _importer = importer;
        _localization = localization;
        Sheets = new(importer.GetSheetInfos(filePath)
            .Select(sheet => new ImportSheetChoice(
                sheet.Name,
                sheet.RowCount)));
        _selectedSheet = Sheets.FirstOrDefault();
        if (_selectedSheet is not null)
            _selectedSheet.IsSelected = true;
        IsDelimited = Path.GetExtension(filePath)
            .Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(filePath)
                .Equals(".tsv", StringComparison.OrdinalIgnoreCase);
        Encodings =
        [
            new("Auto", L("DownloaderImportAuto")),
            new("UTF-8", "UTF-8"),
            new("UTF-8 BOM", "UTF-8 BOM"),
            new("Windows-1251", "Windows-1251")
        ];
        Delimiters =
        [
            new("Auto", L("DownloaderImportAuto")),
            new(";", L("DownloaderDelimiterSemicolon")),
            new(",", L("DownloaderDelimiterComma")),
            new("\t", L("DownloaderDelimiterTab"))
        ];
    }

    public ObservableCollection<ImportSheetChoice> Sheets { get; }
    public IReadOnlyList<ChoiceItem> Encodings { get; }
    public IReadOnlyList<ChoiceItem> Delimiters { get; }
    public bool IsDelimited { get; }
    public string SelectedEncoding { get; set; } = "Auto";
    public string SelectedDelimiter { get; set; } = "Auto";
    public string Title => L("DownloaderImportPreviewTitle");
    public string SheetLabel => L("DownloaderSheet");
    public string MultipleSheetsLabel => L("DownloaderMultipleSheets");
    public string EncodingLabel => L("DownloaderEncoding");
    public string DelimiterLabel => L("DownloaderDelimiter");
    public string HeaderRowLabel => L("DownloaderHeaderRow");
    public string DataStartLabel => L("DownloaderDataStart");
    public string RefreshLabel => L("Refresh");
    public string PreviewHint => L("DownloaderPreviewHint");
    public string ContinueLabel => L("Continue");
    public string CancelLabel => L("Cancel");
    public string SelectSheetMessage => L("DownloaderSelectSheet");

    public ImportSheetChoice? SelectedSheet
    {
        get => _selectedSheet;
        set
        {
            if (!SetProperty(ref _selectedSheet, value) || value is null)
                return;
            if (!MultiSheetMode)
            {
                foreach (var sheet in Sheets)
                    sheet.IsSelected = ReferenceEquals(sheet, value);
            }
        }
    }

    public bool MultiSheetMode
    {
        get => _multiSheetMode;
        set
        {
            if (SetProperty(ref _multiSheetMode, value) &&
                !value &&
                SelectedSheet is not null)
            {
                foreach (var sheet in Sheets)
                    sheet.IsSelected =
                        ReferenceEquals(sheet, SelectedSheet);
            }
        }
    }

    public string HeaderRowText
    {
        get => _headerRowText;
        set => SetProperty(ref _headerRowText, value);
    }

    public string DataStartText
    {
        get => _dataStartText;
        set => SetProperty(ref _dataStartText, value);
    }

    public string DetectionSummary
    {
        get => _detectionSummary;
        set => SetProperty(ref _detectionSummary, value);
    }

    public DataView? PreviewRows
    {
        get => _previewRows;
        private set => SetProperty(ref _previewRows, value);
    }

    public DownloaderTableOptions CurrentOptions => new(
        SelectedEncoding == "Auto" ? null : SelectedEncoding,
        SelectedDelimiter switch
        {
            ";" => ';',
            "," => ',',
            "\t" => '\t',
            _ => null
        },
        ParseOneBased(HeaderRowText),
        ParseOneBased(DataStartText));

    public bool RefreshPreview(bool useDetected)
    {
        if (SelectedSheet is null)
            return false;
        try
        {
            var preview = _importer.Preview(
                _filePath,
                SelectedSheet.Name,
                CurrentOptions);
            if (useDetected)
            {
                HeaderRowText = (preview.HeaderRow + 1).ToString();
                DataStartText = (preview.DataStartRow + 1).ToString();
                if (IsDelimited)
                {
                    if (!string.IsNullOrWhiteSpace(preview.EncodingName))
                        SelectedEncoding = preview.EncodingName;
                    SelectedDelimiter = preview.Delimiter switch
                    {
                        ';' => ";",
                        ',' => ",",
                        '\t' => "\t",
                        _ => "Auto"
                    };
                }
            }
            PreviewRows = ToDataView(preview);
            DetectionSummary = string.Format(
                L("DownloaderDetectionSummary"),
                Path.GetFileName(_filePath),
                SelectedSheet.Name,
                preview.HeaderRow + 1,
                SelectedSheet.RowCount);
            return true;
        }
        catch (Exception exception)
        {
            DetectionSummary = exception.Message;
            return false;
        }
    }

    private static int? ParseOneBased(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!int.TryParse(value, out var number) || number < 1)
            throw new InvalidDataException(
                "Row number must be a positive integer.");
        return number - 1;
    }

    private static DataView ToDataView(
        RipperWorks.Core.DownloaderImportPreview preview)
    {
        var table = new DataTable();
        foreach (var header in preview.Headers)
            table.Columns.Add(header);
        foreach (var source in preview.Rows)
            table.Rows.Add(source.Cast<object>().ToArray());
        return table.DefaultView;
    }

    private string L(string key) => _localization.Get(key);
}

internal sealed class ImportSheetChoice(
    string name,
    int rowCount) : ObservableObject
{
    private bool _isSelected;
    public string Name { get; } = name;
    public int RowCount { get; } = rowCount;
    public string Display => $"{Name} — {RowCount}";
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
