using System.Text.Json;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

internal sealed class PublicationFileStore
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".7z", ".rar"
        };

    internal async Task<(long Size, string Sha256)> InspectSourceAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "Publication source is missing.",
                fullPath);
        var source = new FileInfo(fullPath);
        if (source.Length <= 0)
            throw new InvalidDataException("Downloaded archive is empty.");
        if (!SupportedExtensions.Contains(source.Extension))
        {
            throw new InvalidDataException(
                $"Archive format {source.Extension} is not supported by the Library.");
        }
        var sha = await ArchiveSha256.ComputeAsync(
            fullPath,
            cancellationToken).ConfigureAwait(false);
        return (source.Length, PublicationIntent.NormalizeSha(sha));
    }

    internal async Task EnsureArchiveAsync(
        PublicationIntent intent,
        Func<Task> afterReplayVerification,
        CancellationToken cancellationToken)
    {
        PublicationPathSafety.RevalidateForMutation(
            intent.LibraryRoot,
            intent.ArchivePath);
        if (File.Exists(intent.ArchivePath))
        {
            await VerifyFileAsync(
                intent.ArchivePath,
                intent.Size,
                intent.Sha256,
                "FinalArchiveMismatch",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(intent.SourcePath) ||
            !File.Exists(intent.SourcePath))
        {
            throw new PublicationConflictException(
                "PublicationSourceAndFinalMissing",
                "Neither the publication source nor the final archive exists.");
        }
        await VerifyFileAsync(
            intent.SourcePath,
            intent.Size,
            intent.Sha256,
            "PublicationSourceMismatch",
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(
                intent.SourcePath,
                intent.ArchivePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(intent.ArchivePath)!;
        PublicationPathSafety.CreateDirectories(
            intent.LibraryRoot,
            directory);
        var temporary = intent.ArchivePath +
                        $".{intent.PublicationId[..12]}.ripperworks.tmp";
        PublicationPathSafety.RevalidateForMutation(
            intent.LibraryRoot,
            intent.ArchivePath,
            temporary);
        if (File.Exists(temporary))
        {
            await VerifyFileAsync(
                temporary,
                intent.Size,
                intent.Sha256,
                "ArchiveTemporaryMismatch",
                cancellationToken).ConfigureAwait(false);
            await afterReplayVerification().ConfigureAwait(false);
            PublicationPathSafety.RevalidateForMutation(
                intent.LibraryRoot,
                intent.ArchivePath,
                temporary);
            File.Move(temporary, intent.ArchivePath);
            await VerifyFileAsync(
                intent.ArchivePath,
                intent.Size,
                intent.Sha256,
                "FinalArchiveMismatch",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        var createdTemporary = false;
        try
        {
            await using (var input = new FileStream(
                             intent.SourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             128 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.WriteThrough))
            {
                createdTemporary = true;
                await input.CopyToAsync(
                    output,
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(
                    cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            await VerifyFileAsync(
                temporary,
                intent.Size,
                intent.Sha256,
                "ArchiveTemporaryVerificationFailed",
                cancellationToken).ConfigureAwait(false);
            PublicationPathSafety.RevalidateForMutation(
                intent.LibraryRoot,
                intent.ArchivePath,
                temporary);
            File.Move(temporary, intent.ArchivePath);
            await VerifyFileAsync(
                intent.ArchivePath,
                intent.Size,
                intent.Sha256,
                "FinalArchiveMismatch",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (createdTemporary && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal async Task EnsureMetadataAsync(
        DownloaderEntry entry,
        PublicationIntent intent,
        Func<Task> afterReplayVerification,
        CancellationToken cancellationToken)
    {
        await VerifyFileAsync(
            intent.ArchivePath,
            intent.Size,
            intent.Sha256,
            "FinalArchiveMismatch",
            cancellationToken).ConfigureAwait(false);
        if (File.Exists(intent.MetadataPath))
        {
            await VerifyMetadataAsync(
                entry,
                intent,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var document = CreateMetadata(entry, intent);
        var temporary = intent.MetadataPath +
                        $".{intent.PublicationId[..12]}.tmp";
        PublicationPathSafety.RevalidateForMutation(
            intent.LibraryRoot,
            intent.MetadataPath,
            temporary);
        if (File.Exists(temporary))
        {
            await VerifyMetadataAsync(
                entry,
                intent with { MetadataPath = temporary },
                cancellationToken).ConfigureAwait(false);
            await afterReplayVerification().ConfigureAwait(false);
            PublicationPathSafety.RevalidateForMutation(
                intent.LibraryRoot,
                intent.MetadataPath,
                temporary);
            File.Move(temporary, intent.MetadataPath);
            await VerifyMetadataAsync(
                entry,
                intent,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        var createdTemporary = false;
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.WriteThrough))
            {
                createdTemporary = true;
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    MetadataJsonContext.Default.PublicationMetadataDocument,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            PublicationPathSafety.RevalidateForMutation(
                intent.LibraryRoot,
                intent.MetadataPath,
                temporary);
            File.Move(temporary, intent.MetadataPath);
            await VerifyMetadataAsync(
                entry,
                intent,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (createdTemporary && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal async Task VerifyMetadataAsync(
        DownloaderEntry entry,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                intent.MetadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);
            using var json = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            var recordId = ReadString(root, "recordId");
            var fileName = ReadString(root, "fileName");
            var sha = ReadString(root, "sha256") ??
                      ReadString(root, "fingerprint");
            var size = root.TryGetProperty("size", out var sizeElement) &&
                       sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : -1;
            var modId = ReadInt64(root, "nexusModId");
            var fileId = ReadInt64(root, "nexusFileId");
            if (!Guid.TryParse(recordId, out var parsedRecordId) ||
                parsedRecordId != entry.Id ||
                !string.Equals(
                    fileName,
                    Path.GetFileName(intent.ArchivePath),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    sha,
                    intent.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                size != intent.Size ||
                modId != entry.NexusModId ||
                fileId != entry.NexusFileId)
            {
                throw new PublicationConflictException(
                    "PublicationMetadataMismatch",
                    "Existing metadata belongs to a different archive publication.");
            }
        }
        catch (PublicationConflictException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or
                UnauthorizedAccessException)
        {
            throw new PublicationConflictException(
                "PublicationMetadataUnreadable",
                "Publication metadata cannot be verified.");
        }
    }

    internal static async Task VerifyFileAsync(
        string path,
        long expectedSize,
        string expectedSha,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) ||
            new FileInfo(path).Length != expectedSize)
        {
            throw new PublicationConflictException(
                errorCode,
                $"Publication file size does not match: {path}");
        }
        var actual = await ArchiveSha256.ComputeAsync(
            path,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                actual,
                expectedSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PublicationConflictException(
                errorCode,
                $"Publication file hash does not match: {path}");
        }
    }

    private static PublicationMetadataDocument CreateMetadata(
        DownloaderEntry entry,
        PublicationIntent intent) =>
        new(
            1,
            entry.Id,
            entry.Name,
            entry.Category,
            entry.Source.ToString(),
            entry.Author,
            entry.Url,
            entry.GameDomain,
            entry.NexusModId,
            entry.NexusFileId,
            entry.NexusFileUuid,
            string.IsNullOrWhiteSpace(entry.DecisionGroup)
                ? null
                : entry.DecisionGroup,
            entry.Version,
            Path.GetFileName(intent.ArchivePath),
            intent.Size,
            intent.Sha256,
            intent.Sha256,
            entry.UpdatedAt,
            DateTimeOffset.UtcNow,
            entry.Description,
            entry.Source == DownloaderSource.Nexus
                ? "Nexus"
                : "RipperWorksDownloader");

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;
}

internal sealed record PublicationMetadataDocument(
    int SchemaVersion,
    Guid RecordId,
    string Name,
    string Category,
    string Source,
    string Author,
    string OriginalUrl,
    string GameDomain,
    long? NexusModId,
    long? NexusFileId,
    string NexusFileUuid,
    string? ArchiveFamilyKey,
    string Version,
    string FileName,
    long Size,
    string Fingerprint,
    string Sha256,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset DownloadedAt,
    string Description,
    string MetadataSource);

[System.Text.Json.Serialization.JsonSerializable(
    typeof(PublicationMetadataDocument))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
internal partial class MetadataJsonContext :
    System.Text.Json.Serialization.JsonSerializerContext;
