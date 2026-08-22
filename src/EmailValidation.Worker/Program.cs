using System.Globalization;
using System.Text.Json.Nodes;
using EmailValidation.Core;
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

using var host = builder.Build();
try
{
    await host.Services.GetRequiredService<IEmailValidationPersistenceInitializer>().InitializeAsync();
    await host.Services.GetRequiredService<IRevalidationInfrastructureInitializer>().InitializeAsync();
    await host.RunAsync();
    return 0;
}
catch (OptionsValidationException exception)
{
    await Console.Error.WriteLineAsync($"Configuration error: {string.Join(" ", exception.Failures)}");
    return 2;
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
