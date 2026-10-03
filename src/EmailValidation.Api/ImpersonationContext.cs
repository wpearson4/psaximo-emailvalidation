using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EmailValidation.Api;

public static class ImpersonationProtocol
{
    public const string SessionHeader = "X-OpenMeta-Impersonation-Session";
    public const string ActorUserIdClaim = "openmeta:actor_user_id";
    public const string SessionIdClaim = "openmeta:impersonation_session_id";
    public const string InvalidSessionCode = "IMPERSONATION_SESSION_INVALID";
}

public sealed class ImpersonationContextClient(
    HttpClient httpClient,
    IOptions<ApiHostOptions> options)
{
    public async Task<OpenMetaIdentityContext?> Resolve(
        string authorization,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var baseUrl = options.Value.OpenMeta.BaseUrl.TrimEnd('/') + "/";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(baseUrl), "api/identity-context"));
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation(ImpersonationProtocol.SessionHeader, sessionId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<OpenMetaIdentityContext>(
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ImpersonationContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ImpersonationContextClient client)
    {
        var sessionId = context.Request.Headers[ImpersonationProtocol.SessionHeader]
            .FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        if (sessionId.Length > 128)
        {
            await Reject(context).ConfigureAwait(false);
            return;
        }

        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        var actorUserId = context.User.FindFirstValue("sub")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(authorization)
            || string.IsNullOrWhiteSpace(actorUserId)
            || ApiSecurityExtensions.IsMachineClient(context.User))
        {
            await Reject(context).ConfigureAwait(false);
            return;
        }

        OpenMetaIdentityContext? resolved;
        try
        {
            resolved = await client.Resolve(
                authorization,
                sessionId,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            resolved = null;
        }
        catch (JsonException)
        {
            resolved = null;
        }
        catch (NotSupportedException)
        {
            resolved = null;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            resolved = null;
        }

        if (resolved is null
            || !string.Equals(resolved.Mode, "impersonating", StringComparison.Ordinal)
            || !string.Equals(resolved.SessionId, sessionId, StringComparison.Ordinal)
            || resolved.Actor is null
            || resolved.EffectiveUser is null
            || !string.Equals(resolved.Actor.UserId, actorUserId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(resolved.EffectiveUser.UserId))
        {
            await Reject(context).ConfigureAwait(false);
            return;
        }

        var claims = new List<Claim>
        {
            new("sub", resolved.EffectiveUser.UserId),
            new(ClaimTypes.NameIdentifier, resolved.EffectiveUser.UserId),
            new("email", resolved.EffectiveUser.Email ?? string.Empty),
            new("name", resolved.EffectiveUser.DisplayName ?? resolved.EffectiveUser.Email ?? string.Empty),
            new("email_verified", resolved.EffectiveUser.EmailVerified ? "true" : "false"),
            new(ImpersonationProtocol.ActorUserIdClaim, actorUserId),
            new(ImpersonationProtocol.SessionIdClaim, sessionId)
        };
        claims.AddRange(resolved.EffectiveUser.Permissions.Select(permission =>
            new Claim("permissions", permission)));
        if (!string.IsNullOrWhiteSpace(resolved.Company?.AccountId))
            claims.Add(new Claim("tenant_id", resolved.Company.AccountId));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            "OpenMetaImpersonation",
            "name",
            ClaimTypes.Role));
        await next(context).ConfigureAwait(false);
    }

    private static async Task Reject(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Type = "https://api.digitalwarehouse.io/problems/impersonation-session-invalid",
            Title = "Impersonation session unavailable",
            Detail = "The impersonation session is invalid, expired, or no longer authorized.",
            Status = StatusCodes.Status403Forbidden,
            Instance = context.Request.Path
        };
        problem.Extensions["code"] = ImpersonationProtocol.InvalidSessionCode;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
    }
}

public sealed record OpenMetaIdentityContext(
    string Mode,
    OpenMetaIdentitySummary Actor,
    OpenMetaIdentitySummary EffectiveUser,
    string? SessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    OpenMetaCompanyContext? Company);

public sealed record OpenMetaIdentitySummary(
    string UserId,
    string? Email,
    string? DisplayName,
    IReadOnlyCollection<string> Permissions,
    bool EmailVerified);

public sealed record OpenMetaCompanyContext(
    string AccountId,
    string? Name,
    string? Role,
    string Status);
