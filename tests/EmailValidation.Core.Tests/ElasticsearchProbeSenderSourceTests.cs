using System.Text.Json;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ElasticsearchProbeSenderSourceTests
{
    [Fact]
    public async Task ConfiguredQueryIndexLimitAndSourceFilter_AreSent()
    {
        var client = new RecordingElasticsearchClient(Response(
            "john@business.test",
            " JOHN@business.test ",
            "malformed",
            "jane@business.test"));
        using var source = CreateSource(client);

        var candidates = await source.GetCandidatesAsync(25);

        Assert.Equal("authorized-senders", client.Index);
        using var request = JsonDocument.Parse(client.RequestJson!);
        var root = request.RootElement;
        Assert.Equal(25, root.GetProperty("size").GetInt32());
        Assert.Equal("business_email", root.GetProperty("_source")[0].GetString());
        Assert.Equal("business_email",
            root.GetProperty("query").GetProperty("exists").GetProperty("field").GetString());
        Assert.Equal(["john@business.test", "jane@business.test"], candidates.Select(item => item.Address));
        Assert.Equal(4, source.LastRetrievedCount);
        Assert.Equal(1, source.LastInvalidCount);
        Assert.Equal(1, source.LastDuplicateCount);
    }

    [Fact]
    public async Task QueryLimit_IsCappedAtFiveThousand()
    {
        var client = new RecordingElasticsearchClient(Response());
        using var source = CreateSource(client);

        await source.GetCandidatesAsync(50_000);

        using var request = JsonDocument.Parse(client.RequestJson!);
        Assert.Equal(5_000, request.RootElement.GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task FiveHundredHits_WithMalformedAndDuplicates_YieldsUniqueUsableCandidates()
    {
        var unique = Enumerable.Range(0, 475).Select(index => $"sender{index}@business.test").ToArray();
        var addresses = unique
            .Concat(unique.Take(15).Select(address => address.ToUpperInvariant()))
            .Concat(Enumerable.Repeat("not-an-email", 10))
            .ToArray();
        var client = new RecordingElasticsearchClient(Response(addresses));
        using var source = CreateSource(client);

        var candidates = await source.GetCandidatesAsync(500);

        Assert.Equal(475, candidates.Count);
        Assert.Equal(500, source.LastRetrievedCount);
        Assert.Equal(10, source.LastInvalidCount);
        Assert.Equal(15, source.LastDuplicateCount);
        Assert.Equal(475, candidates.Select(candidate => candidate.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static ElasticsearchProbeSenderSource CreateSource(RecordingElasticsearchClient client)
    {
        var options = Options.Create(new EmailValidationOptions
        {
            ProbeSenderSource = new ProbeSenderSourceOptions
            {
                Index = "authorized-senders",
                EmailField = "business_email",
                QueryJson = "{\"exists\":{\"field\":\"business_email\"}}"
            }
        });
        return new(client, new EmailNormalizer(), options,
            NullLogger<ElasticsearchProbeSenderSource>.Instance);
    }

    private static string Response(params string[] addresses) => JsonSerializer.Serialize(new
    {
        hits = new
        {
            hits = addresses.Select((address, index) => new
            {
                _source = new Dictionary<string, string> { ["business_email"] = address },
                sort = new[] { index }
            })
        }
    });

    private sealed class RecordingElasticsearchClient(string response) : IElasticsearchSearchClient
    {
        public string? Index { get; private set; }
        public string? RequestJson { get; private set; }

        public Task<string> SearchAsync(string index, string requestJson, CancellationToken cancellationToken)
        {
            Index = index;
            RequestJson = requestJson;
            return Task.FromResult(response);
        }
    }
}
