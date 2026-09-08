using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public enum NexusModernRequirementOutcome
{
    Complete,
    AmbiguousSource,
    Failed
}

public sealed record NexusModernRequirementResult
{
    private NexusModernRequirementResult(
        NexusModernRequirementOutcome outcome,
        NexusRequirementSnapshot? snapshot,
        string? selectedFileVersionId,
        NexusRequirementFailure? failure)
    {
        Outcome = outcome;
        Snapshot = snapshot;
        SelectedFileVersionId = selectedFileVersionId;
        Failure = failure;
    }

    public NexusModernRequirementOutcome Outcome { get; }
    public NexusRequirementSnapshot? Snapshot { get; }
    public string? SelectedFileVersionId { get; }
    public NexusRequirementFailure? Failure { get; }

    public static NexusModernRequirementResult Complete(
        NexusRequirementSnapshot snapshot,
        string selectedFileVersionId) =>
        new(NexusModernRequirementOutcome.Complete, snapshot, selectedFileVersionId, null);

    public static NexusModernRequirementResult Ambiguous(string reason) =>
        new(NexusModernRequirementOutcome.AmbiguousSource, null, null, null);

    public static NexusModernRequirementResult Failed(NexusRequirementFailure failure) =>
        new(NexusModernRequirementOutcome.Failed, null, null, failure);
}

public interface INexusModernRequirementClient
{
    Task<NexusModernRequirementResult> GetModernRequirementsAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken = default);
}

