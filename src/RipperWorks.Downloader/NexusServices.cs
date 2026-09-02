using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed class NexusApiClient(HttpClient client)
    : INexusApiClient, INexusUpdateApiClient
{
    private static readonly Uri BaseUri =
        new("https://api.nexusmods.com/");

    public async Task<NexusUserInfo> ValidateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            "v1/users/validate.json",
            apiKey,
            cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<UserDto>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Nexus returned an empty response.");
        return new(
            value.Name ?? "Nexus user",
            value.IsPremium,
            value.IsSupporter);
    }

    public async Task<NexusModMetadata> GetModAsync(
        string gameDomain,
        long modId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var modResponse = await SendAsync(
            $"v1/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}.json",
            apiKey,
            cancellationToken);
        using var modDocument = await JsonDocument.ParseAsync(
            await modResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = modDocument.RootElement;

        using var filesResponse = await SendAsync(
            $"v1/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files.json",
            apiKey,
            cancellationToken);
        using var filesDocument = await JsonDocument.ParseAsync(
            await filesResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var files = new List<NexusFileInfo>();
        if (filesDocument.RootElement.TryGetProperty(
                "files",
                out var fileArray) &&
            fileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in fileArray.EnumerateArray())
            {
                var fileId = GetInt64(file, "file_id");
                if (fileId is null)
                    continue;
                files.Add(new NexusFileInfo(
                    fileId.Value,
                    GetString(file, "file_name") ??
                    $"nexus-{modId}-{fileId}.zip",
                    GetString(file, "name") ?? "Не определено",
                    GetString(file, "version") ?? string.Empty,
                    GetInt64(file, "size_in_bytes") ?? 0,
                    ToTimestamp(GetInt64(file, "uploaded_timestamp"))));
            }
        }

        return new NexusModMetadata(
            gameDomain,
            modId,
            GetString(root, "name") ?? $"Nexus mod {modId}",
            GetString(root, "author") ?? string.Empty,
            GetString(root, "category_name") ?? "Не определено",
            GetString(root, "summary") ??
            GetString(root, "description") ?? string.Empty,
            ToTimestamp(GetInt64(root, "updated_timestamp")),
            files.OrderByDescending(file => file.UpdatedAt).ToArray(),
            GetString(root, "version") ?? string.Empty);
    }

    public async Task<NexusModMetadata> GetModMetadataOnlyAsync(
        string gameDomain,
        long modId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var modResponse = await SendAsync(
            $"v1/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}.json",
            apiKey,
            cancellationToken);
        using var modDocument = await JsonDocument.ParseAsync(
            await modResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = modDocument.RootElement;

        return new NexusModMetadata(
            gameDomain,
            modId,
            GetString(root, "name") ?? $"Nexus mod {modId}",
            GetString(root, "author") ?? string.Empty,
            GetString(root, "category_name") ?? string.Empty,
            GetString(root, "summary") ??
            GetString(root, "description") ?? string.Empty,
            ToTimestamp(GetInt64(root, "updated_timestamp")),
            [],
            GetString(root, "version") ?? string.Empty);
    }

    public async Task<Uri> GetDownloadLinkAsync(
        NxmLink link,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var relative =
            $"v1/games/{Uri.EscapeDataString(link.GameDomain)}/mods/" +
            $"{link.ModId}/files/{link.FileId}/download_link.json" +
            $"?key={Uri.EscapeDataString(link.Key)}&expires={link.Expires}";
        using var response = await SendAsync(
            relative,
            apiKey,
            cancellationToken);
        return await ReadDownloadUriAsync(response, cancellationToken);
    }

    public async Task<IReadOnlySet<long>> GetModFileIdsAsync(
        string gameDomain,
        long modId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            $"v1/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files.json",
            apiKey,
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<long>();
        }
        return files.EnumerateArray()
            .Select(file => GetInt64(file, "file_id"))
            .Where(fileId => fileId is not null)
            .Select(fileId => fileId!.Value)
            .ToHashSet();
    }

    public async Task<NexusUpdateFileVersion?>
        GetFileVersionByGameScopedIdAsync(
            string gameDomain,
            long numericFileId,
            string apiKey,
            CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(
                $"v3/games/{Uri.EscapeDataString(gameDomain)}/" +
                $"mod-file-versions/{numericFileId}",
                apiKey,
                cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty(
                    "data",
                    out var data)
                ? ReadUpdateFileVersion(data)
                : null;
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<NexusUpdateFileVersion>>
        GetFileVersionsAsync(
            string modFileUuid,
            string apiKey,
            CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            $"v3/mod-files/{Uri.EscapeDataString(modFileUuid)}/versions",
            apiKey,
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return versions.EnumerateArray()
            .Select(ReadUpdateFileVersion)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();
    }

    public async Task<Uri> GetDownloadLinkAsync(
        string gameDomain,
        long modId,
        long fileId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var relative =
            $"v1/games/{Uri.EscapeDataString(gameDomain)}/mods/" +
            $"{modId}/files/{fileId}/download_link.json";
        using var response = await SendAsync(
            relative,
            apiKey,
            cancellationToken);
        return await ReadDownloadUriAsync(response, cancellationToken);
    }

    private static async Task<Uri> ReadDownloadUriAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var values = await response.Content.ReadFromJsonAsync<DownloadDto[]>(
            cancellationToken: cancellationToken);
        var url = values?
            .Select(value => value.Uri)
            .FirstOrDefault(value =>
                Uri.TryCreate(value, UriKind.Absolute, out _));
        return url is null
            ? throw new InvalidDataException(
                "Nexus did not return a permitted download link.")
            : new Uri(url);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string relative,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Nexus API key is not configured.");
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(BaseUri, relative));
            request.Headers.Add("apikey", apiKey.Trim());
            NexusClientIdentity.ApplyHeaders(request);
            var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return response;
            if (response.StatusCode == (HttpStatusCode)429 &&
                attempt == 0)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ??
                    TimeSpan.FromSeconds(30);
                response.Dispose();
                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;
                await Task.Delay(delay, cancellationToken);
                continue;
            }
            var statusCode = response.StatusCode;
            response.Dispose();
            if (statusCode is HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden)
            {
                throw new NexusAuthenticationException(
                    "Nexus rejected the API key.");
            }
            var message = statusCode == (HttpStatusCode)429
                ? "Nexus request limit exceeded. Try again later."
                : $"Nexus API error {(int)statusCode}.";
            throw new HttpRequestException(message, null, statusCode);
        }
    }

    private static NexusUpdateFileVersion? ReadUpdateFileVersion(
        JsonElement value)
    {
        var numericFileId = GetInt64(value, "game_scoped_id");
        var versionUuid = GetString(value, "id");
        if (numericFileId is null ||
            string.IsNullOrWhiteSpace(versionUuid) ||
            !value.TryGetProperty("file", out var file))
        {
            return null;
        }
        var modFileUuid = GetString(file, "id");
        if (string.IsNullOrWhiteSpace(modFileUuid))
            return null;
        var positionText = GetString(value, "position");
        _ = decimal.TryParse(
            positionText,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var position);
        DateTimeOffset? uploadedAt = null;
        if (GetString(value, "uploaded_at") is { } uploaded &&
            DateTimeOffset.TryParse(
                uploaded,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsedUploaded))
        {
            uploadedAt = parsedUploaded;
        }
        return new(
            numericFileId.Value,
            versionUuid,
            modFileUuid,
            GetString(value, "name") ?? string.Empty,
            GetString(value, "version") ?? string.Empty,
            position,
            uploadedAt);
    }

    private static string? GetString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? GetInt64(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.Number
                    when property.TryGetInt64(out var number) => number,
                JsonValueKind.String
                    when long.TryParse(
                        property.GetString(),
                        out var parsed) => parsed,
                _ => null
            }
            : null;

    private static DateTimeOffset? ToTimestamp(long? timestamp) =>
        timestamp is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)
            : null;

    private sealed record UserDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("is_premium")] bool IsPremium,
        [property: JsonPropertyName("is_supporter")] bool IsSupporter);

    private sealed record DownloadDto(
        [property: JsonPropertyName("URI")] string? Uri);
}

