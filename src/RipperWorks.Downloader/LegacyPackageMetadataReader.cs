using System.Text.Json;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed class LegacyPackageMetadataReader : IPackageMetadataReader
{
    public async Task<PackageMetadata> ReadAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fallbackName = Path.GetFileNameWithoutExtension(archivePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath))
            ?? throw new ArgumentException("Archive path has no parent directory.", nameof(archivePath));
        var metadataPath = Path.Combine(directory, "metadata.json");
        if (!File.Exists(metadataPath))
            return CreateFallback(fallbackName, PackageMetadataSources.None);

        try
        {
            await using var stream = new FileStream(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return CreateFallback(fallbackName, PackageMetadataSources.Unknown);

            if (IsPascalCaseForm(root))
                return ReadPascalCase(root, fallbackName);
            if (IsCamelCaseForm(root))
                return ReadCamelCase(root, fallbackName);

            return CreateFallback(fallbackName, PackageMetadataSources.Unknown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return CreateFallback(fallbackName, PackageMetadataSources.Corrupt);
        }
    }

    private static bool IsPascalCaseForm(JsonElement root) =>
        root.TryGetProperty("DisplayName", out _) ||
        root.TryGetProperty("ModId", out _) ||
        root.TryGetProperty("FileId", out _) ||
        root.TryGetProperty("SourceUrl", out _);

    private static bool IsCamelCaseForm(JsonElement root) =>
        root.TryGetProperty("recordId", out _) ||
        root.TryGetProperty("name", out _) ||
        root.TryGetProperty("nexusModId", out _) ||
        root.TryGetProperty("fileName", out _);

    private static PackageMetadata ReadPascalCase(
        JsonElement root,
        string fallbackName)
    {
        var declaredSource = GetString(root, "MetadataSource");
        return new PackageMetadata
        {
            DisplayName = GetString(root, "DisplayName") ?? fallbackName,
            Source = PackageSource.Nexus,
            GameDomain = GetString(root, "GameDomain"),
            Nexus = new(
                GetInt64(root, "ModId"),
                GetInt64(root, "FileId"),
                GetString(root, "SourceUrl")),
            NexusFileUuid = GetString(root, "NexusFileUuid"),
            ArchiveFamilyKey = GetString(root, "ArchiveFamilyKey"),
            Version = GetString(root, "Version"),
            Author = GetString(root, "Author"),
            Category = GetString(root, "Category"),
            Sha256 = GetString(root, "Sha256") ??
                GetString(root, "Fingerprint"),
            MetadataSource = declaredSource
                ?? PackageMetadataSources.LegacyDownloaderPascalCase,
            MetadataSchemaVersion = GetInt32(root, "SchemaVersion") ?? 0,
            HasMetadata = true
        };
    }

    private static PackageMetadata ReadCamelCase(
        JsonElement root,
        string fallbackName)
    {
        var source = ParseSource(GetString(root, "source"));
        var declaredSource = GetString(root, "metadataSource");
        return new PackageMetadata
        {
            DisplayName = GetString(root, "name") ?? fallbackName,
            Source = source,
            GameDomain = GetString(root, "gameDomain"),
            Nexus = new(
                GetInt64(root, "nexusModId"),
                GetInt64(root, "nexusFileId"),
                GetString(root, "originalUrl")),
            NexusFileUuid = GetString(root, "nexusFileUuid"),
            ArchiveFamilyKey = GetString(root, "archiveFamilyKey"),
            Version = GetString(root, "version"),
            Author = GetString(root, "author"),
            Category = GetString(root, "category"),
            Sha256 = GetString(root, "sha256") ??
                GetString(root, "fingerprint"),
            MetadataSource = declaredSource
                ?? PackageMetadataSources.LegacyDownloaderCamelCase,
            MetadataSchemaVersion = GetInt32(root, "schemaVersion") ?? 0,
            HasMetadata = true
        };
    }

    private static PackageMetadata CreateFallback(
        string fallbackName,
        string metadataSource) =>
        new()
        {
            DisplayName = fallbackName,
            Source = PackageSource.Local,
            Nexus = new(null, null, null),
            MetadataSource = metadataSource,
            HasMetadata = false
        };

    private static PackageSource ParseSource(string? source) =>
        source?.Trim().ToLowerInvariant() switch
        {
            "nexus" => PackageSource.Nexus,
            "manual" => PackageSource.Manual,
            "other" => PackageSource.Other,
            _ => PackageSource.Local
        };

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static long? GetInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static int? GetInt32(JsonElement root, string name)
    {
        var number = GetInt64(root, name);
        return number is >= int.MinValue and <= int.MaxValue
            ? (int)number.Value
            : null;
    }
}
