using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;
using RipperWorks.Downloader;

namespace RipperWorks.App.Services;

public sealed record ManualDownloadEntry(
    string Name,
    string Category,
    DownloaderSource Source,
    string Author,
    string Url,
    string Version,
    string? ArchivePath);

public interface IDownloaderDialogService
{
    string? SelectImportFile();
    string? SelectExportFile();
    string? SelectArchiveFile();
    NexusFileInfo? SelectNexusFile(
        NexusModMetadata metadata,
        LocalizationService localization);
    ManualDownloadEntry? EditManual(
        DownloaderEntry? entry,
        LocalizationService localization);
    DownloaderPreparedImport? ConfigureImport(
        string filePath,
        DownloaderTableImporter importer,
        IReadOnlyList<DownloaderEntry> existing,
        LocalizationService localization);
    bool ConfirmDeleteRecords(
        IReadOnlyList<DownloaderEntry> entries,
        LocalizationService localization);
    void OpenUri(string uri);
    void OpenFile(string path);
    void OpenFolder(string path);
}

public sealed record DownloaderPreparedImport(
    IReadOnlyList<DownloaderEntry> Entries,
    bool ReplaceCatalog);

public sealed class DownloaderDialogService : IDownloaderDialogService
{
    public string? SelectImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter =
                "Tables|*.xlsx;*.xlsm;*.xls;*.csv;*.tsv|" +
                "All files|*.*",
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectExportFile()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV|*.csv",
            DefaultExt = ".csv",
            FileName = "ripperworks-downloads.csv"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectArchiveFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Mod archives|*.zip;*.7z;*.rar|All files|*.*",
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public NexusFileInfo? SelectNexusFile(
        NexusModMetadata metadata,
        LocalizationService localization)
    {
        var window = new NexusFileSelectionWindow(
            metadata,
            localization)
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true
            ? window.SelectedFile
            : null;
    }

    public ManualDownloadEntry? EditManual(
        DownloaderEntry? entry,
        LocalizationService localization)
    {
        var window = new ManualDownloadWindow(entry, localization)
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public DownloaderPreparedImport? ConfigureImport(
        string filePath,
        DownloaderTableImporter importer,
        IReadOnlyList<DownloaderEntry> existing,
        LocalizationService localization)
    {
        var previewWindow = new DownloaderImportWindow(
            filePath,
            importer,
            localization)
        {
            Owner = Application.Current?.MainWindow
        };
        if (previewWindow.ShowDialog() != true ||
            previewWindow.Selection is null)
        {
            return null;
        }

        var selectedSheets = new List<DownloaderImportSheet>();
        foreach (var sheetName in previewWindow.Selection.SheetNames)
        {
            var options = previewWindow.Selection.Options[sheetName];
            var preview = importer.Preview(
                filePath,
                sheetName,
                options);
            var mappingWindow = new DownloaderColumnMappingWindow(
                preview,
                localization)
            {
                Owner = Application.Current?.MainWindow
            };
            if (mappingWindow.ShowDialog() != true)
                return null;
            selectedSheets.Add(new DownloaderImportSheet(
                sheetName,
                options,
                mappingWindow.Mapping));
        }
        var prepared = importer.ImportSheets(
            filePath,
            selectedSheets);

        var summary = DownloaderImportPlanner.Analyze(
            existing,
            prepared);
        var summaryWindow = new DownloaderImportSummaryWindow(
            filePath,
            previewWindow.Selection.SheetNames,
            summary,
            existing.Count,
            localization)
        {
            Owner = Application.Current?.MainWindow
        };
        return summaryWindow.ShowDialog() == true
            ? new DownloaderPreparedImport(
                prepared,
                summaryWindow.ReplaceCatalog)
            : null;
    }

    public bool ConfirmDeleteRecords(
        IReadOnlyList<DownloaderEntry> entries,
        LocalizationService localization)
    {
        if (entries.Count == 1)
        {
            return MessageBox.Show(
                Application.Current?.MainWindow,
                string.Format(
                    localization.Get("DownloaderDeleteQuestion"),
                    entries[0].Name),
                localization.Get("DownloaderDeleteRecord"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        var activeCount = entries.Count(entry =>
            entry.Status is DownloaderStatus.Downloading or
                DownloaderStatus.Waiting or
                DownloaderStatus.Paused);
        var window = new DownloaderBatchDeleteWindow(
            entries.Count,
            activeCount,
            localization)
        {
            Owner = Application.Current?.MainWindow
        };
        return window.ShowDialog() == true;
    }

    public void OpenUri(string uri) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = uri,
            UseShellExecute = true
        });

    public void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });

    public void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
}
