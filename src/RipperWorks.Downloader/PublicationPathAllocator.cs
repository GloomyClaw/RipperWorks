using RipperWorks.Core;

namespace RipperWorks.Downloader;

internal sealed class PublicationPathAllocator
{
    internal async Task<string> SelectIntendedArchivePathAsync(
        DownloaderEntry entry,
        string sourcePath,
        string libraryRoot,
        string sha256,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entry.LocalArchivePath) &&
            !string.IsNullOrWhiteSpace(entry.Sha256) &&
            string.Equals(
                entry.Sha256,
                sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            var persistedPath = PublicationPathSafety.RequireContainedPath(
                libraryRoot,
                entry.LocalArchivePath);
            if (CanReusePersistedPath(entry, persistedPath))
                return persistedPath;
        }
        var source = new FileInfo(sourcePath);
        var folder = BuildFolder(entry, libraryRoot);
        var candidate = Path.Combine(
            folder,
            SelectArchiveName(entry, source));
        candidate = PublicationPathSafety.RequireContainedPath(
            libraryRoot,
            candidate);
        if (!File.Exists(candidate))
            return candidate;
        var actual = await ArchiveSha256.ComputeAsync(
            candidate,
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            return candidate;
        throw new PublicationConflictException(
            "PublicationFinalPathCollision",
            "The intended final archive path contains different bytes.");
    }

    private static bool CanReusePersistedPath(
        DownloaderEntry entry,
        string archivePath)
    {
        if (entry.Source != DownloaderSource.Nexus ||
            entry.NexusFileId is not { } fileId)
        {
            return true;
        }
        var directory = Path.GetDirectoryName(archivePath);
        return directory is not null &&
               string.Equals(
                   Path.GetFileName(directory),
                   fileId.ToString(
                       System.Globalization.CultureInfo.InvariantCulture),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFolder(
        DownloaderEntry entry,
        string libraryRoot)
    {
        var sourceFolder = entry.Source switch
        {
            DownloaderSource.Nexus => "Nexus",
            DownloaderSource.Manual => "Manual",
            _ => "Other"
        };
        return Path.Combine(
            libraryRoot,
            "Downloads",
            sourceFolder,
            Sanitize(entry.Category, 80),
            Sanitize(entry.Name, 120),
            entry.NexusFileId?.ToString() ?? entry.Id.ToString("N"));
    }

    private static string SelectArchiveName(
        DownloaderEntry entry,
        FileInfo source)
    {
        var value = NexusMetadataMerge.IsMeaningful(entry.ArchiveFileName)
            ? entry.ArchiveFileName.Trim()
            : entry.Source == DownloaderSource.Manual
                ? source.Name
                : BuildFallbackArchiveName(entry, source.Extension);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(value)))
            value += source.Extension;
        return Sanitize(value, 180);
    }

    private static string BuildFallbackArchiveName(
        DownloaderEntry entry,
        string extension)
    {
        var parts = new List<string> { entry.Name };
        if (NexusMetadataMerge.IsMeaningful(entry.Version))
            parts.Add(entry.Version);
        if (entry.NexusFileId is { } fileId)
            parts.Add(fileId.ToString());
        var value = string.Join(
            "-",
            parts.Where(NexusMetadataMerge.IsMeaningful));
        return (string.IsNullOrWhiteSpace(value)
                ? entry.Id.ToString("N")
                : value) + extension;
    }

    private static string Sanitize(string value, int maxLength)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string((string.IsNullOrWhiteSpace(value)
                ? "Не определено"
                : value)
            .Select(character =>
                invalid.Contains(character) ||
                character is '<' or '>' or ':' or '"' or '/' or '\\' or '|'
                    or '?' or '*'
                    ? '_'
                    : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        return text.Length <= maxLength
            ? text
            : text[..maxLength].TrimEnd(' ', '.');
    }
}