public sealed class NexusV3RequirementClient : INexusModernRequirementClient
{
    public const string DefaultBaseUrl = "https://api.nexusmods.com/v3";
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public NexusV3RequirementClient(
        HttpClient client,
        string baseUrl = DefaultBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<NexusModernRequirementResult> GetModernRequirementsAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken = default)
    {
        if (mod.GameId <= 0 || mod.ModId <= 0)
        {
            return NexusModernRequirementResult.Failed(new(
                NexusRequirementFailureKind.MalformedIdentity,
                "The queried Nexus mod identity must contain positive IDs."));
        }

        if (!TryGetGameDomain(mod.GameId, out var gameDomain))
        {
            return NexusModernRequirementResult.Failed(new(
                NexusRequirementFailureKind.MalformedIdentity,
                $"Unsupported game ID {mod.GameId}."));
        }

        return await ExecuteQueryAsync(
            mod,
            gameDomain,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<NexusModernRequirementResult> ExecuteQueryAsync(
        NexusModIdentity mod,
        string gameDomain,
        CancellationToken cancellationToken)
    {
        try
        {
            // Step 1: GET /v3/games/{domain}/mods/{modId}
            var modRes = await GetV3DocAsync($"/games/{gameDomain}/mods/{mod.ModId}", cancellationToken).ConfigureAwait(false);
            if (modRes.Failure != null) return NexusModernRequirementResult.Failed(modRes.Failure);
            using var modDoc = modRes.Doc;
            if (modDoc == null || !modDoc.RootElement.TryGetProperty("data", out var modData))
            {
                return NexusModernRequirementResult.Failed(new(
                    NexusRequirementFailureKind.InvalidJson,
                    $"Failed to resolve global mod ID for {gameDomain}/{mod.ModId}."));
            }

            var globalModId = modData.GetProperty("id").GetString();

            // Step 2: GET /v3/mods/{globalModId}/files
            var filesRes = await GetV3DocAsync($"/mods/{globalModId}/files", cancellationToken).ConfigureAwait(false);
            if (filesRes.Failure != null) return NexusModernRequirementResult.Failed(filesRes.Failure);
            using var filesDoc = filesRes.Doc;
            if (filesDoc == null || !filesDoc.RootElement.TryGetProperty("data", out var filesData))
            {
                return NexusModernRequirementResult.Failed(new(
                    NexusRequirementFailureKind.InvalidJson,
                    $"Failed to retrieve ModFiles for global mod ID {globalModId}."));
            }

            var modFiles = filesData.GetProperty("mod_files");
            var activeFiles = new List<JsonElement>();
            foreach (var mf in modFiles.EnumerateArray())
            {
                if (mf.TryGetProperty("is_active", out var isActive) && isActive.GetBoolean())
                {
                    activeFiles.Add(mf);
                }
            }

            if (activeFiles.Count == 0)
            {
                return NexusModernRequirementResult.Ambiguous("No active ModFiles found.");
            }
            if (activeFiles.Count > 1)
            {
                return NexusModernRequirementResult.Ambiguous($"Multiple active ModFiles found ({activeFiles.Count}).");
            }

            var activeFileId = activeFiles[0].GetProperty("id").GetString();

            // Step 3: GET /v3/mod-files/{activeFileId}/versions
            var versionsRes = await GetV3DocAsync($"/mod-files/{activeFileId}/versions", cancellationToken).ConfigureAwait(false);
            if (versionsRes.Failure != null) return NexusModernRequirementResult.Failed(versionsRes.Failure);
            using var versionsDoc = versionsRes.Doc;
            if (versionsDoc == null || !versionsDoc.RootElement.TryGetProperty("data", out var versionsData))
            {
                return NexusModernRequirementResult.Failed(new(
                    NexusRequirementFailureKind.InvalidJson,
                    $"Failed to retrieve versions for ModFile {activeFileId}."));
            }

            // Official OpenAPI ModFileWithAggregates.is_active defines active categories as
            // exactly: main, update, optional, miscellaneous.
            // Positive whitelist / fail-closed: a version is an applicable current source
            // candidate ONLY when its category explicitly matches one of these four values.
            // Null, missing, unrecognized, or future category values are NOT active.
            var versions = versionsData.GetProperty("versions");
            var applicableVersions = new List<JsonElement>();
            foreach (var v in versions.EnumerateArray())
            {
                var category = v.TryGetProperty("category", out var c) ? c.GetString() : null;
                if (string.Equals(category, "main", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "update", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "optional", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "miscellaneous", StringComparison.OrdinalIgnoreCase))
                {
                    applicableVersions.Add(v);
                }
            }

            if (applicableVersions.Count == 0)
            {
                return NexusModernRequirementResult.Ambiguous("No applicable published versions found.");
            }
            if (applicableVersions.Count > 1)
            {
                return NexusModernRequirementResult.Ambiguous($"Multiple applicable published versions found ({applicableVersions.Count}).");
            }

            var versionId = applicableVersions[0].GetProperty("id").GetString() ??
                applicableVersions[0].GetProperty("id").GetRawText();
            if (string.IsNullOrWhiteSpace(versionId))
            {
                return NexusModernRequirementResult.Failed(new(
                    NexusRequirementFailureKind.InvalidJson,
                    "Failed to parse version ID."));
            }

            // Step 4: GET /v3/mod-file-versions/{versionId}/dependencies/ranges
            var depRes = await GetV3DocAsync($"/mod-file-versions/{versionId}/dependencies/ranges", cancellationToken).ConfigureAwait(false);
            if (depRes.Failure != null) return NexusModernRequirementResult.Failed(depRes.Failure);
            using var depDoc = depRes.Doc;
            if (depDoc == null || !depDoc.RootElement.TryGetProperty("dependency_definitions", out var definitions))
            {
                return NexusModernRequirementResult.Failed(new(
                    NexusRequirementFailureKind.InvalidJson,
                    $"Failed to retrieve dependency ranges for version {versionId}."));
            }

            var rawCount = definitions.GetArrayLength();

            if (rawCount == 0)
            {
                var emptySnapshot = new NexusRequirementSnapshot(
                    new NexusRequirementSnapshotOwner(mod, NexusRequirementTraversal.ForwardRequirements),
                    []);
                return NexusModernRequirementResult.Complete(emptySnapshot, versionId);
            }

            var edges = new List<NexusModRequirementEdge>();
            var seenLogicalMods = new HashSet<NexusModIdentity>();

            foreach (var dep in definitions.EnumerateArray())
            {
                var defId = dep.TryGetProperty("id", out var idElem) ? (idElem.GetString() ?? idElem.GetRawText()) : null;
                if (!dep.TryGetProperty("ranges", out var rangesElem) || rangesElem.ValueKind != JsonValueKind.Array)
                {
                    return NexusModernRequirementResult.Failed(new(
                        NexusRequirementFailureKind.InvalidJson,
                        $"Dependency definition '{defId}' is missing a valid ranges array."));
                }

                if (rangesElem.GetArrayLength() == 0)
                {
                    return NexusModernRequirementResult.Failed(new(
                        NexusRequirementFailureKind.InvalidJson,
                        $"Dependency definition '{defId}' contains zero ranges."));
                }

                var defTargetMods = new HashSet<NexusModIdentity>();
                NexusModRequirementTarget? resolvedTarget = null;

                foreach (var range in rangesElem.EnumerateArray())
                {
                    if (!range.TryGetProperty("target_mod_file", out var targetModFile) ||
                        targetModFile.ValueKind != JsonValueKind.Object ||
                        !targetModFile.TryGetProperty("mod", out var targetMod) ||
                        targetMod.ValueKind != JsonValueKind.Object)
                    {
                        return NexusModernRequirementResult.Failed(new(
                            NexusRequirementFailureKind.InvalidJson,
                            $"Dependency definition '{defId}' range is missing target_mod_file.mod."));
                    }

                    if (!targetMod.TryGetProperty("game_scoped_id", out var scopedIdElem))
                    {
                        return NexusModernRequirementResult.Failed(new(
                            NexusRequirementFailureKind.InvalidJson,
                            $"Dependency definition '{defId}' range target mod is missing game_scoped_id."));
                    }
                    var scopedIdStr = scopedIdElem.GetString() ?? scopedIdElem.GetRawText();
                    if (!long.TryParse(scopedIdStr, out var targetModId) || targetModId <= 0)
                    {
                        return NexusModernRequirementResult.Failed(new(
                            NexusRequirementFailureKind.InvalidJson,
                            $"Dependency definition '{defId}' range target mod contains invalid game_scoped_id '{scopedIdStr}'."));
                    }

                    long targetGameId = 0;
                    string? targetDomain = null;
                    if (targetMod.TryGetProperty("game", out var gameElem) && gameElem.ValueKind == JsonValueKind.Object)
                    {
                        if (gameElem.TryGetProperty("id", out var gIdElem))
                        {
                            var gIdStr = gIdElem.GetString() ?? gIdElem.GetRawText();
                            long.TryParse(gIdStr, out targetGameId);
                        }
                        if (gameElem.TryGetProperty("domain_name", out var dElem))
                        {
                            targetDomain = dElem.GetString();
                        }
                    }
                    else if (targetMod.TryGetProperty("game_id", out var directGameIdElem))
                    {
                        var gIdStr = directGameIdElem.GetString() ?? directGameIdElem.GetRawText();
                        long.TryParse(gIdStr, out targetGameId);
                    }

                    if (targetGameId <= 0)
                    {
                        return NexusModernRequirementResult.Failed(new(
                            NexusRequirementFailureKind.InvalidJson,
                            $"Dependency definition '{defId}' range target mod contains invalid or missing GameId."));
                    }

                    var targetIdentity = new NexusModIdentity(targetGameId, targetModId);
                    defTargetMods.Add(targetIdentity);

                    if (resolvedTarget == null)
                    {
                        var targetModName = targetMod.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : null;
                        var domainForUrl = !string.IsNullOrWhiteSpace(targetDomain)
                            ? targetDomain
                            : (TryGetGameDomain(targetGameId, out var knownDomain) ? knownDomain : null);

                        string? targetUrl = null;
                        Uri? targetUri = null;
                        if (!string.IsNullOrWhiteSpace(domainForUrl))
                        {
                            targetUrl = $"https://www.nexusmods.com/{domainForUrl}/mods/{targetModId}";
                            Uri.TryCreate(targetUrl, UriKind.Absolute, out targetUri);
                        }

                        resolvedTarget = new NexusModRequirementTarget(targetIdentity, targetModName, targetUrl, targetUri);
                    }
                }

                if (defTargetMods.Count == 0)
                {
                    return NexusModernRequirementResult.Failed(new(
                        NexusRequirementFailureKind.InvalidJson,
                        $"Dependency definition '{defId}' could not resolve any target mod identity."));
                }

                if (defTargetMods.Count > 1)
                {
                    return NexusModernRequirementResult.Failed(new(
                        NexusRequirementFailureKind.InvalidJson,
                        $"Dependency definition '{defId}' resolves to multiple distinct logical target mods ({defTargetMods.Count}), which is unsupported."));
                }

                if (seenLogicalMods.Add(resolvedTarget!.Identity))
                {
                    edges.Add(new NexusModRequirementEdge(
                        mod,
                        resolvedTarget,
                        SourceMetadata: null,
                        ProviderRequirementId: defId,
                        Notes: null,
                        ObservedThrough: NexusRequirementTraversal.ForwardRequirements));
                }
            }

            var snapshot = new NexusRequirementSnapshot(
                new NexusRequirementSnapshotOwner(mod, NexusRequirementTraversal.ForwardRequirements),
                edges);
            return NexusModernRequirementResult.Complete(snapshot, versionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NexusModernRequirementResult.Failed(new(NexusRequirementFailureKind.Cancelled, "Operation cancelled."));
        }
        catch (Exception ex)
        {
            return NexusModernRequirementResult.Failed(new(NexusRequirementFailureKind.Transport, ex.Message));
        }
    }

    private sealed record HttpDocResult(JsonDocument? Doc, NexusRequirementFailure? Failure);

    private async Task<HttpDocResult> GetV3DocAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var url = $"{_baseUrl}{relativePath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        NexusClientIdentity.ApplyHeaders(request);

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new(null, new NexusRequirementFailure(NexusRequirementFailureKind.Http, $"Nexus V3 returned HTTP {(int)response.StatusCode} ({response.StatusCode}).", response.StatusCode));
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new(doc, null);
        }
        catch (JsonException ex)
        {
            return new(null, new NexusRequirementFailure(NexusRequirementFailureKind.InvalidJson, $"Invalid JSON received from Nexus V3: {ex.Message}"));
        }
    }

    private static bool TryGetGameDomain(long gameId, out string domain)
    {
        if (gameId == 3333)
        {
            domain = "cyberpunk2077";
            return true;
        }

        domain = string.Empty;
        return false;
    }
}
