using System.Net;
using System.Security.Claims;
using System.Text;
using EmailValidation.Api;
using EmailValidation.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace EmailValidation.Api.Tests;

public sealed class ImpersonationContextTests
{
    [Fact]
    public async Task ValidSessionUsesEffectiveIdentityAndPreservesActorAttribution()
    {
        const string sessionId = "session-reference";
        var context = CreateContext("auth0|actor", sessionId);
        CurrentConsumer? consumer = null;
        var middleware = new ImpersonationContextMiddleware(_ =>
        {
            var accessor = new HttpContextAccessor { HttpContext = context };
            consumer = new HttpCurrentConsumerContext(accessor).GetRequiredConsumer();
            return Task.CompletedTask;
        });
        var client = CreateClient(HttpStatusCode.OK, """
            {
              "mode": "impersonating",
              "actor": {
                "userId": "auth0|actor",
                "email": "admin@example.com",
                "displayName": "Platform Admin",
                "permissions": ["users:impersonate"],
                "emailVerified": true
              },
              "effectiveUser": {
                "userId": "auth0|target",
                "email": "user@example.com",
                "displayName": "Company User",
                "permissions": ["search:execute", "match:execute"],
                "emailVerified": true
              },
              "sessionId": "session-reference",
              "startedAtUtc": "2026-10-03T12:00:00Z",
              "expiresAtUtc": "2026-10-03T12:30:00Z",
              "company": {
                "accountId": "company-1",
                "name": "AppendPros",
                "role": "CompanyUser",
                "status": "Active"
              }
            }
            """);

        await middleware.InvokeAsync(context, client);

        Assert.NotNull(consumer);
        Assert.Equal("auth0|target", consumer.SubjectId);
        Assert.Equal("auth0|actor", consumer.ActorSubjectId);
        Assert.Equal(sessionId, consumer.ImpersonationSessionId);
        Assert.Equal("company-1", consumer.TenantId);
        Assert.Contains("search:execute", consumer.Scopes);
        Assert.DoesNotContain("users:impersonate", consumer.Scopes);
    }

    [Fact]
    public async Task InvalidSessionFailsClosedWithoutCallingTheApplication()
    {
        var context = CreateContext("auth0|actor", "expired-session");
        var called = false;
        var middleware = new ImpersonationContextMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var client = CreateClient(HttpStatusCode.Forbidden, "{}");

        await middleware.InvokeAsync(context, client);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains(ImpersonationProtocol.InvalidSessionCode, body, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateContext(string actorUserId, string sessionId)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers.Authorization = "Bearer actor-token";
        context.Request.Headers[ImpersonationProtocol.SessionHeader] = sessionId;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", actorUserId)],
            "Bearer"));
        return context;
    }

    private static ImpersonationContextClient CreateClient(HttpStatusCode status, string content)
    {
        var handler = new StubHttpMessageHandler(status, content);
        return new ImpersonationContextClient(
            new HttpClient(handler),
            Options.Create(new ApiHostOptions
            {
                OpenMeta = new OpenMetaSourceOptions
                {
                    BaseUrl = "https://api.digitalwarehouse.io"
                }
            }));
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }
}
