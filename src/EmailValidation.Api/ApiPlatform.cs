using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using EmailValidation.Core;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EmailValidation.Api;

public static class ApiRateLimitPolicies
{
    public const string Requests = "api-requests";
    public const string Streams = "grpc-streams";
}

public static class ApiPlatformExtensions
{
    public static IServiceCollection AddEmailValidationApiPlatform(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ApiHostOptions>()
            .Bind(configuration.GetSection("Api"))
            .Validate(options => options.Limits.MaximumRequestBodyBytes is >= 16_384 and <= 100_000_000,
                "Api:Limits:MaximumRequestBodyBytes must be between 16384 and 100000000.")
            .Validate(options => options.RateLimiting.PermitLimit > 0 &&
                    options.RateLimiting.WindowSeconds > 0 && options.RateLimiting.StreamConcurrencyLimit > 0,
                "API rate limit values must be positive.")
            .ValidateOnStart();

        var hostOptions = configuration.GetSection("Api").Get<ApiHostOptions>() ?? new();
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
            options.Limits.MaxRequestBodySize = hostOptions.Limits.MaximumRequestBodyBytes);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    type = "https://httpstatuses.com/429",
                    title = "Rate limit exceeded",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = "The consumer rate limit was exceeded. Retry later.",
                    instance = context.HttpContext.Request.Path.Value,
                    traceId = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier
                }, cancellationToken).ConfigureAwait(false);
            };
            options.AddPolicy(ApiRateLimitPolicies.Requests, http =>
                RateLimitPartition.GetFixedWindowLimiter(PartitionKey(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = hostOptions.RateLimiting.PermitLimit,
                    Window = TimeSpan.FromSeconds(hostOptions.RateLimiting.WindowSeconds),
                    QueueLimit = hostOptions.RateLimiting.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                }));
            options.AddPolicy(ApiRateLimitPolicies.Streams, http =>
                RateLimitPartition.GetConcurrencyLimiter(PartitionKey(http), _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = hostOptions.RateLimiting.StreamConcurrencyLimit,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));
        });

        services.AddCors();
        services.AddHttpClient<IEmailValidationSourceFileClient, OpenMetaEmailValidationSourceFileClient>();

        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<ApiReadinessHealthCheck>("critical-dependencies", tags: ["ready"]);
        services.AddSingleton<ApiTelemetry>();
        return services;
    }

    public static WebApplication UseEmailValidationApiPlatform(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.CacheControl = "no-store";
            await next(context).ConfigureAwait(false);
        });
        app.UseExceptionHandler();
        var allowedOrigins = app.Configuration.GetSection("Api:Cors:AllowedOrigins").Get<string[]>();
        if (allowedOrigins?.Length > 0)
            app.UseCors(policy => policy.WithOrigins(allowedOrigins)
                .WithMethods("GET", "POST")
                .WithHeaders(
                    "Authorization",
                    "Content-Type",
                    "Idempotency-Key",
                    "X-Correlation-ID",
                    "traceparent"));
        app.UseAuthentication();
        app.UseMiddleware<ApiRequestTelemetryMiddleware>();
        app.UseRateLimiter();
        app.UseAuthorization();
        return app;
    }

    public static IEndpointRouteBuilder MapEmailValidationHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("live"),
                ResponseWriter = WriteMinimalHealthAsync
            })
            .AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("ready"),
                ResponseWriter = WriteMinimalHealthAsync
            })
            .AllowAnonymous();
        return endpoints;
    }

    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue("tenant_id") ?? context.User.FindFirstValue("tid") ??
        context.User.FindFirstValue("sub") ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static Task WriteMinimalHealthAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { status = report.Status.ToString() }));
    }
}

public sealed class ApiReadinessHealthCheck(
    IOptions<EmailValidationOptions> options,
    IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var persistence = options.Value.Persistence;
        if (!persistence.Enabled || !string.Equals(persistence.Provider, "MongoDB", StringComparison.OrdinalIgnoreCase))
            return HealthCheckResult.Healthy();
        try
        {
            var mongo = services.GetRequiredService<IMongoClient>();
            await mongo.GetDatabase(persistence.DatabaseName)
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is MongoException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("A critical dependency is unavailable.");
        }
    }
}

public sealed class ApiTelemetry : IDisposable
{
    private readonly Meter _meter = new("EmailValidation.Api");
    public Counter<long> Requests { get; }
    public Histogram<double> Duration { get; }
    public Counter<long> AuthenticationFailures { get; }
    public Counter<long> AuthorizationFailures { get; }
    public Counter<long> RateLimited { get; }
    public Counter<long> ValidationRequests { get; }

    public ApiTelemetry()
    {
        Requests = _meter.CreateCounter<long>("email_validation.api.requests");
        Duration = _meter.CreateHistogram<double>("email_validation.api.request_duration", "ms");
        AuthenticationFailures = _meter.CreateCounter<long>("email_validation.api.authentication_failures");
        AuthorizationFailures = _meter.CreateCounter<long>("email_validation.api.authorization_failures");
        RateLimited = _meter.CreateCounter<long>("email_validation.api.rate_limited");
        ValidationRequests = _meter.CreateCounter<long>("email_validation.api.validation_requests");
    }

    public void Dispose() => _meter.Dispose();
}

public sealed class ApiRequestTelemetryMiddleware(
    RequestDelegate next,
    ILogger<ApiRequestTelemetryMiddleware> logger,
    ApiTelemetry telemetry)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        using (logger.BeginScope(new Dictionary<string, object?>
               {
                   ["TraceId"] = traceId,
                   ["ConsumerId"] = context.User.FindFirstValue("sub"),
                   ["TenantId"] = context.User.FindFirstValue("tenant_id") ?? context.User.FindFirstValue("tid")
               }))
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var endpoint = context.GetEndpoint()?.DisplayName ?? context.Request.Path.Value ?? "unknown";
                telemetry.Requests.Add(1,
                    new KeyValuePair<string, object?>("method", context.Request.Method),
                    new KeyValuePair<string, object?>("status_code", context.Response.StatusCode));
                telemetry.Duration.Record(elapsed,
                    new KeyValuePair<string, object?>("endpoint", endpoint),
                    new KeyValuePair<string, object?>("status_code", context.Response.StatusCode));
                if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                    telemetry.AuthenticationFailures.Add(1);
                if (context.Response.StatusCode == StatusCodes.Status403Forbidden)
                    telemetry.AuthorizationFailures.Add(1);
                if (context.Response.StatusCode == StatusCodes.Status429TooManyRequests)
                    telemetry.RateLimited.Add(1);
                if (string.Equals(endpoint, "CreateEmailValidationV1", StringComparison.Ordinal))
                    telemetry.ValidationRequests.Add(1);
                logger.LogInformation(
                    "API request {Method} {Endpoint} completed with {StatusCode} in {DurationMs:F1} ms",
                    context.Request.Method, endpoint, context.Response.StatusCode, elapsed);
            }
        }
    }
}
