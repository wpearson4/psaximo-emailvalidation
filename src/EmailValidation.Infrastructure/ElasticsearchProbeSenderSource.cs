using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public interface IElasticsearchSearchClient
{
    Task<string> SearchAsync(string index, string requestJson, CancellationToken cancellationToken);
}

public interface IProbeSenderSourceDiagnostics
{
    int LastRetrievedCount { get; }
    int LastInvalidCount { get; }
    int LastDuplicateCount { get; }
    TimeSpan LastQueryDuration { get; }
}

public sealed class ElasticsearchSearchClient(ElasticsearchClient client) : IElasticsearchSearchClient
{
    public async Task<string> SearchAsync(string index, string requestJson, CancellationToken cancellationToken)
    {
        var path = $"/{Uri.EscapeDataString(index)}/_search";
        var response = await client.Transport.RequestAsync<StringResponse>(
            Elastic.Transport.HttpMethod.POST,
            path,
            PostData.String(requestJson),
            cancellationToken);
        if (response.ApiCallDetails?.HasSuccessfulStatusCode != true)
            throw new InvalidOperationException(
                $"Elasticsearch sender query failed with HTTP status {response.ApiCallDetails?.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");
        return response.Body;
    }
}

public sealed class ElasticsearchProbeSenderSource : IProbeSenderSource, IProbeSenderSourceDiagnostics, IDisposable
{
    private readonly IElasticsearchSearchClient _client;
    private readonly IEmailNormalizer _normalizer;
    private readonly ProbeSenderSourceOptions _options;
    private readonly ILogger<ElasticsearchProbeSenderSource> _logger;
    private readonly SemaphoreSlim _cursorGate = new(1, 1);
    private JsonArray? _searchAfter;

    public int LastRetrievedCount { get; private set; }
    public int LastInvalidCount { get; private set; }
    public int LastDuplicateCount { get; private set; }
    public TimeSpan LastQueryDuration { get; private set; }

    public ElasticsearchProbeSenderSource(
        IElasticsearchSearchClient client,
        IEmailNormalizer normalizer,
        IOptions<EmailValidationOptions> options,
        ILogger<ElasticsearchProbeSenderSource> logger)
    {
        _client = client;
        _normalizer = normalizer;
        _options = options.Value.ProbeSenderSource;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<ProbeSenderCandidate>> GetCandidatesAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 10, 5_000);
        await _cursorGate.WaitAsync(cancellationToken);
        try
        {
            var watch = Stopwatch.StartNew();
            var candidates = await FetchAsync(boundedLimit, cancellationToken);
            if (candidates.Count == 0 && _searchAfter is not null)
            {
                _searchAfter = null;
                candidates = await FetchAsync(boundedLimit, cancellationToken);
            }
            watch.Stop();
            LastQueryDuration = watch.Elapsed;
            _logger.LogInformation(
                "Elasticsearch probe sender query completed in {QueryDurationMs} ms and returned {CandidateCount} usable candidates",
                watch.ElapsedMilliseconds,
                candidates.Count);
            return candidates;
        }
        finally
        {
            _cursorGate.Release();
        }
    }

    private async Task<IReadOnlyCollection<ProbeSenderCandidate>> FetchAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var query = JsonNode.Parse(_options.QueryJson)
            ?? throw new InvalidOperationException("The configured Elasticsearch query is empty.");
        var request = new JsonObject
        {
            ["size"] = limit,
            ["track_total_hits"] = false,
            ["_source"] = new JsonArray(_options.EmailField),
            ["query"] = query,
            ["sort"] = new JsonArray(new JsonObject { ["_shard_doc"] = "asc" })
        };
        if (_searchAfter is not null)
            request["search_after"] = _searchAfter.DeepClone();

        var responseJson = await _client.SearchAsync(
            _options.Index,
            request.ToJsonString(),
            cancellationToken);
        using var response = JsonDocument.Parse(responseJson);
        if (!response.RootElement.TryGetProperty("hits", out var hitsContainer) ||
            !hitsContainer.TryGetProperty("hits", out var hits) ||
            hits.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Elasticsearch returned an invalid search response without hits.hits.");

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ProbeSenderCandidate>();
        var invalid = 0;
        var duplicates = 0;
        JsonArray? lastSort = null;
        LastRetrievedCount = hits.GetArrayLength();
        foreach (var hit in hits.EnumerateArray())
        {
            if (hit.TryGetProperty("sort", out var sort) && sort.ValueKind == JsonValueKind.Array)
                lastSort = JsonNode.Parse(sort.GetRawText())?.AsArray();
            if (!hit.TryGetProperty("_source", out var source)) continue;
            foreach (var value in ReadFieldValues(source, _options.EmailField))
            {
                var normalized = _normalizer.Normalize(value.Trim());
                if (!normalized.IsValid || normalized.NormalizedEmail is null)
                {
                    invalid++;
                    continue;
                }
                if (!unique.Add(normalized.NormalizedEmail))
                {
                    duplicates++;
                    continue;
                }
                candidates.Add(new ProbeSenderCandidate(normalized.NormalizedEmail, DateTimeOffset.UtcNow));
            }
        }
        LastInvalidCount = invalid;
        LastDuplicateCount = duplicates;
        _searchAfter = lastSort;
        return candidates;
    }

    private static IEnumerable<string> ReadFieldValues(JsonElement source, string field)
    {
        var current = source;
        foreach (var segment in field.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                yield break;
        }

        if (current.ValueKind == JsonValueKind.String)
        {
            var value = current.GetString();
            if (value is not null) yield return value;
        }
        else if (current.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in current.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
                    yield return value;
        }
    }

    public void Dispose() => _cursorGate.Dispose();
}
