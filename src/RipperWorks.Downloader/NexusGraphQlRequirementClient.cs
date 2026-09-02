using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public interface INexusGraphQlRequirementClient
{
    Task<NexusRequirementQueryResult> GetRequirementsAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken = default);

    Task<NexusRequirementQueryResult> GetModsRequiringAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken = default);
}

public sealed class NexusGraphQlRequirementClient
    : INexusGraphQlRequirementClient
{
    private const string ForwardQuery = """
        query ModRequirements(
            $gameId: ID!, $modId: ID!, $offset: Int!, $count: Int!) {
          mod(gameId: $gameId, modId: $modId) {
            legacyModRequirementsEnabled
            modRequirements {
              nexusRequirements(offset: $offset, count: $count) {
                nodes {
                  id
                  gameId
                  modId
                  modName
                  notes
                  url
                  externalRequirement
                }
                nodesCount
                totalCount
              }
            }
          }
        }
        """;

    private const string ReverseQuery = """
        query ModsRequiringThisMod(
            $gameId: ID!, $modId: ID!, $offset: Int!, $count: Int!) {
          mod(gameId: $gameId, modId: $modId) {
            modRequirements {
              modsRequiringThisMod(offset: $offset, count: $count) {
                nodes {
                  id
                  gameId
                  modId
                  modName
                  notes
                  url
                  externalRequirement
                }
                nodesCount
                totalCount
              }
            }
          }
        }
        """;

    private readonly HttpClient _client;
    private readonly NexusGraphQlRequirementClientOptions _options;

    public NexusGraphQlRequirementClient(
        HttpClient client,
        NexusGraphQlRequirementClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options ?? new NexusGraphQlRequirementClientOptions();
        ValidateOptions(_options);
    }

    public Task<NexusRequirementQueryResult> GetRequirementsAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            mod,
            NexusRequirementTraversal.ForwardRequirements,
            ForwardQuery,
            "nexusRequirements",
            cancellationToken);

    public Task<NexusRequirementQueryResult> GetModsRequiringAsync(
        NexusModIdentity mod,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            mod,
            NexusRequirementTraversal.ReverseRequiredBy,
            ReverseQuery,
            "modsRequiringThisMod",
            cancellationToken);

    private async Task<NexusRequirementQueryResult> QueryAsync(
        NexusModIdentity queriedMod,
        NexusRequirementTraversal traversal,
        string query,
        string collectionName,
        CancellationToken cancellationToken)
    {
        if (!IsValidIdentity(queriedMod))
        {
            return Failure(
                NexusRequirementFailureKind.MalformedIdentity,
                "The queried Nexus mod identity must contain positive IDs.");
        }

        var edges = new List<NexusModRequirementEdge>();
        var pageSignatures = new HashSet<string>(StringComparer.Ordinal);
        var observationKeys = new HashSet<string>(StringComparer.Ordinal);
        int? expectedTotal = null;
        var offset = 0;

        try
        {
            for (var page = 0; page < _options.MaximumPageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var response = await SendPageAsync(
                    queriedMod,
                    query,
                    offset,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return NexusRequirementQueryResult.Failed(new(
                        NexusRequirementFailureKind.Http,
                        $"Nexus GraphQL returned HTTP {(int)response.StatusCode}.",
                        response.StatusCode));
                }

                JsonDocument document;
                try
                {
                    await using var stream = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    document = await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (JsonException exception)
                {
                    return Failure(
                        NexusRequirementFailureKind.InvalidJson,
                        $"Nexus GraphQL returned invalid JSON: {exception.Message}");
                }

                using (document)
                {
                    var hasErrors = HasGraphQlErrors(document.RootElement);
                    if (!TryGetCollection(
                            document.RootElement,
                            collectionName,
                            out var collection))
                    {
                        return Failure(
                            hasErrors
                                ? NexusRequirementFailureKind.GraphQl
                                : NexusRequirementFailureKind.InvalidJson,
                            hasErrors
                                ? "Nexus GraphQL returned errors without usable data."
                                : "Nexus GraphQL response omitted requirement data.");
                    }
                    if (hasErrors)
                    {
                        return Failure(
                            NexusRequirementFailureKind.PartialGraphQl,
                            "Nexus GraphQL returned partial data with errors.");
                    }

                    if (traversal == NexusRequirementTraversal.ForwardRequirements)
                    {
                        bool? legacyEnabled = null;
                        if (document.RootElement.TryGetProperty("data", out var d) &&
                            d.ValueKind == JsonValueKind.Object &&
                            d.TryGetProperty("mod", out var m) &&
                            m.ValueKind == JsonValueKind.Object &&
                            m.TryGetProperty("legacyModRequirementsEnabled", out var lElem))
                        {
                            if (lElem.ValueKind == JsonValueKind.True) legacyEnabled = true;
                            else if (lElem.ValueKind == JsonValueKind.False) legacyEnabled = false;
                        }

                        if (legacyEnabled == null)
                        {
                            return Failure(
                                NexusRequirementFailureKind.InvalidJson,
                                "Nexus GraphQL response omitted or contained a non-boolean legacyModRequirementsEnabled selector.");
                        }

                        if (legacyEnabled == false)
                        {
                            return NexusRequirementQueryResult.Modern(legacyModRequirementsEnabled: false);
                        }
                    }

                    var pageResult = ParsePage(
                        collection,
                        queriedMod,
                        traversal);
                    if (pageResult.Failure is not null)
                        return NexusRequirementQueryResult.Failed(pageResult.Failure);

                    if (pageResult.TotalCount > _options.MaximumNodeCount)
                    {
                        return Failure(
                            NexusRequirementFailureKind.Pagination,
                            "Nexus requirement count exceeds the configured limit.");
                    }
                    if (expectedTotal is { } knownTotal &&
                        knownTotal != pageResult.TotalCount)
                    {
                        return Failure(
                            NexusRequirementFailureKind.Pagination,
                            "Nexus requirement total changed during pagination.");
                    }
                    expectedTotal ??= pageResult.TotalCount;

                    if (!pageSignatures.Add(pageResult.Signature) &&
                        pageResult.Edges.Count > 0)
                    {
                        return Failure(
                            NexusRequirementFailureKind.Pagination,
                            "Nexus repeated a requirement page.");
                    }
                    if (pageResult.ObservationKeys.Any(
                            key => !observationKeys.Add(key)))
                    {
                        return Failure(
                            NexusRequirementFailureKind.Pagination,
                            "Nexus repeated a requirement across pages.");
                    }
                    if (edges.Count + pageResult.Edges.Count >
                        pageResult.TotalCount)
                    {
                        return Failure(
                            NexusRequirementFailureKind.Pagination,
                            "Nexus returned more requirements than totalCount.");
                    }

                    edges.AddRange(pageResult.Edges);
                    if (edges.Count == pageResult.TotalCount)
                    {
                        return NexusRequirementQueryResult.Complete(
                            new NexusRequirementSnapshot(new NexusRequirementSnapshotOwner(queriedMod, traversal), edges.ToArray()),
                            legacyModRequirementsEnabled: true);
                    }
                    if (pageResult.Edges.Count == 0)
                    {
                        return Failure(
                            NexusRequirementFailureKind.Pagination,
                            "Nexus pagination made no positive progress.");
                    }
                    offset = checked(offset + pageResult.Edges.Count);
                }
            }

            return Failure(
                NexusRequirementFailureKind.Pagination,
                "Nexus requirement pagination exceeded the page limit.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                NexusRequirementFailureKind.Cancelled,
                "Nexus requirement query was cancelled.");
        }
        catch (OperationCanceledException exception)
        {
            return Failure(
                NexusRequirementFailureKind.Transport,
                $"Nexus GraphQL transport timed out: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return Failure(
                NexusRequirementFailureKind.Transport,
                $"Nexus GraphQL transport failed: {exception.Message}");
        }
        catch (IOException exception)
        {
            return Failure(
                NexusRequirementFailureKind.Transport,
                $"Nexus GraphQL response failed: {exception.Message}");
        }
        catch (OverflowException)
        {
            return Failure(
                NexusRequirementFailureKind.Pagination,
                "Nexus requirement pagination offset overflowed.");
        }
    }

    private async Task<HttpResponseMessage> SendPageAsync(
        NexusModIdentity mod,
        string query,
        int offset,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            query,
            variables = new
            {
                gameId = mod.GameId,
                modId = mod.ModId,
                offset,
                count = _options.PageSize
            }
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            _options.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        NexusClientIdentity.ApplyHeaders(request);
        return await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static PageResult ParsePage(
        JsonElement collection,
        NexusModIdentity queriedMod,
        NexusRequirementTraversal traversal)
    {
        if (!TryGetNonNegativeInt(collection, "nodesCount", out var nodesCount) ||
            !TryGetNonNegativeInt(collection, "totalCount", out var totalCount) ||
            !collection.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return PageResult.Fail(
                "Nexus returned incoherent pagination metadata.");
        }
        var nodeArray = nodes.EnumerateArray().ToArray();
        if (nodeArray.Length != nodesCount)
            return PageResult.Fail("Nexus nodesCount did not match nodes.");

        var edges = new List<NexusModRequirementEdge>(nodeArray.Length);
        foreach (var node in nodeArray)
        {
            var edge = ParseEdge(node, queriedMod, traversal);
            if (edge is null)
            {
                return PageResult.Fail(
                    "Nexus returned a requirement with malformed identity.",
                    NexusRequirementFailureKind.MalformedIdentity);
            }
            edges.Add(edge);
        }
        var observationKeys = edges
            .Select(GetPaginationObservationKey)
            .ToArray();
        return new(
            edges,
            totalCount,
            string.Join(
                "\n",
                observationKeys.OrderBy(
                    key => key,
                    StringComparer.Ordinal)),
            observationKeys,
            null);
    }

    private static string GetPaginationObservationKey(
        NexusModRequirementEdge edge)
    {
        if (edge.CanonicalKey is { } key &&
            key.NexusTargetIdentity is { } target)
        {
            return $"N|{key.Source.GameId}|{key.Source.ModId}|" +
                $"{target.GameId}|{target.ModId}";
        }

        var external = (NexusExternalRequirementTarget)edge.Target;
        if (!string.IsNullOrWhiteSpace(edge.ProviderRequirementId))
        {
            return $"E|row|{edge.ProviderRequirementId.Trim()}";
        }
        var fallback = string.Join(
            "\u001F",
            edge.Source.GameId,
            edge.Source.ModId,
            external.DisplayName?.Trim() ?? string.Empty,
            external.ProviderUrl?.Trim() ?? string.Empty);
        return $"E|observation|{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fallback)))}";
    }

    private static NexusModRequirementEdge? ParseEdge(
        JsonElement node,
        NexusModIdentity queriedMod,
        NexusRequirementTraversal traversal)
    {
        var external = GetBoolean(node, "externalRequirement");
        if (external is null)
            return null;
        var displayName = GetString(node, "modName");
        var rawUrl = GetString(node, "url");
        var clickableUrl = GetSafeUrl(rawUrl);
        NexusRequirementTarget target;
        NexusModIdentity source;

        NexusModRequirementEndpointMetadata? sourceMetadata = null;
        if (external.Value)
        {
            if (traversal != NexusRequirementTraversal.ForwardRequirements)
                return null;
            source = queriedMod;
            target = new NexusExternalRequirementTarget(
                displayName,
                rawUrl,
                clickableUrl);
        }
        else
        {
            var observed = new NexusModIdentity(
                GetInt64(node, "gameId") ?? 0,
                GetInt64(node, "modId") ?? 0);
            if (!IsValidIdentity(observed))
                return null;
            if (traversal == NexusRequirementTraversal.ForwardRequirements)
            {
                source = queriedMod;
                target = new NexusModRequirementTarget(
                    observed,
                    displayName,
                    rawUrl,
                    clickableUrl);
            }
            else
            {
                source = observed;
                sourceMetadata = new(
                    displayName,
                    rawUrl,
                    clickableUrl);
                target = new NexusModRequirementTarget(
                    queriedMod,
                    null,
                    null,
                    null);
            }
        }

        return new(
            source,
            target,
            sourceMetadata,
            GetOpaqueId(node, "id"),
            GetString(node, "notes"),
            traversal);
    }

    private static bool TryGetCollection(
        JsonElement root,
        string collectionName,
        out JsonElement collection)
    {
        collection = default;
        return root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("mod", out var mod) &&
            mod.ValueKind == JsonValueKind.Object &&
            mod.TryGetProperty("modRequirements", out var requirements) &&
            requirements.ValueKind == JsonValueKind.Object &&
            requirements.TryGetProperty(collectionName, out collection) &&
            collection.ValueKind == JsonValueKind.Object;
    }

    private static bool HasGraphQlErrors(JsonElement root) =>
        root.TryGetProperty("errors", out var errors) &&
        errors.ValueKind == JsonValueKind.Array &&
        errors.GetArrayLength() > 0;

    private static bool TryGetNonNegativeInt(
        JsonElement value,
        string name,
        out int result)
    {
        result = 0;
        return value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out result) &&
            result >= 0;
    }

    private static long? GetInt64(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetInt64(out var number) =>
                    number,
                JsonValueKind.String when long.TryParse(
                    property.GetString(),
                    out var number) => number,
                _ => null
            }
            : null;

    private static string? GetString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetOpaqueId(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                _ => null
            }
            : null;

    private static bool? GetBoolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static Uri? GetSafeUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https"
            ? uri
            : null;

    private static bool IsValidIdentity(NexusModIdentity identity) =>
        identity.GameId > 0 && identity.ModId > 0;

    private static NexusRequirementQueryResult Failure(
        NexusRequirementFailureKind kind,
        string message) =>
        NexusRequirementQueryResult.Failed(new(kind, message));

    private static void ValidateOptions(
        NexusGraphQlRequirementClientOptions options)
    {
        if (!options.Endpoint.IsAbsoluteUri ||
            options.PageSize <= 0 ||
            options.MaximumPageCount <= 0 ||
            options.MaximumNodeCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Nexus GraphQL limits and endpoint must be valid.");
        }
    }

    private sealed record PageResult(
        IReadOnlyList<NexusModRequirementEdge> Edges,
        int TotalCount,
        string Signature,
        IReadOnlyList<string> ObservationKeys,
        NexusRequirementFailure? Failure)
    {
        public static PageResult Fail(
            string message,
            NexusRequirementFailureKind kind =
                NexusRequirementFailureKind.Pagination) =>
            new([], 0, string.Empty, [], new(kind, message));
    }
}
