namespace RipperWorks.Infrastructure;

public sealed class RipperWorksPaths
{
    public static string DefaultDataRoot
    {
        get
        {
            var overrideRoot = Environment.GetEnvironmentVariable(
                "RIPPERWORKS_DATA_ROOT");
            return string.IsNullOrWhiteSpace(overrideRoot)
                ? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "RipperWorks")
                : Path.GetFullPath(overrideRoot);
        }
    }

    public RipperWorksPaths(string? dataRoot = null)
    {
        DataRoot = Path.GetFullPath(dataRoot ?? DefaultDataRoot);
        EnsureCreated();
    }

    public string DataRoot { get; }
    public string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public string ShortlistPath =>
        Path.Combine(DataRoot, "nexus-shortlist.json");
    public string DatabaseDirectory => Path.Combine(DataRoot, "Database");
    public string LogsDirectory => Path.Combine(DataRoot, "Logs");
    public string TempDirectory => Path.Combine(DataRoot, "Temp");
    public string WebView2Directory => Path.Combine(DataRoot, "WebView2");
    public string ContentDirectory => Path.Combine(DataRoot, "Content");
    public string StagingDirectory => Path.Combine(DataRoot, "Staging");
    public string DownloaderDatabasePath =>
        Path.Combine(DataRoot, "downloader.db");
    public string CatalogDatabasePath =>
        Path.Combine(DataRoot, "catalog.db");
    public string NexusKeyPath => Path.Combine(DataRoot, "nexus.key");
    public string DownloaderDirectory =>
        Path.Combine(DataRoot, "Downloads");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        foreach (var directory in new[]
                 {
                     DatabaseDirectory,
                     LogsDirectory,
                     TempDirectory,
                     WebView2Directory,
                     ContentDirectory,
                     StagingDirectory,
                     DownloaderDirectory
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
