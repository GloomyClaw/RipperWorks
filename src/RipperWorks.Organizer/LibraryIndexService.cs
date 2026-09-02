using RipperWorks.Core;

namespace RipperWorks.Organizer;

public sealed class LibraryIndexService(
    IPackageMetadataReader metadataReader,
    IDownloaderArchiveMetadataLookup? downloaderMetadata = null)
    : ILibraryIndexService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip",
            ".7z",
            ".rar"
        };

    public async Task<LibraryIndexResult> IndexAsync(
        string libraryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(libraryRoot));
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException(
                $"Library directory does not exist: {normalizedRoot}");

        var packages = new List<PackageRecord>();
        var pendingDirectories = new Stack<string>();
        var visitedDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var indexedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var skippedDirectories = 0;
        pendingDirectories.Push(normalizedRoot);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            if (!visitedDirectories.Add(directory))
                continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception exception) when (IsAccessFailure(exception))
            {
                skippedDirectories++;
                continue;
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SupportedExtensions.Contains(Path.GetExtension(filePath)))
                    continue;

                try
                {
                    var fullPath = Path.GetFullPath(filePath);
                    if (!indexedPaths.Add(fullPath))
                        continue;

                    var file = new FileInfo(fullPath);
                    var metadata = await metadataReader
                        .ReadAsync(fullPath, cancellationToken)
                        .ConfigureAwait(false);
                    var sha256 = metadata.Sha256;
                    if (string.IsNullOrWhiteSpace(sha256))
                    {
                        sha256 = await ComputeSha256Async(
                                fullPath,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    if (string.IsNullOrWhiteSpace(metadata.Version) &&
                        metadata.Source == PackageSource.Nexus &&
                        !string.IsNullOrWhiteSpace(metadata.GameDomain) &&
                        metadata.Nexus.ModId is { } modId &&
                        metadata.Nexus.FileId is { } fileId &&
                        downloaderMetadata is not null)
                    {
                        var fallback = await downloaderMetadata.FindAsync(
                                metadata.GameDomain,
                                modId,
                                fileId,
                                sha256,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (fallback is not null)
                        {
                            metadata = metadata with
                            {
                                Version = fallback.Version,
                                Sha256 = fallback.Sha256 ?? sha256
                            };
                            sha256 = metadata.Sha256;
                        }
                    }
                    packages.Add(CreateRecord(file, metadata, sha256));
                }
                catch (Exception exception) when (IsAccessFailure(exception))
                {
                    // A single inaccessible or disappearing archive must not fail the index.
                }
            }

            string[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (IsAccessFailure(exception))
            {
                skippedDirectories++;
                continue;
            }

            foreach (var child in childDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(child);
                    if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                        pendingDirectories.Push(Path.GetFullPath(child));
                }
                catch (Exception exception) when (IsAccessFailure(exception))
                {
                    skippedDirectories++;
                }
            }
        }

        return new(
            packages
                .OrderBy(package => package.DisplayName,
                    StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(package => package.ArchivePath,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            skippedDirectories);
    }

    private static PackageRecord CreateRecord(
        FileInfo file,
        PackageMetadata metadata,
        string sha256)
    {
        var lastWriteUtc = DateTime.SpecifyKind(
            file.LastWriteTimeUtc,
            DateTimeKind.Utc);
        return new PackageRecord
        {
            PackageId = PackageId.CreateTemporary(
                file.FullName,
                file.Length,
                lastWriteUtc),
            ArchivePath = file.FullName,
            DisplayName = metadata.DisplayName
                ?? Path.GetFileNameWithoutExtension(file.Name),
            ArchiveFileName = file.Name,
            ArchiveFormat = file.Extension.TrimStart('.').ToUpperInvariant(),
            FileSize = file.Length,
            LastWriteUtc = lastWriteUtc,
            Source = metadata.Source,
            GameDomain = metadata.GameDomain,
            NexusModId = metadata.Nexus.ModId,
            NexusFileId = metadata.Nexus.FileId,
            NexusUrl = metadata.Nexus.Url,
            NexusFileUuid = metadata.NexusFileUuid,
            ArchiveFamilyKey = metadata.ArchiveFamilyKey,
            Version = metadata.Version,
            Author = metadata.Author,
            Category = metadata.Category,
            Sha256 = sha256,
            DownloadedAtUtc = lastWriteUtc,
            MetadataSource = metadata.MetadataSource,
            MetadataSchemaVersion = metadata.MetadataSchemaVersion,
            HasMetadata = metadata.HasMetadata
        };
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var algorithm = System.Security.Cryptography.SHA256.Create();
        var hash = await algorithm.ComputeHashAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsAccessFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException;
}