public sealed record NexusUrlResolution(
    NexusModMetadata Metadata,
    NexusFileInfo? SelectedFile,
    bool RequiresFileSelection);

public sealed class NexusDownloadCoordinator(
    IDownloaderRepository repository,
    INexusApiClient nexus,
    IProtectedCredentialStore credentials,
    IDownloadQueue queue,
    DownloaderTechnicalLog? technicalLog = null)
    : INexusEntryDownloadCoordinator
{
    public async Task<NexusUrlResolution> ResolveUrlAsync(
        string value,
        CancellationToken cancellationToken = default)
    {
        if (!DownloaderLinkParser.TryParseNexusPage(value, out var page) ||
            page is null)
        {
            throw new InvalidOperationException("Unsupported Nexus URL.");
        }
        return await credentials.UseAsync(
            CredentialIdentity.NexusDefault,
            async (apiKey, token) =>
            {
                var metadata = await nexus.GetModAsync(
                    page.GameDomain,
                    page.ModId,
                    apiKey,
                    token);
                var selected = page.FileId is null
                    ? metadata.Files.Count == 1 ? metadata.Files[0] : null
                    : metadata.Files.FirstOrDefault(file =>
                        file.FileId == page.FileId.Value);
                return new NexusUrlResolution(
                    metadata,
                    selected,
                    selected is null && metadata.Files.Count > 1);
            },
            cancellationToken);
    }

    public async Task<NexusModMetadata> GetModMetadataAsync(
        string gameDomain,
        long modId,
        CancellationToken cancellationToken = default)
    {
        return await credentials.UseAsync(
            CredentialIdentity.NexusDefault,
            (apiKey, token) => nexus.GetModMetadataOnlyAsync(gameDomain, modId, apiKey, token),
            cancellationToken);
    }

    public async Task<DownloaderEntry> AddResolvedAsync(
        NexusModMetadata metadata,
        NexusFileInfo file,
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        var existing = (await repository.LoadEntriesAsync(cancellationToken))
            .FirstOrDefault(entry =>
                entry.NexusModId == metadata.ModId &&
                entry.NexusFileId == file.FileId &&
                string.Equals(
                    entry.GameDomain,
                    metadata.GameDomain,
                    StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ApplyMetadata(existing, metadata, file);
            await repository.SaveEntryAsync(existing, cancellationToken);
            return existing;
        }
        var entry = CreateEntry(metadata, file, sourceUrl);
        await repository.SaveEntryAsync(entry, cancellationToken);
        return entry;
    }

    public async Task<DownloaderEntry> ProcessNxmAsync(
        string value,
        CancellationToken cancellationToken = default)
    {
        if (!DownloaderLinkParser.TryParseNxm(value, out var link) ||
            link is null)
        {
            throw new InvalidOperationException("Invalid NXM link.");
        }
        return await credentials.UseAsync(
            CredentialIdentity.NexusDefault,
            async (apiKey, token) =>
            {
                var metadata = await nexus.GetModAsync(
                    link.GameDomain,
                    link.ModId,
                    apiKey,
                    token);
                var file = metadata.Files.FirstOrDefault(candidate =>
                    candidate.FileId == link.FileId) ??
                    new NexusFileInfo(
                        link.FileId,
                        $"nexus-{link.ModId}-{link.FileId}.zip",
                        "Не определено",
                        string.Empty,
                        0,
                        null);
                var entry = await AddResolvedAsync(
                    metadata,
                    file,
                    $"https://www.nexusmods.com/{link.GameDomain}/mods/{link.ModId}" +
                    $"?file_id={link.FileId}",
                    token);
                if (entry.Status == DownloaderStatus.Downloaded)
                    return entry;
                var downloadUri = await nexus.GetDownloadLinkAsync(
                    link,
                    apiKey,
                    token);
                await queue.EnqueueAsync(entry, downloadUri, token);
                return entry;
            },
            cancellationToken);
    }

    public async Task<DownloaderEntry> ProcessNxmForEntryAsync(
        Guid entryId,
        string value,
        CancellationToken cancellationToken = default)
    {
        if (!DownloaderLinkParser.TryParseNxm(value, out var link) ||
            link is null)
        {
            throw new InvalidOperationException("Invalid NXM link.");
        }
        var entry = await repository.LoadEntryAsync(
            entryId,
            cancellationToken) ??
            throw new InvalidOperationException(
                "The current Downloader entry no longer exists.");
        technicalLog?.Invoke("NexusFileCaptured", entry, null);
        if (entry.NexusModId is { } expectedMod &&
            (expectedMod != link.ModId ||
             !string.Equals(
                 entry.GameDomain,
                 link.GameDomain,
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The selected Nexus file belongs to another mod.");
        }
        return await credentials.UseAsync(
            CredentialIdentity.NexusDefault,
            async (apiKey, token) =>
            {
                var metadata = await nexus.GetModAsync(
                    link.GameDomain,
                    link.ModId,
                    apiKey,
                    token);
                var file = metadata.Files.FirstOrDefault(candidate =>
                    candidate.FileId == link.FileId) ??
                    new NexusFileInfo(
                        link.FileId,
                        $"nexus-{link.ModId}-{link.FileId}.zip",
                        "Не определено",
                        string.Empty,
                        0,
                        null);
                ApplyMetadata(entry, metadata, file);
                await repository.SaveEntryAsync(entry, token);
                technicalLog?.Invoke("MetadataUpdated", entry, null);
                var downloadUri = await nexus.GetDownloadLinkAsync(
                    link,
                    apiKey,
                    token);
                await queue.EnqueueAsync(entry, downloadUri, token);
                return entry;
            },
            cancellationToken);
    }

    public async Task<DownloaderEntry> QueueKnownFileAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        var entry = await repository.LoadEntryAsync(
            entryId,
            cancellationToken) ??
            throw new InvalidOperationException(
                "The Downloader entry no longer exists.");
        if (entry.NexusModId is not { } modId ||
            entry.NexusFileId is not { } fileId)
        {
            throw new InvalidOperationException(
                "A precise Nexus file has not been selected.");
        }
        return await credentials.UseAsync(
            CredentialIdentity.NexusDefault,
            async (apiKey, token) =>
            {
                var metadata = await nexus.GetModAsync(
                    entry.GameDomain,
                    modId,
                    apiKey,
                    token);
                var file = metadata.Files.FirstOrDefault(candidate =>
                    candidate.FileId == fileId) ??
                    new NexusFileInfo(
                        fileId,
                        $"nexus-{modId}-{fileId}.zip",
                        "Не определено",
                        string.Empty,
                        0,
                        null);
                ApplyMetadata(entry, metadata, file);
                await repository.SaveEntryAsync(entry, token);
                var downloadUri = await nexus.GetDownloadLinkAsync(
                    entry.GameDomain,
                    modId,
                    fileId,
                    apiKey,
                    token);
                await queue.EnqueueAsync(entry, downloadUri, token);
                return entry;
            },
            cancellationToken);
    }

    public async Task<DownloaderEntry> QueueDirectForEntryAsync(
        Guid entryId,
        Uri downloadUri,
        CancellationToken cancellationToken = default)
    {
        var entry = await repository.LoadEntryAsync(
            entryId,
            cancellationToken) ??
            throw new InvalidOperationException(
                "The Downloader entry no longer exists.");
        entry.AdditionalUrl = downloadUri.ToString();
        await repository.SaveEntryAsync(entry, cancellationToken);
        await queue.EnqueueAsync(entry, downloadUri, cancellationToken);
        return entry;
    }

    private static DownloaderEntry CreateEntry(
        NexusModMetadata metadata,
        NexusFileInfo file,
        string sourceUrl) =>
        new()
        {
            Name = metadata.Name,
            Category = metadata.Category,
            Source = DownloaderSource.Nexus,
            Author = metadata.Author,
            Url = sourceUrl,
            GameDomain = metadata.GameDomain,
            NexusModId = metadata.ModId,
            NexusFileId = file.FileId,
            Version = NexusVersionResolver.Resolve(
                file.Version,
                metadata.Version,
                string.Empty),
            ArchiveFileName = NexusMetadataMerge.IsMeaningful(
                file.FileName)
                ? file.FileName
                : string.Empty,
            Status = DownloaderStatus.Ready,
            Size = file.Size,
            Description = string.IsNullOrWhiteSpace(file.Name)
                ? metadata.Description
                : file.Name,
            UpdatedAt = file.UpdatedAt ?? metadata.UpdatedAt
        };

    private static void ApplyMetadata(
        DownloaderEntry entry,
        NexusModMetadata metadata,
        NexusFileInfo file) =>
        NexusMetadataMerge.Apply(entry, metadata, file);
}

public static class NexusMetadataMerge
{
    private static readonly HashSet<string> EmptyValues =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Не определено",
            "Сверить на странице",
            "Сверить в разделе Files",
            "—",
            "-"
        };

    public static bool IsMeaningful(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) &&
            !EmptyValues.Contains(normalized);
    }

    public static void Apply(
        DownloaderEntry entry,
        NexusModMetadata metadata,
        NexusFileInfo file)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Source = DownloaderSource.Nexus;
        entry.GameDomain = metadata.GameDomain;
        entry.NexusModId = metadata.ModId;
        entry.NexusFileId = file.FileId;
        if (IsMeaningful(metadata.Name))
            entry.Name = metadata.Name.Trim();
        entry.Version = NexusVersionResolver.Resolve(
            file.Version,
            metadata.Version,
            entry.Version);
        if (IsMeaningful(file.FileName))
            entry.ArchiveFileName = file.FileName.Trim();
        if (file.Size > 0)
            entry.Size = file.Size;
        if (IsMeaningful(metadata.Author))
            entry.Author = metadata.Author.Trim();
        if (IsMeaningful(metadata.Category))
            entry.Category = metadata.Category.Trim();
        if (IsMeaningful(file.Name))
            entry.Description = file.Name.Trim();
        else if (IsMeaningful(metadata.Description))
            entry.Description = metadata.Description.Trim();
        entry.UpdatedAt =
            file.UpdatedAt ??
            metadata.UpdatedAt ??
            entry.UpdatedAt;
        entry.Status = DownloaderStatus.Ready;
    }
}

public static class NexusVersionResolver
{
    private static readonly HashSet<string> Placeholders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Сверить в разделе Files",
            "Сверить на странице",
            "Не определено",
            "—",
            "-",
            "Check on page",
            "Check in Files",
            "Undefined"
        };

    public static string Resolve(
        string? fileVersion,
        string? modVersion,
        string? currentVersion)
    {
        foreach (var value in new[]
                 {
                     fileVersion,
                     modVersion,
                     currentVersion
                 })
        {
            var normalized = value?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !Placeholders.Contains(normalized))
            {
                return normalized;
            }
        }
        return "Не определено";
    }
}
