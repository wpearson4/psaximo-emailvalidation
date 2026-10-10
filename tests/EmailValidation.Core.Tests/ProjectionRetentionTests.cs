using System.Net;
using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ProjectionRetentionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectionRetention_UsesBenchmarkClock_AndBoundedApply(bool dryRun)
    {
        var cutoff = new DateTimeOffset(2025, 10, 10, 12, 0, 0, TimeSpan.Zero);
        using var handler = new RetentionHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith(dryRun ? "/_count" : "/_delete_by_query?conflicts=proceed", request.RequestUri!.ToString(), StringComparison.Ordinal);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(cutoff, body.RootElement.GetProperty("query").GetProperty("range")
                .GetProperty("@timestamp").GetProperty("lt").GetDateTimeOffset());
            Assert.Equal(!dryRun, body.RootElement.TryGetProperty("max_docs", out var limit));
            if (!dryRun) Assert.Equal(17, limit.GetInt32());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(dryRun ?
                "{\"count\":3}" : "{\"deleted\":3,\"failures\":[]}") };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://projection.example.test/") };
        var sink = new ElasticsearchObservationSink(http, Options.Create(new EmailValidationOptions()));
        Assert.Equal(3, await sink.PruneAsync(new(cutoff.AddDays(275), cutoff, 17, dryRun)));
    }

    [Fact]
    public async Task ProjectionRetention_DoesNotReportSuccessWhenDeletionFails()
    {
        using var handler = new RetentionHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"deleted\":0,\"failures\":[{\"cause\":\"unavailable\"}]}") }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://projection.example.test/") };
        var sink = new ElasticsearchObservationSink(http, Options.Create(new EmailValidationOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.PruneAsync(new(
            DateTimeOffset.UtcNow.AddDays(-90), DateTimeOffset.UtcNow.AddDays(-365), DryRun: false)));
    }

    private sealed class RetentionHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }
}
