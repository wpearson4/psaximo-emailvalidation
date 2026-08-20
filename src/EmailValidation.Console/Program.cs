using EmailValidation.ConsoleApp;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json.Nodes;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
// The explicit output-directory JSON file is added after the host defaults, so add
// environment variables again to preserve the standard .NET override precedence.
builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<EmailValidationOptions>(builder.Configuration.GetSection("EmailValidation"));
builder.Services.PostConfigure<EmailValidationOptions>(options =>
    options.ProbeSenderSource.QueryJson = SerializeConfigurationNode(
        builder.Configuration.GetSection("EmailValidation:ProbeSenderSource:Query")).ToJsonString());
builder.Services.AddEmailValidation();
builder.Services.AddSingleton<CsvFileProcessor>();
builder.Services.AddSingleton<ConsoleApplication>();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

using var host = builder.Build();
using var cancellation = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    return await host.Services.GetRequiredService<ConsoleApplication>().RunAsync(args, cancellation.Token);
}
catch (OptionsValidationException exception)
{
    await System.Console.Error.WriteLineAsync($"Configuration error: {string.Join(" ", exception.Failures)}");
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
