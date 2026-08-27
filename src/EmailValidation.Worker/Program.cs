using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailValidation.Core;
using EmailValidation.Application;
using EmailValidation.Infrastructure;
using EmailValidation.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
try
{
    builder.Configuration.AddEmailValidationAzureAppConfiguration(builder.Environment);
}
catch (EmailValidationConfigurationException exception)
{
    await Console.Error.WriteLineAsync($"Configuration error: {exception.Message}");
    return 2;
}
builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<EmailValidationOptions>(builder.Configuration.GetSection("EmailValidation"));
builder.Services.PostConfigure<EmailValidationOptions>(options =>
    options.ProbeSenderSource.QueryJson = SerializeConfigurationNode(
        builder.Configuration.GetSection("EmailValidation:ProbeSenderSource:Query")).ToJsonString());
builder.Services.AddEmailValidation();
builder.Services.AddHostedService<ServiceBusRevalidationWorker>();
builder.Services.AddHostedService<RevalidationOutboxPublisherService>();
builder.Services.AddHostedService<ServiceBusValidationJobWorker>();
builder.Services.AddHostedService<ProjectionOutboxPublisherWorker>();
builder.Services.AddHostedService<ElasticsearchProjectionWorker>();
builder.Services.AddHostedService<ProjectionReconciliationWorker>();

using var host = builder.Build();
try
{
    await host.Services.GetRequiredService<IEmailValidationPersistenceInitializer>().InitializeAsync();
    await host.Services.GetRequiredService<IRevalidationInfrastructureInitializer>().InitializeAsync();
    await host.Services.GetRequiredService<IValidationJobInfrastructureInitializer>().InitializeAsync();
    await host.Services.GetRequiredService<ProjectionInfrastructureInitializer>().InitializeAsync();
    if (GetArgument(args, "--projection-backfill-from") is { } fromText)
    {
        if (!DateTimeOffset.TryParse(fromText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var from) ||
            !DateTimeOffset.TryParse(GetArgument(args, "--projection-backfill-to"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var to))
            throw new InvalidOperationException(
                "Projection backfill requires valid --projection-backfill-from and --projection-backfill-to UTC timestamps.");
        var projection = host.Services.GetRequiredService<IOptions<EmailValidationOptions>>().Value.Projection;
        var request = new ProjectionReplayRequest(
            from, to,
            int.TryParse(GetArgument(args, "--projection-backfill-batch-size"), out var batchSize)
                ? batchSize : projection.Reconciliation.BatchSize,
            int.TryParse(GetArgument(args, "--projection-backfill-max-events"), out var maxEvents)
                ? maxEvents : projection.Reconciliation.MaximumEventsPerRun,
            DryRun: !args.Contains("--projection-backfill-commit", StringComparer.Ordinal),
            EventType: GetArgument(args, "--projection-backfill-event-type"),
            TenantId: GetArgument(args, "--projection-backfill-tenant"));
        var result = await host.Services.GetRequiredService<IProjectionReconciler>().BackfillAsync(request);
        ProjectionTelemetry.RecordBackfillProgress(result.EventsConsidered, result.DryRun);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 0;
    }
    await host.RunAsync();
    return 0;
}
catch (OptionsValidationException exception)
{
    await Console.Error.WriteLineAsync($"Configuration error: {string.Join(" ", exception.Failures)}");
    return 2;
}

static string? GetArgument(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, item => string.Equals(item, name, StringComparison.Ordinal));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
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
