using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RipperWorks.Core;

public enum DownloaderStatus
{
    Added,
    Waiting,
    FetchingNexus,
    Ready,
    Downloading,
    Paused,
    Downloaded,
    ManualActionRequired,
    Error,
    Canceled,
    FileMissing,
    FileCorrupted,
    PublicationIntentPersisted,
    PublicationArchiveCommitted,
    PublicationMetadataCommitted,
    PublicationCatalogCommitted,
    PublicationDownloaderTerminalCommitted,
    ManualPublicationIntentPersisted,
    ManualPublicationArchiveCommitted,
    ManualPublicationMetadataCommitted,
    ManualPublicationCatalogCommitted,
    ManualPublicationDownloaderTerminalCommitted
}

public static class DownloaderStatusPolicy
{
    public static bool CanStart(DownloaderStatus status) =>
        status is DownloaderStatus.Added or
            DownloaderStatus.Ready or
            DownloaderStatus.Canceled or
            DownloaderStatus.ManualActionRequired or
            DownloaderStatus.FileMissing or
            DownloaderStatus.FileCorrupted;

    public static bool CanRetry(DownloaderStatus status) =>
        status is DownloaderStatus.Error or
            DownloaderStatus.FileMissing or
            DownloaderStatus.FileCorrupted;
}

public enum DownloaderSource
{
    Nexus,
    Direct,
    Manual,
    Import
}

public enum NexusUpdateCheckStatus
{
    NotChecked,
    Checking,
    UpToDate,
    UpdateAvailable,
    ManualReviewRequired,
    CurrentFileUnavailable,
    MissingIdentity,
    ApiError,
    OlderVersion,
    DifferentComponent,
    UnknownComparison
}

