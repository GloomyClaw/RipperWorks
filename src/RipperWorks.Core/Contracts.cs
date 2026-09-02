namespace RipperWorks.Core;

public interface IRipperWorksSettingsStore
{
    Task<RipperWorksSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(
        RipperWorksSettings settings,
        CancellationToken cancellationToken = default);
}

public interface IPackageMetadataReader
{
    Task<PackageMetadata> ReadAsync(
        string archivePath,
        CancellationToken cancellationToken = default);
}

public interface ILibraryIndexService
{
    Task<LibraryIndexResult> IndexAsync(
        string libraryRoot,
        CancellationToken cancellationToken = default);
}

public sealed record DownloaderArchiveMetadata(
    string Version,
    string? Sha256);

public interface IDownloaderArchiveMetadataLookup
{
    Task<DownloaderArchiveMetadata?> FindAsync(
        string gameDomain,
        long nexusModId,
        long nexusFileId,
        string? sha256,
        CancellationToken cancellationToken = default);
}
