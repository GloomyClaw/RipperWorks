using RipperWorks.Core;

namespace RipperWorks.Downloader;

internal enum ArchivePublicationOrigin
{
    DownloadJob,
    ManualAttach
}

internal enum PublicationFaultPoint
{
    AfterIntentPersisted,
    AfterArchiveCommitted,
    AfterMetadataCommitted,
    AfterCatalogCommitted,
    BeforeDownloaderTerminalCommit,
    AfterDownloaderTerminalCommitted,
    BeforeSourceCleanup,
    AfterArchiveReplayTempVerified,
    AfterMetadataReplayTempVerified,
    AfterSourceCleanupVerified
}

internal enum PublicationPhase
{
    Intent,
    Archive,
    Metadata,
    Catalog,
    DownloaderTerminal
}

internal sealed record PublicationIntent(
    string PublicationId,
    Guid EntryId,
    string SourcePath,
    string LibraryRoot,
    string ArchivePath,
    string MetadataPath,
    string Sha256,
    long Size,
    ArchivePublicationOrigin Origin)
{
    public static PublicationIntent FromPersisted(
        DownloaderEntry entry,
        string libraryRoot)
        => FromPersisted(
            entry,
            libraryRoot,
            PublicationPhaseState.Origin(entry.Status));

    public static PublicationIntent FromPersisted(
        DownloaderEntry entry,
        string libraryRoot,
        ArchivePublicationOrigin origin)
    {
        var root = PublicationPathSafety.NormalizeRoot(libraryRoot);
        var archive = PublicationPathSafety.RequireContainedPath(
            root,
            entry.LocalArchivePath);
        var source = string.IsNullOrWhiteSpace(entry.TemporaryPath)
            ? string.Empty
            : Path.GetFullPath(entry.TemporaryPath);
        var sha = NormalizeSha(entry.Sha256);
        if (entry.Size <= 0)
            throw new InvalidDataException("Publication size is missing.");
        return new(
            CreateId(entry.Id, sha, archive),
            entry.Id,
            source,
            root,
            archive,
            Path.Combine(Path.GetDirectoryName(archive)!, "metadata.json"),
            sha,
            entry.Size,
            origin);
    }

    public static string CreateId(
        Guid entryId,
        string sha256,
        string archivePath)
    {
        var value = string.Join(
            "\n",
            entryId.ToString("N"),
            NormalizeSha(sha256),
            Path.GetFullPath(archivePath).ToUpperInvariant());
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    public static string NormalizeSha(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 ||
            normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                "Publication SHA-256 is missing or malformed.");
        }
        return normalized;
    }
}

internal static class PublicationPhaseState
{
    internal static ArchivePublicationOrigin Origin(DownloaderStatus status) =>
        status is DownloaderStatus.ManualPublicationIntentPersisted or
            DownloaderStatus.ManualPublicationArchiveCommitted or
            DownloaderStatus.ManualPublicationMetadataCommitted or
            DownloaderStatus.ManualPublicationCatalogCommitted or
            DownloaderStatus.ManualPublicationDownloaderTerminalCommitted
            ? ArchivePublicationOrigin.ManualAttach
            : PublicationStatusPolicy.IsPending(status)
                ? ArchivePublicationOrigin.DownloadJob
                : throw new InvalidDataException(
                    $"Status is not a publication phase: {status}.");

    internal static bool Is(
        DownloaderStatus status,
        PublicationPhase phase) =>
        status == For(phase, Origin(status));

    internal static DownloaderStatus For(
        PublicationPhase phase,
        ArchivePublicationOrigin origin) =>
        (phase, origin) switch
        {
            (PublicationPhase.Intent, ArchivePublicationOrigin.DownloadJob) =>
                DownloaderStatus.PublicationIntentPersisted,
            (PublicationPhase.Archive, ArchivePublicationOrigin.DownloadJob) =>
                DownloaderStatus.PublicationArchiveCommitted,
            (PublicationPhase.Metadata, ArchivePublicationOrigin.DownloadJob) =>
                DownloaderStatus.PublicationMetadataCommitted,
            (PublicationPhase.Catalog, ArchivePublicationOrigin.DownloadJob) =>
                DownloaderStatus.PublicationCatalogCommitted,
            (PublicationPhase.DownloaderTerminal,
                ArchivePublicationOrigin.DownloadJob) =>
                DownloaderStatus.PublicationDownloaderTerminalCommitted,
            (PublicationPhase.Intent, ArchivePublicationOrigin.ManualAttach) =>
                DownloaderStatus.ManualPublicationIntentPersisted,
            (PublicationPhase.Archive, ArchivePublicationOrigin.ManualAttach) =>
                DownloaderStatus.ManualPublicationArchiveCommitted,
            (PublicationPhase.Metadata, ArchivePublicationOrigin.ManualAttach) =>
                DownloaderStatus.ManualPublicationMetadataCommitted,
            (PublicationPhase.Catalog, ArchivePublicationOrigin.ManualAttach) =>
                DownloaderStatus.ManualPublicationCatalogCommitted,
            (PublicationPhase.DownloaderTerminal,
                ArchivePublicationOrigin.ManualAttach) =>
                DownloaderStatus.ManualPublicationDownloaderTerminalCommitted,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
}

internal sealed class PublicationConflictException(
    string code,
    string message) : IOException(message)
{
    public string Code { get; } = code;
}