public sealed class DownloaderEntry : INotifyPropertyChanged
{
    private bool _isSelected;
    private DownloaderStatus _status;
    private double _progress;
    private double _bytesPerSecond;
    private NexusUpdateCheckStatus _updateCheckStatus;
    private string _availableVersion = string.Empty;
    private long? _availableFileId;
    private string _availableFileUuid = string.Empty;
    private DateTimeOffset? _lastUpdateCheckUtc;
    private string _updateCheckMessage = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public int Number { get; set; }
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DownloaderSource Source { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string AdditionalUrl { get; set; } = string.Empty;
    public string GameDomain { get; set; } = "cyberpunk2077";
    public long? NexusModId { get; set; }
    public long? NexusFileId { get; set; }
    public string NexusFileUuid { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public NexusUpdateCheckStatus UpdateCheckStatus
    {
        get => _updateCheckStatus;
        set => Set(ref _updateCheckStatus, value);
    }
    public string AvailableVersion
    {
        get => _availableVersion;
        set => Set(ref _availableVersion, value);
    }
    public long? AvailableFileId
    {
        get => _availableFileId;
        set => Set(ref _availableFileId, value);
    }
    public string AvailableFileUuid
    {
        get => _availableFileUuid;
        set => Set(ref _availableFileUuid, value);
    }
    public DateTimeOffset? LastUpdateCheckUtc
    {
        get => _lastUpdateCheckUtc;
        set => Set(ref _lastUpdateCheckUtc, value);
    }
    public string UpdateCheckMessage
    {
        get => _updateCheckMessage;
        set => Set(ref _updateCheckMessage, value);
    }
    public int? DownloadOrder { get; set; }
    public string CatalogId { get; set; } = string.Empty;
    public string DecisionGroup { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string ArchiveFileName { get; set; } = string.Empty;
    public string Sha256Display => string.IsNullOrWhiteSpace(Sha256)
        ? "—"
        : Sha256.Length <= 20
            ? Sha256
            : $"{Sha256[..12]}…{Sha256[^6..]}";
    public DownloaderStatus Status
    {
        get => _status;
        set => Set(ref _status, value);
    }
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }
    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        set => Set(ref _bytesPerSecond, value);
    }
    public long Size { get; set; }
    public string LocalArchivePath { get; set; } = string.Empty;
    public string TemporaryPath { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyAll()
    {
        foreach (var property in GetType().GetProperties())
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(property.Name));
    }

    public void Notify(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }

    private void Set<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record DownloaderDownloadRecord
{
    public Guid DownloadId { get; init; } = Guid.NewGuid();
    public required Guid EntryId { get; init; }
    public required string SourceUrl { get; init; }
    public required string TemporaryPath { get; init; }
    public long BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
    public DownloaderStatus Status { get; init; }
    public string Error { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record NxmLink(
    string GameDomain,
    long ModId,
    long FileId,
    string Key,
    long Expires);

public sealed record NexusPageLink(
    string GameDomain,
    long ModId,
    long? FileId);

public sealed record NexusFileInfo(
    long FileId,
    string FileName,
    string Name,
    string Version,
    long Size,
    DateTimeOffset? UpdatedAt);

public sealed record NexusModMetadata(
    string GameDomain,
    long ModId,
    string Name,
    string Author,
    string Category,
    string Description,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<NexusFileInfo> Files,
    string Version = "");

public sealed record NexusUpdateFileVersion(
    long NumericFileId,
    string VersionUuid,
    string ModFileUuid,
    string Name,
    string Version,
    decimal Position,
    DateTimeOffset? UploadedAt);

public sealed record NexusModUpdateCheckResult(
    NexusUpdateCheckStatus Status,
    NexusUpdateFileVersion? CurrentFile,
    NexusUpdateFileVersion? LatestFileInSameChain,
    bool IsCurrentLatest,
    bool IsChainAmbiguous,
    bool CurrentFileUnavailable,
    string Message);

public sealed record NexusModUpdateCheckProgress(
    int Completed,
    int Total,
    DownloaderEntry? UpdatedEntry,
    int UpdatesFound = 0);

public sealed record NexusModUpdateCheckSummary(
    int Checked,
    int UpToDate,
    int UpdateAvailable,
    int ManualReviewRequired,
    int MissingIdentity,
    int Errors,
    bool Canceled,
    bool AuthenticationFailed);

public sealed record DownloaderProgress(
    Guid EntryId,
    long DownloadedBytes,
    long? TotalBytes,
    double BytesPerSecond)
{
    public double Percent => TotalBytes > 0
        ? Math.Clamp(
            (double)DownloadedBytes / TotalBytes.Value * 100,
            0,
            100)
        : 0;
}

public delegate void DownloaderTechnicalLog(
    string stage,
    DownloaderEntry entry,
    Exception? exception);

public sealed record DownloadTransportResponse(
    Stream Content,
    long? TotalBytes,
    bool RangeAccepted,
    string? FileName = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record DownloaderImportPreview(
    string SheetName,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyDictionary<string, string> SuggestedMapping,
    bool MappingAmbiguous,
    string EncodingName = "",
    char? Delimiter = null,
    int HeaderRow = 0,
    int DataStartRow = 1);

public sealed record DownloaderImportCommitResult(
    int Added,
    int Updated,
    int Unchanged,
    int Deleted,
    int FinalTotal);

public interface IDownloaderRepository
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloaderEntry>> LoadEntriesAsync(
        CancellationToken cancellationToken = default);
    Task<DownloaderEntry?> LoadEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
    Task SaveEntryAsync(
        DownloaderEntry entry,
        CancellationToken cancellationToken = default);
    Task DeleteEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
    Task DeleteEntriesAsync(
        IReadOnlyCollection<Guid> entryIds,
        CancellationToken cancellationToken = default);
    Task<DownloaderImportCommitResult> ApplyImportAsync(
        IReadOnlyList<DownloaderEntry> entries,
        bool replaceCatalog,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloaderDownloadRecord>> LoadDownloadsAsync(
        CancellationToken cancellationToken = default);
    Task SaveDownloadAsync(
        DownloaderDownloadRecord download,
        CancellationToken cancellationToken = default);
}

public interface ILibraryArchiveLookup
{
    Task<string?> FindArchivePathAsync(
        string gameDomain,
        long nexusModId,
        long nexusFileId,
        CancellationToken cancellationToken = default);
}

public interface INexusApiClient
{
    Task<NexusModMetadata> GetModAsync(
        string gameDomain,
        long modId,
        CancellationToken cancellationToken = default);
    Task<NexusModMetadata> GetModMetadataOnlyAsync(
        string gameDomain,
        long modId,
        CancellationToken cancellationToken = default);
    Task<Uri> GetDownloadLinkAsync(
        NxmLink link,
        CancellationToken cancellationToken = default);
    Task<Uri> GetDownloadLinkAsync(
        string gameDomain,
        long modId,
        long fileId,
        CancellationToken cancellationToken = default);
}

public interface INexusUpdateApiClient
{
    Task<IReadOnlySet<long>> GetModFileIdsAsync(
        string gameDomain,
        long modId,
        CancellationToken cancellationToken = default);
    Task<NexusUpdateFileVersion?> GetFileVersionByGameScopedIdAsync(
        string gameDomain,
        long numericFileId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusUpdateFileVersion>> GetFileVersionsAsync(
        string modFileUuid,
        CancellationToken cancellationToken = default);
}

public interface INexusModUpdateCheckClient
{
    Task<IReadOnlyDictionary<long, NexusModUpdateCheckResult>> CheckModAsync(
        string gameDomain,
        long modId,
        IReadOnlyCollection<long> numericFileIds,
        CancellationToken cancellationToken = default);
}

public interface INexusModUpdateCheckService
{
    Task<NexusModUpdateCheckResult> CheckEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
    Task<NexusModUpdateCheckSummary> CheckAllAsync(
        IProgress<NexusModUpdateCheckProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public static class NexusAuthenticatedFeatureAvailability
{
    public const string UnavailableMessage =
        "Authenticated Nexus functionality is temporarily unavailable while " +
        "OAuth integration is pending Nexus Mods approval.";
}

public sealed class NexusAuthenticatedFeatureUnavailableException()
    : HttpRequestException(NexusAuthenticatedFeatureAvailability.UnavailableMessage);

public interface INexusEntryDownloadCoordinator
{
    Task<DownloaderEntry> ProcessNxmForEntryAsync(
        Guid entryId,
        string value,
        CancellationToken cancellationToken = default);
    Task<DownloaderEntry> QueueKnownFileAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
    Task<DownloaderEntry> QueueDirectForEntryAsync(
        Guid entryId,
        Uri downloadUri,
        CancellationToken cancellationToken = default);
}

public interface IDownloadTransport
{
    Task<DownloadTransportResponse> OpenReadAsync(
        Uri uri,
        long offset,
        CancellationToken cancellationToken = default);
}

public interface INxmProtocolRegistration
{
    void Register(string executablePath);
    void Unregister();
    bool IsRegistered(string executablePath);
}

public sealed record InstanceMessage(
    string Argument,
    bool IsActivationOnly);

public interface ISingleInstanceCoordinator : IAsyncDisposable
{
    event EventHandler<InstanceMessage>? MessageReceived;
    Task<bool> StartAsync(
        string? argument,
        CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public static partial class DownloaderLinkParser
{
    [GeneratedRegex(
        @"^nxm://(?<game>[^/]+)/mods/(?<mod>\d+)/files/(?<file>\d+)\?(?<query>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NxmRegex();

    [GeneratedRegex(
        @"^https://(?:www\.)?nexusmods\.com/(?<game>[^/]+)/mods/(?<mod>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NexusRegex();

    public static bool TryParseNxm(string? value, out NxmLink? link)
    {
        link = null;
        var match = NxmRegex().Match(value?.Trim() ?? string.Empty);
        if (!match.Success)
            return false;
        var query = ParseQuery(match.Groups["query"].Value);
        if (!query.TryGetValue("key", out var key) ||
            !query.TryGetValue("expires", out var expiresText) ||
            !long.TryParse(expiresText, out var expires))
        {
            return false;
        }
        link = new(
            match.Groups["game"].Value,
            long.Parse(match.Groups["mod"].Value),
            long.Parse(match.Groups["file"].Value),
            key,
            expires);
        return true;
    }

    public static bool TryParseNexusPage(
        string? value,
        out NexusPageLink? link)
    {
        link = null;
        var text = value?.Trim() ?? string.Empty;
        var match = NexusRegex().Match(text);
        if (!match.Success)
            return false;
        long? fileId = null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var query = ParseQuery(uri.Query.TrimStart('?'));
            foreach (var key in new[] { "file_id", "fileid" })
            {
                if (query.TryGetValue(key, out var raw) &&
                    long.TryParse(raw, out var parsed))
                {
                    fileId = parsed;
                    break;
                }
            }
        }
        link = new(
            match.Groups["game"].Value,
            long.Parse(match.Groups["mod"].Value),
            fileId);
        return true;
    }

    public static Uri BuildNexusFilesPageUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!TryParseNexusPage(uri.AbsoluteUri, out _))
            return uri;
        var builder = new UriBuilder(uri);
        var values = builder.Query
            .TrimStart('?')
            .Split(
                '&',
                StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var tabFound = false;
        var result = new List<string>(values.Count + 1);
        foreach (var value in values)
        {
            var separator = value.IndexOf('=');
            var rawName = separator < 0
                ? value
                : value[..separator];
            var name = Uri.UnescapeDataString(rawName);
            if (!name.Equals(
                    "tab",
                    StringComparison.OrdinalIgnoreCase))
            {
                result.Add(value);
                continue;
            }
            if (!tabFound)
            {
                result.Add("tab=files");
                tabFound = true;
            }
        }
        if (!tabFound)
            result.Insert(0, "tab=files");
        builder.Query = string.Join("&", result);
        return builder.Uri;
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
}
