using EmailValidation.ConsoleApp;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

if (args.Length > 0 && args[0] is "provider-policy-hash" or "provider-shadow-report")
{
    try
    {
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        if (args[0] == "provider-policy-hash" && args.Length == 2)
        {
            var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(args[1])).Build();
            var settings = new EmailValidationOptions();
            config.GetSection("EmailValidation").Bind(settings);
            await System.Console.Out.WriteLineAsync(ProviderCapabilityPolicy.Fingerprint(settings.ProviderCapabilities));
            return 0;
        }
        if (args[0] == "provider-shadow-report" && args.Length == 3)
        {
            var snapshots = System.Text.Json.JsonSerializer.Deserialize<EmailValidationFeatureSnapshot[]>(
                await File.ReadAllTextAsync(args[1]), jsonOptions) ?? [];
            await File.WriteAllTextAsync(args[2], System.Text.Json.JsonSerializer.Serialize(
                ProviderCapabilityReport.Summarize(snapshots), jsonOptions));
            await System.Console.Out.WriteLineAsync("Provider shadow report written. No release approval is implied.");
            return 0;
        }
        await System.Console.Error.WriteLineAsync("Usage: provider-policy-hash <appsettings.json> | provider-shadow-report <snapshots.json> <report.json>");
        return 2;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException)
    {
        await System.Console.Error.WriteLineAsync($"Provider policy command rejected: {exception.GetType().Name}");
        return 2;
    }
}

if (args.Length > 0 && args[0] == "benchmark")
{
    if (args.Length is < 3 or > 4)
    {
        await System.Console.Error.WriteLineAsync("Usage: benchmark <frozen-evidence.json> <report.json> [candidate-model.json]");
        return 2;
    }
    try
    {
        var report = await OfflineAccuracyBenchmark.RunAsync(args[1], args[2], args.Length == 4 ? args[3] : null);
        await System.Console.Out.WriteLineAsync($"Benchmark {report.DatasetId}: {report.Cohort}; report written to {args[2]}. No release approval is implied.");
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or System.Text.Json.JsonException)
    {
        await System.Console.Error.WriteLineAsync($"Benchmark rejected: {exception.Message}");
        return 2;
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
try
{
    builder.Configuration.AddEmailValidationAzureAppConfiguration(builder.Environment);
}
catch (EmailValidationConfigurationException exception)
{
    await System.Console.Error.WriteLineAsync($"Configuration error: {exception.Message}");
    return 2;
}
// The explicit output-directory JSON file is added after the host defaults, so add
// environment variables again to preserve the standard .NET override precedence.
builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<EmailValidationOptions>(builder.Configuration.GetSection("EmailValidation"));
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
    await host.Services.GetRequiredService<IEmailValidationPersistenceInitializer>()
        .InitializeAsync(cancellation.Token);
    await host.Services.GetRequiredService<IRevalidationInfrastructureInitializer>()
        .InitializeAsync(cancellation.Token);
    return await host.Services.GetRequiredService<ConsoleApplication>().RunAsync(args, cancellation.Token);
}
catch (OptionsValidationException exception)
{
    await System.Console.Error.WriteLineAsync($"Configuration error: {string.Join(" ", exception.Failures)}");
    return 2;
}
catch (EmailValidationPersistenceException exception)
{
    await System.Console.Error.WriteLineAsync(
        $"Persistence error: {exception.Message} ({exception.InnerException?.GetType().Name ?? "Unknown"})");
    return 2;
}
