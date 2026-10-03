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
            [EmailValidationScopes.Validate] = "Validate arbitrary addresses only when interactive or administratively authorized.",
            [EmailValidationScopes.Read] = "Read a validation resource.",
            [EmailValidationScopes.JobsWrite] = "Create durable jobs; standard machine clients are limited to owned purchased results.",
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
                Description = "Secure REST boundary for canonical Email Validation. Standard machine clients may validate only email columns in completed Search or Match & Append results purchased by their API client. The service resolves ownership and field inclusion from the authoritative purchased output; knowing a transaction identifier or email address does not grant access. Explicitly authorized administrators may validate arbitrary addresses."
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
                {"validationId":"01J...","email":"person@example.com","lifecycleState":"RetryScheduled","resultState":"Provisional","status":"Unknown","subStatus":"TemporaryFailure","confidence":0.82,"unknownContext":{"cause":"TemporarySmtpFailure","summary":"The destination returned a temporary SMTP failure.","retryable":true,"recommendedAction":"Retry after the destination or provider cooldown clears.","smtpCategory":"TemporaryFailure","failedStage":"RcptTo","responseCode":451,"enhancedStatusCode":"4.7.1","mxHost":"mx.example.com","retryAfterUtc":"2026-08-23T18:30:00Z"},"provider":"Microsoft365","source":"LiveValidation","retryScheduled":true,"retryAtUtc":"2026-08-23T18:30:00Z","attemptNumber":1,"maxAttempts":2}
                """);
            AddExample(validationOperation, "200", "unknown", "Final unknown", """
                {"validationId":"01J...","email":"person@example.com","lifecycleState":"Final","resultState":"Final","status":"Unknown","subStatus":"VerificationBlocked","confidence":0.88,"unknownContext":{"cause":"ProviderVerificationBlocked","summary":"The destination did not provide recipient-specific verification evidence.","retryable":true,"recommendedAction":"Wait for the provider cooldown and retry later; do not attempt to circumvent the provider policy.","smtpCategory":"VerificationBlocked","failedStage":"RcptTo","responseCode":550,"enhancedStatusCode":"5.7.1","mxHost":"mx.example.com"},"provider":"Google","source":"LiveValidation","retryScheduled":false,"attemptNumber":2,"maxAttempts":2}
                """);
            AddExample(validationOperation, "200", "risky", "Risky mailbox", """
                {"validationId":"01J...","email":"person@example.com","lifecycleState":"Final","resultState":"Final","status":"Risky","subStatus":"MailboxFull","confidence":0.9,"provider":"Unknown","source":"LiveValidation","retryScheduled":false,"attemptNumber":1,"maxAttempts":2}
                """);
            AddProblemExample(validationOperation, "401", "Unauthorized", "A bearer token is required.");
            AddProblemExample(validationOperation, "403", "Forbidden", "The token does not contain the required scope.");
            AddProblemExample(validationOperation, "429", "Rate limit exceeded", "Retry later.");
        }

        if (swaggerDoc.Paths.TryGetValue(
                "/v1/purchased-results/{transactionId}/email-validation", out var purchasedPath) &&
            purchasedPath.Operations?.TryGetValue(HttpMethod.Post, out var purchasedOperation) == true)
        {
            AddExample(purchasedOperation, "202", "searchPurchased", "Search-purchased email validation accepted", """
                {"jobId":"file_5f4dcc3b5aa765d61d8327deb882cf99","createdAtUtc":"2026-10-03T20:00:00Z","state":"Queued","totalItems":1250,"processedItems":0,"finalItems":0,"provisionalItems":0,"failedItems":0,"updatedAtUtc":"2026-10-03T20:00:00Z","enableSmtp":true,"sourceFileId":"txn_01JSEARCH","sourceFileName":"search-results.csv","emailColumn":"Email"}
                """);
            AddExample(purchasedOperation, "202", "matchPurchased", "Match & Append-purchased email validation accepted", """
                {"jobId":"file_098f6bcd4621d373cade4e832627b4f6","createdAtUtc":"2026-10-03T20:00:00Z","state":"Queued","totalItems":640,"processedItems":0,"finalItems":0,"provisionalItems":0,"failedItems":0,"updatedAtUtc":"2026-10-03T20:00:00Z","enableSmtp":true,"sourceFileId":"txn_01JMATCH","sourceFileName":"match-results.csv","emailColumn":"Appended Email"}
                """);
            AddCodedProblemExample(purchasedOperation, "403", "EMAIL_VALIDATION_NOT_AUTHORIZED",
                "Email validation is not authorized", "The access token cannot validate this purchased result.");
            AddCodedProblemExample(purchasedOperation, "404", "PURCHASED_RESULT_NOT_FOUND",
                "Purchased result not found",
                "The purchased result does not exist or is not available to this API client.");
            AddCodedProblemExample(purchasedOperation, "409", "PURCHASE_NOT_COMPLETED",
                "Purchase is not complete",
                "Wait for the Search or Match & Append transaction to complete, then retry.");
            AddCodedProblemExample(purchasedOperation, "422", "EMAIL_NOT_INCLUDED_IN_PURCHASE",
                "Email was not included in the purchase",
                "Choose an email column that is present in the purchased output.");
            AddProblemExample(purchasedOperation, "429", "Rate limit exceeded", "Retry later.");
            AddProblemExample(purchasedOperation, "503", "Purchased-result service unavailable", "Retry later.");
        }
    }

    private static void AddProblemExample(OpenApiOperation operation, string status, string title, string detail) =>
        AddExample(operation, status, title.Replace(" ", string.Empty), title,
            $$"""{"type":"https://httpstatuses.com/{{status}}","title":"{{title}}","status":{{status}},"detail":"{{detail}}","traceId":"4bf92f3577b34da6a3ce929d0e0e4736"}""");

    private static void AddCodedProblemExample(
        OpenApiOperation operation,
        string status,
        string code,
        string title,
        string detail) =>
        AddExample(operation, status, code, title,
            $$"""{"type":"https://email.digitalwarehouse.io/problems/{{code.ToLowerInvariant().Replace('_', '-')}}","title":"{{title}}","status":{{status}},"code":"{{code}}","detail":"{{detail}}","traceId":"4bf92f3577b34da6a3ce929d0e0e4736"}""");

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
        EmailValidationPolicies.PurchasedJobsWrite => [EmailValidationScopes.JobsWrite],
        EmailValidationPolicies.JobsRead => [EmailValidationScopes.JobsRead],
        EmailValidationPolicies.Stream => [EmailValidationScopes.Stream],
        EmailValidationPolicies.Admin => [EmailValidationScopes.Admin],
        _ => []
    };
}
