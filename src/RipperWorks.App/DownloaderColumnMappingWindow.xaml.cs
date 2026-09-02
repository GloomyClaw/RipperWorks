using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;

namespace RipperWorks.App;

public partial class DownloaderColumnMappingWindow : Window
{
    private readonly DownloaderColumnMappingViewModel _viewModel;

    public DownloaderColumnMappingWindow(
        DownloaderImportPreview preview,
        LocalizationService localization,
        IReadOnlyDictionary<string, string>? initialMapping = null)
    {
        InitializeComponent();
        _viewModel = new(
            preview,
            localization,
            initialMapping);
        DataContext = _viewModel;
    }

    public IReadOnlyDictionary<string, string> Mapping =>
        _viewModel.Rows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.SelectedField))
            .ToDictionary(
                row => row.Header,
                row => row.SelectedField,
                StringComparer.OrdinalIgnoreCase);

    private void Mapping_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        _viewModel.UpdateValidation();

    private void AutoMapButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        _viewModel.AutoMap();

    private void ResetButton_OnClick(
        object sender,
        RoutedEventArgs e) =>
        _viewModel.Reset();

    private void ApplyButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.UpdateValidation();
        if (_viewModel.CanApply)
            DialogResult = true;
    }
}

internal sealed class DownloaderColumnMappingViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<string, string> _suggested;
    private readonly LocalizationService _localization;
    private string _validationMessage = string.Empty;
    private bool _canApply;

    public DownloaderColumnMappingViewModel(
        DownloaderImportPreview preview,
        LocalizationService localization,
        IReadOnlyDictionary<string, string>? initialMapping)
    {
        _suggested = preview.SuggestedMapping;
        _localization = localization;
        Fields = CreateFields();
        Rows = new(preview.Headers.Select((header, index) =>
        {
            var examples = preview.Rows
                .Select(row => index < row.Count
                    ? row[index]
                    : string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct()
                .Take(4)
                .ToArray();
            var selected = initialMapping?.GetValueOrDefault(
                    header,
                    _suggested.GetValueOrDefault(header, string.Empty)) ??
                _suggested.GetValueOrDefault(header, string.Empty);
            return new ImportMappingRow(
                header,
                examples.FirstOrDefault() ?? string.Empty,
                string.Join(Environment.NewLine, examples),
                selected,
                Fields);
        }));
        SheetTitle = string.Format(
            L("DownloaderMappingSheetTitle"),
            preview.SheetName);
        UpdateValidation();
    }

    public ObservableCollection<ImportMappingRow> Rows { get; }
    public IReadOnlyList<ChoiceItem> Fields { get; }
    public string Title => L("DownloaderMappingTitle");
    public string SheetTitle { get; }
    public string Hint => L("DownloaderMappingHint");
    public string SourceColumnLabel => L("DownloaderSourceColumn");
    public string ExampleLabel => L("DownloaderExampleValue");
    public string ApplicationFieldLabel =>
        L("DownloaderTargetField");
    public string AutoMapLabel => L("DownloaderAutoMap");
    public string ResetLabel => L("DownloaderResetMapping");
    public string ApplyLabel => L("Apply");
    public string CancelLabel => L("Cancel");

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public bool CanApply
    {
        get => _canApply;
        private set => SetProperty(ref _canApply, value);
    }

    public void AutoMap()
    {
        foreach (var row in Rows)
            row.SelectedField =
                _suggested.GetValueOrDefault(row.Header, string.Empty);
        UpdateValidation();
    }

    public void Reset()
    {
        foreach (var row in Rows)
            row.SelectedField = string.Empty;
        UpdateValidation();
    }

    public void UpdateValidation()
    {
        var mapped = Rows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.SelectedField))
            .ToArray();
        var duplicate = mapped
            .GroupBy(row => row.SelectedField)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            ValidationMessage = string.Format(
                L("DownloaderDuplicateMapping"),
                duplicate.Key);
            CanApply = false;
            return;
        }
        foreach (var required in new[] { "Name", "Url" })
        {
            if (mapped.Any(row => row.SelectedField == required))
                continue;
            ValidationMessage = string.Format(
                L("DownloaderRequiredMapping"),
                FieldDisplay(required));
            CanApply = false;
            return;
        }
        ValidationMessage = string.Empty;
        CanApply = true;
    }

    private IReadOnlyList<ChoiceItem> CreateFields() =>
    [
        new("", L("DownloaderIgnoreColumn")),
        new("Name", L("DisplayName")),
        new("Url", "URL"),
        new("AdditionalUrl", L("DownloaderAdditionalUrl")),
        new("Source", L("ColumnSource")),
        new("Category", L("Category")),
        new("Author", L("Author")),
        new("Version", L("ColumnVersion")),
        new("NexusModId", "mod_id"),
        new("NexusFileId", "file_id"),
        new("DownloadOrder", L("DownloaderDownloadOrder")),
        new("CatalogId", L("DownloaderCatalogId")),
        new("DecisionGroup", L("DownloaderDecisionGroup"))
    ];

    private string FieldDisplay(string value) =>
        Fields.First(field => field.Value == value).Display;

    private string L(string key) => _localization.Get(key);
}

internal sealed class ImportMappingRow(
    string header,
    string example,
    string examplesToolTip,
    string selectedField,
    IReadOnlyList<ChoiceItem> fields) : ObservableObject
{
    private string _selectedField = selectedField;
    public string Header { get; } = header;
    public string Example { get; } = example;
    public string ExamplesToolTip { get; } = examplesToolTip;
    public IReadOnlyList<ChoiceItem> Fields { get; } = fields;
    public string SelectedField
    {
        get => _selectedField;
        set => SetProperty(ref _selectedField, value);
    }
}
