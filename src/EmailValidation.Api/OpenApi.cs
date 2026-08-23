using EmailValidation.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EmailValidation.Api;

public static class ApiOpenApiExtensions
{
    public static IServiceCollection AddEmailValidationOpenApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var api = configuration.GetSection("Api").Get<ApiHostOptions>() ?? new();
        var authentication = configuration.GetSection("Authentication").Get<ApiAuthenticationOptions>() ?? new();
        var tokenUrl = ResolveUrl(api.OpenApi.TokenUrl, authentication.Authority, "connect/token");
        var authorizationUrl = ResolveUrl(api.OpenApi.AuthorizationUrl, authentication.Authority, "authorize");
        var scopes = new Dictionary<string, string>
        {
            [EmailValidationScopes.Validate] = "Validate one email address.",
            [EmailValidationScopes.Read] = "Read a validation resource.",
            [EmailValidationScopes.JobsWrite] = "Create durable validation jobs.",
            [EmailValidationScopes.JobsRead] = "Read validation jobs and results.",
            [EmailValidationScopes.Stream] = "Subscribe to validation lifecycle streams.",
            [EmailValidationScopes.Admin] = "Administrative access; not intended for ordinary consumers."
        };

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Email Validation API",
                Version = "v1",
                Description = "Secure commercial REST boundary for canonical email validation and durable jobs."
            });
            options.SupportNonNullableReferenceTypes();
            options.CustomSchemaIds(type => type.FullName?.Replace('+', '.') ?? type.Name);
            options.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "OAuth 2.0 bearer access token. Commercial integrations normally use client credentials.",
                Flows = new OpenApiOAuthFlows
                {
                    ClientCredentials = new OpenApiOAuthFlow { TokenUrl = tokenUrl, Scopes = scopes },
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        AuthorizationUrl = authorizationUrl,
                        TokenUrl = tokenUrl,
                        Scopes = scopes
                    }
                }
            });
            options.DocumentFilter<ScopeSecurityDocumentFilter>();
        });
        return services;
    }

    public static WebApplication MapEmailValidationOpenApi(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ApiHostOptions>>().Value.OpenApi;
        var expose = app.Environment.IsDevelopment() || options.ExposeInProduction;
        if (!expose) return app;

        if (!app.Environment.IsDevelopment())
        {
            app.UseWhen(context => context.Request.Path.StartsWithSegments("/swagger"), branch =>
            {
                branch.UseAuthentication();
                branch.UseAuthorization();
                branch.Use(async (context, next) =>
                {
                    var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
                    var result = await authorization.AuthorizeAsync(
                        context.User, null, EmailValidationPolicies.Admin).ConfigureAwait(false);
                    if (!result.Succeeded)
                    {
                        if (context.User.Identity?.IsAuthenticated == true)
                            await context.ForbidAsync().ConfigureAwait(false);
                        else
                            await context.ChallengeAsync().ConfigureAwait(false);
                        return;
                    }
                    await next(context).ConfigureAwait(false);
                });
            });
        }

        app.UseSwagger(options => options.RouteTemplate = "swagger/{documentName}/swagger.json");
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = "swagger";
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Email Validation API v1");
            options.DocumentTitle = "Email Validation API v1";
            options.OAuthUsePkce();
            if (!string.IsNullOrWhiteSpace(app.Configuration["Api:OpenApi:SwaggerClientId"]))
                options.OAuthClientId(app.Configuration["Api:OpenApi:SwaggerClientId"]);
            options.OAuthScopes(EmailValidationScopes.All.ToArray());
        });
        return app;
    }

    public static async Task ExportOpenApiAsync(IServiceProvider services, string outputPath)
    {
        var provider = services.GetRequiredService<ISwaggerProvider>();
        var document = provider.GetSwagger("v1");
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await using var stream = File.Create(outputPath);
        await using var text = new StreamWriter(stream);
        var writer = new OpenApiJsonWriter(text, new OpenApiJsonWriterSettings { Terse = false });
        document.SerializeAsV3(writer);
        await text.FlushAsync().ConfigureAwait(false);
    }

    private static Uri ResolveUrl(string? configured, string authority, string suffix)
    {
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)) return uri;
        if (Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
            return new Uri($"{authorityUri.ToString().TrimEnd('/')}/{suffix}");
        return new Uri($"https://identity.example.invalid/{suffix}");
    }
}

