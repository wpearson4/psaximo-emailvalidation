using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EmailValidation.Api;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Grpc;
using EmailValidation.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;

if (args.Contains("--healthcheck", StringComparer.Ordinal))
    return await RunContainerHealthCheckAsync().ConfigureAwait(false);

var builder = WebApplication.CreateBuilder(args);
var exportingOpenApi = args.Length >= 2 && string.Equals(args[0], "--export-openapi", StringComparison.Ordinal);
if (!builder.Environment.IsEnvironment("Testing") &&
    builder.Configuration.GetValue("Azure:AppConfigurationEnabled", true))
    builder.Configuration.AddEmailValidationAzureAppConfiguration(builder.Environment);
builder.Configuration.AddEnvironmentVariables();
if (exportingOpenApi)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["EmailValidation:Persistence:Enabled"] = "false",
        ["EmailValidation:Persistence:Provider"] = "Json",
        ["EmailValidation:Persistence:StoragePath"] = "openapi-generation",
        ["EmailValidation:ProbeSenderSource:Index"] = "openapi-generation",
        ["EmailValidation:ProbeSenderSource:Query:match_all:enabled"] = "true",
        ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
        ["Kestrel:Endpoints:Grpc:Url"] = "http://127.0.0.1:0"
    });
}

builder.Services.Configure<EmailValidationOptions>(builder.Configuration.GetSection("EmailValidation"));
builder.Services.PostConfigure<EmailValidationOptions>(options =>
    options.ProbeSenderSource.QueryJson = SerializeConfigurationNode(
        builder.Configuration.GetSection("EmailValidation:ProbeSenderSource:Query")).ToJsonString());
builder.Services.AddEmailValidation();
builder.Services.RemoveAll<IValidationAccessPolicy>();
builder.Services.AddSingleton<IValidationAccessPolicy, CommercialValidationAccessPolicy>();
builder.Services.AddSingleton<IValidationJobAccessPolicy, CommercialValidationJobAccessPolicy>();
builder.Services.AddEmailValidationSecurity(builder.Configuration, builder.Environment);
builder.Services.AddEmailValidationApiPlatform(builder.Configuration, builder.Environment);
builder.Services.AddEmailValidationOpenApi(builder.Configuration);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddGrpc(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddGrpcReflection();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["traceId"] =
        Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
    context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
});

var app = builder.Build();
app.MapEmailValidationHealth();
app.MapEmailValidationV1();
app.MapGrpcService<EmailValidationGrpcService>()
    .RequireRateLimiting(ApiRateLimitPolicies.Requests);
app.MapGrpcService<EmailValidationStatusGrpcService>()
    .RequireRateLimiting(ApiRateLimitPolicies.Streams);
if (app.Environment.IsDevelopment())
    app.MapGrpcReflectionService().RequireAuthorization(EmailValidationPolicies.Admin);

if (exportingOpenApi)
{
    await app.StartAsync().ConfigureAwait(false);
    try
    {
        await ApiOpenApiExtensions.ExportOpenApiAsync(app.Services, args[1]).ConfigureAwait(false);
    }
    finally
    {
        await app.StopAsync().ConfigureAwait(false);
    }
    return 0;
}

if (!app.Environment.IsEnvironment("Testing"))
{
    await app.Services.GetRequiredService<IEmailValidationPersistenceInitializer>().InitializeAsync();
    await app.Services.GetRequiredService<IRevalidationInfrastructureInitializer>().InitializeAsync();
    await app.Services.GetRequiredService<IValidationJobInfrastructureInitializer>().InitializeAsync();
    await app.Services.GetRequiredService<ICommercialResourceInfrastructureInitializer>().InitializeAsync();
}

app.UseForwardedHeaders();
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    if (response.HasStarted || statusContext.HttpContext.Request.ContentType?.StartsWith(
            "application/grpc", StringComparison.OrdinalIgnoreCase) == true) return;
    await Results.Problem(
        statusCode: response.StatusCode,
        title: ReasonPhrases.GetReasonPhrase(response.StatusCode),
        instance: statusContext.HttpContext.Request.Path)
        .ExecuteAsync(statusContext.HttpContext).ConfigureAwait(false);
});
app.MapEmailValidationOpenApi();
app.UseEmailValidationApiPlatform();

await app.RunAsync();
return 0;

static async Task<int> RunContainerHealthCheckAsync()
{
    var url = Environment.GetEnvironmentVariable("EMAILVALIDATION_HEALTHCHECK_URL") ??
        "http://127.0.0.1:8080/health/live";
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var client = new HttpClient();
    try
    {
        using var response = await client.GetAsync(url, timeout.Token).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
    {
        return 1;
    }
}

static JsonNode SerializeConfigurationNode(IConfigurationSection section)
{
    var children = section.GetChildren().ToArray();
    if (children.Length == 0)
    {
        var value = section.Value;
        if (bool.TryParse(value, out var boolean)) return JsonValue.Create(boolean);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)) return null!;
        return JsonValue.Create(value ?? string.Empty);
    }
    if (children.Select(child => child.Key).SequenceEqual(
            Enumerable.Range(0, children.Length).Select(index => index.ToString(CultureInfo.InvariantCulture))))
        return new JsonArray(children.Select(SerializeConfigurationNode).ToArray());
    var result = new JsonObject();
    foreach (var child in children) result[child.Key] = SerializeConfigurationNode(child);
    return result;
}

#pragma warning disable CA1050
public partial class Program;
#pragma warning restore CA1050
