using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RipperWorks.Downloader;

public interface INexusModContentMetadataClient
{
    Task<IReadOnlyDictionary<NexusModIdentity, NexusAdultContentClassification>>
        GetAdultContentClassificationsAsync(
            IReadOnlyCollection<NexusModIdentity> identities,
            CancellationToken cancellationToken = default);
}

public sealed record NexusModContentMetadataClientOptions
{
    public Uri Endpoint { get; init; } =
        NexusGraphQlRequirementClientOptions.DefaultEndpoint;
    public int BatchSize { get; init; } = 40;
}

public sealed class NexusModContentMetadataClient : INexusModContentMetadataClient
{
    private readonly HttpClient _client;
    private readonly NexusModContentMetadataClientOptions _options;

    public NexusModContentMetadataClient(
        HttpClient client,
        NexusModContentMetadataClientOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new NexusModContentMetadataClientOptions();
        if (!_options.Endpoint.IsAbsoluteUri || _options.BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Nexus content metadata endpoint and batch size must be valid.");
        }
    }

    public async Task<IReadOnlyDictionary<NexusModIdentity, NexusAdultContentClassification>>
        GetAdultContentClassificationsAsync(
            IReadOnlyCollection<NexusModIdentity> identities,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identities);
        cancellationToken.ThrowIfCancellationRequested();

        var distinct = identities
            .Where(identity => identity.GameId > 0 && identity.ModId > 0)
            .Distinct()
            .ToArray();
        var result = distinct.ToDictionary(
            identity => identity,
            _ => NexusAdultContentClassification.Unknown);

        foreach (var batch in distinct.Chunk(_options.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classifications = await QueryBatchAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in classifications)
                result[item.Key] = item.Value;
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<NexusModIdentity, NexusAdultContentClassification>>
        QueryBatchAsync(
            IReadOnlyList<NexusModIdentity> identities,
            CancellationToken cancellationToken)
    {
        var result = identities.ToDictionary(
            identity => identity,
            _ => NexusAdultContentClassification.Unknown);
        try
        {
            using var request = CreateRequest(identities);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return result;

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            for (var index = 0; index < identities.Count; index++)
            {
                if (!data.TryGetProperty($"mod{index}", out var mod) ||
                    mod.ValueKind != JsonValueKind.Object ||
                    !mod.TryGetProperty("adultContent", out var adult))
                {
                    continue;
                }

                result[identities[index]] = adult.ValueKind switch
                {
                    JsonValueKind.True => NexusAdultContentClassification.Adult,
                    JsonValueKind.False => NexusAdultContentClassification.NonAdult,
                    _ => NexusAdultContentClassification.Unknown
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // Unknown remains fail-closed downstream.
        }
        catch (IOException)
        {
            // Unknown remains fail-closed downstream.
        }
        catch (JsonException)
        {
            // Unknown remains fail-closed downstream.
        }

        return result;
    }

    private HttpRequestMessage CreateRequest(
        IReadOnlyList<NexusModIdentity> identities)
    {
        var query = new StringBuilder("query ModAdultContent(");
        var variables = new Dictionary<string, long>();
        for (var index = 0; index < identities.Count; index++)
        {
            if (index > 0)
                query.Append(", ");
            query.Append("$game").Append(index).Append(": ID!, $mod")
                .Append(index).Append(": ID!");
            variables[$"game{index}"] = identities[index].GameId;
            variables[$"mod{index}"] = identities[index].ModId;
        }
        query.Append(") {");
        for (var index = 0; index < identities.Count; index++)
        {
            query.Append(" mod").Append(index)
                .Append(": mod(gameId: $game").Append(index)
                .Append(", modId: $mod").Append(index)
                .Append(") { adultContent }");
        }
        query.Append(" }");

        var body = JsonSerializer.Serialize(new
        {
            query = query.ToString(),
            variables
        });
        var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        NexusClientIdentity.ApplyHeaders(request);
        return request;
    }
}