public sealed class ScopeSecurityDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var scheme = new OpenApiSecuritySchemeReference("oauth2", swaggerDoc);
        foreach (var description in context.ApiDescriptions)
        {
            var scopes = description.ActionDescriptor.EndpointMetadata
                .OfType<IAuthorizeData>()
                .SelectMany(value => ScopesForPolicy(value.Policy))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (scopes.Count == 0) continue;
            var path = "/" + (description.RelativePath?.Split('?')[0] ?? string.Empty);
            if (!swaggerDoc.Paths.TryGetValue(path, out var pathItem)) continue;
            var method = description.HttpMethod?.ToUpperInvariant() switch
            {
                "GET" => HttpMethod.Get,
                "POST" => HttpMethod.Post,
                "PUT" => HttpMethod.Put,
                "PATCH" => HttpMethod.Patch,
                "DELETE" => HttpMethod.Delete,
                _ => (HttpMethod?)null
            };
            if (method is null || pathItem.Operations is null ||
                !pathItem.Operations.TryGetValue(method, out var operation)) continue;
            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [scheme] = scopes
                }
            ];
        }

        if (swaggerDoc.Paths.TryGetValue("/v1/email-validations", out var validationPath) &&
            validationPath.Operations?.TryGetValue(HttpMethod.Post, out var validationOperation) == true)
        {
            AddExample(validationOperation, "200", "finalValid", "Final valid", """
                {"validationId":"01J...","email":"person@example.com","lifecycleState":"Final","resultState":"Final","status":"Valid","subStatus":"MailboxAccepted","confidence":0.94,"provider":"Microsoft365","source":"LiveValidation","retryScheduled":false,"attemptNumber":1,"maxAttempts":2}
                """);
            AddExample(validationOperation, "200", "finalInvalid", "Final invalid", """
                {"validationId":"01J...","email":"missing@example.com","lifecycleState":"Final","resultState":"Final","status":"Invalid","subStatus":"MailboxNotFound","confidence":0.99,"provider":"Unknown","source":"LiveValidation","retryScheduled":false,"attemptNumber":1,"maxAttempts":2}
                """);
            AddExample(validationOperation, "200", "provisionalRetry", "Provisional retry scheduled", """
                {"validationId":"01J...","email":"person@example.com","lifecycleState":"RetryScheduled","resultState":"Provisional","status":"Unknown","subStatus":"TemporaryFailure","confidence":0.82,"provider":"Microsoft365","source":"LiveValidation","retryScheduled":true,"retryAtUtc":"2026-08-23T18:30:00Z","attemptNumber":1,"maxAttempts":2}
                """);
            AddExample(validationOperation, "200", "unknown", "Final unknown", """
                {"validationId":"01J...","email":"person@example.com","lifecycleState":"Final","resultState":"Final","status":"Unknown","subStatus":"VerificationBlocked","confidence":0.88,"provider":"Google","source":"LiveValidation","retryScheduled":false,"attemptNumber":2,"maxAttempts":2}
                """);
            AddExample(validationOperation, "200", "risky", "Risky mailbox", """
                {"validationId":"01J...","email":"person@example.com","lifecycleState":"Final","resultState":"Final","status":"Risky","subStatus":"MailboxFull","confidence":0.9,"provider":"Unknown","source":"LiveValidation","retryScheduled":false,"attemptNumber":1,"maxAttempts":2}
                """);
            AddProblemExample(validationOperation, "401", "Unauthorized", "A bearer token is required.");
            AddProblemExample(validationOperation, "403", "Forbidden", "The token does not contain the required scope.");
            AddProblemExample(validationOperation, "429", "Rate limit exceeded", "Retry later.");
        }
    }

    private static void AddProblemExample(OpenApiOperation operation, string status, string title, string detail) =>
        AddExample(operation, status, title.Replace(" ", string.Empty), title,
            $$"""{"type":"https://httpstatuses.com/{{status}}","title":"{{title}}","status":{{status}},"detail":"{{detail}}","traceId":"4bf92f3577b34da6a3ce929d0e0e4736"}""");

    private static void AddExample(
        OpenApiOperation operation,
        string status,
        string name,
        string summary,
        string json)
    {
        if (!operation.Responses!.TryGetValue(status, out var response) || response.Content is null) return;
        if (!response.Content.TryGetValue("application/json", out var media) &&
            !response.Content.TryGetValue("application/problem+json", out media)) return;
        if (media is null) return;
        media.Examples ??= new Dictionary<string, IOpenApiExample>();
        media.Examples[name] = new OpenApiExample
        {
            Summary = summary,
            Value = JsonNode.Parse(json)
        };
    }

    private static IEnumerable<string> ScopesForPolicy(string? policy) => policy switch
    {
        EmailValidationPolicies.Validate => [EmailValidationScopes.Validate],
        EmailValidationPolicies.Read => [EmailValidationScopes.Read],
        EmailValidationPolicies.JobsWrite => [EmailValidationScopes.JobsWrite],
        EmailValidationPolicies.JobsRead => [EmailValidationScopes.JobsRead],
        EmailValidationPolicies.Stream => [EmailValidationScopes.Stream],
        EmailValidationPolicies.Admin => [EmailValidationScopes.Admin],
        _ => []
    };
}
