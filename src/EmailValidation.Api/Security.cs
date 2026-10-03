using System.Security.Claims;
using System.Text.Json;
using EmailValidation.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace EmailValidation.Api;

public static class ApiSecurityExtensions
{
    public static IServiceCollection AddEmailValidationSecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var authentication = configuration.GetSection("Authentication").Get<ApiAuthenticationOptions>() ?? new();
        if (!environment.IsEnvironment("Testing"))
        {
            if (!Uri.TryCreate(authentication.Authority, UriKind.Absolute, out var authority))
                throw new InvalidOperationException("Authentication:Authority must be an absolute OIDC authority URI.");
            if (authentication.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Authentication:Authority must use HTTPS when RequireHttpsMetadata is enabled.");
            if (string.IsNullOrWhiteSpace(authentication.Audience))
                throw new InvalidOperationException("Authentication:Audience is required.");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var validAudiences = new[] { authentication.Audience }
                    .Concat(authentication.AdditionalAudiences ?? [])
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                options.Authority = authentication.Authority;
                options.Audience = authentication.Audience;
                options.RequireHttpsMetadata = authentication.RequireHttpsMetadata;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidAudiences = validAudiences,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = "sub"
                };
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddScopePolicy(EmailValidationPolicies.Validate, EmailValidationScopes.Validate)
            .AddPolicy(EmailValidationPolicies.ArbitraryValidation, options => options
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    HasScope(context.User, EmailValidationScopes.Admin) ||
                    !IsMachineClient(context.User)))
            .AddScopePolicy(EmailValidationPolicies.Read, EmailValidationScopes.Read)
            .AddScopePolicy(
                EmailValidationPolicies.JobsWrite,
                EmailValidationScopes.JobsWrite,
                "search:execute",
                "match:execute",
                "openmeta.write")
            .AddPolicy(EmailValidationPolicies.PurchasedJobsWrite, options => options
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsMachineClient(context.User) &&
                    (HasScope(context.User, EmailValidationScopes.JobsWrite) ||
                        HasScope(context.User, EmailValidationScopes.Admin))))
            .AddScopePolicy(
                EmailValidationPolicies.JobsRead,
                EmailValidationScopes.JobsRead,
                "search:read",
                "match:read",
                "openmeta.read")
            .AddScopePolicy(EmailValidationPolicies.Stream, EmailValidationScopes.Stream)
            .AddScopePolicy(EmailValidationPolicies.Admin, EmailValidationScopes.Admin);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentConsumerContext, HttpCurrentConsumerContext>();
        return services;
    }

    private static AuthorizationBuilder AddScopePolicy(
        this AuthorizationBuilder builder,
        string policy,
        string requiredScope,
        params string[] compatiblePermissions) =>
        builder.AddPolicy(policy, options => options
            .RequireAuthenticatedUser()
            .RequireAssertion(context => HasScope(context.User, requiredScope) ||
                compatiblePermissions.Any(permission => HasScope(context.User, permission)) ||
                HasScope(context.User, EmailValidationScopes.Admin)));

    public static bool HasScope(ClaimsPrincipal principal, string scope) =>
        GetAuthorizationValues(principal)
            .Contains(scope, StringComparer.Ordinal);

    public static IReadOnlySet<string> GetScopes(ClaimsPrincipal principal) =>
        GetAuthorizationValues(principal)
            .ToHashSet(StringComparer.Ordinal);

    public static bool IsMachineClient(ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirstValue("gty"), "client-credentials", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(principal.FindFirstValue("grant_type"), "client_credentials", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(principal.FindFirstValue("client_id")) ||
        principal.FindFirstValue("sub")?.EndsWith("@clients", StringComparison.Ordinal) == true;

    private static IEnumerable<string> GetAuthorizationValues(ClaimsPrincipal principal)
    {
        foreach (var claim in principal.FindAll("scope").Concat(principal.FindAll("scp")))
        {
            foreach (var value in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                yield return value;
        }

        foreach (var claim in principal.FindAll("permissions"))
        {
            if (!claim.Value.TrimStart().StartsWith('['))
            {
                yield return claim.Value;
                continue;
            }

            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(claim.Value);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                        yield return value;
                }
            }
            finally
            {
                document?.Dispose();
            }
        }
    }
}

public sealed class HttpCurrentConsumerContext(IHttpContextAccessor accessor) : ICurrentConsumerContext
{
    public CurrentConsumer GetRequiredConsumer()
    {
        var principal = accessor.HttpContext?.User;
        if (principal is null)
            throw new InvalidOperationException("There is no active authenticated consumer.");
        var subject = principal.FindFirstValue("sub") ??
            principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
            throw new InvalidOperationException("The authenticated token does not contain a subject claim.");
        var tenant = principal.FindFirstValue("tenant_id") ?? principal.FindFirstValue("tid");
        return new CurrentConsumer(
            subject,
            tenant,
            ApiSecurityExtensions.GetScopes(principal),
            principal.FindFirstValue(ImpersonationProtocol.ActorUserIdClaim),
            principal.FindFirstValue(ImpersonationProtocol.SessionIdClaim));
    }
}
