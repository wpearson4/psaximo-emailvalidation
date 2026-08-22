using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using EmailValidation.Core;
using EmailValidation.Grpc;
using EmailValidation.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
try
{
    builder.Configuration.AddEmailValidationAzureAppConfiguration(builder.Environment);
}
catch (EmailValidationConfigurationException exception)
{
    Console.Error.WriteLine($"Configuration error: {exception.Message}");
    return;
}
builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<EmailValidationOptions>(builder.Configuration.GetSection("EmailValidation"));
builder.Services.PostConfigure<EmailValidationOptions>(options =>
    options.ProbeSenderSource.QueryJson = SerializeConfigurationNode(
        builder.Configuration.GetSection("EmailValidation:ProbeSenderSource:Query")).ToJsonString());
builder.Services.AddEmailValidation();
builder.Services.AddGrpc();
builder.Services.AddRateLimiter(options => options.AddPolicy("status-streams", httpContext =>
    RateLimitPartition.GetConcurrencyLimiter(
        httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = 20,
            QueueLimit = 0
        })));

var app = builder.Build();
try
{
    await app.Services.GetRequiredService<IEmailValidationPersistenceInitializer>().InitializeAsync();
    await app.Services.GetRequiredService<IRevalidationPersistenceInitializer>().InitializeAsync();
}
catch (OptionsValidationException exception)
{
    app.Logger.LogCritical("Configuration error: {Failures}", string.Join(" ", exception.Failures));
    return;
}
app.UseRateLimiter();
app.MapGrpcService<EmailValidationStatusGrpcService>().RequireRateLimiting("status-streams");
app.MapGet("/", () => Results.Text("Email validation status gRPC endpoint. Use a gRPC client."));
await app.RunAsync();

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
