using EmailValidation.ConsoleApp;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
builder.Services.AddSingleton<IDomainValidationScheduler, DomainValidationScheduler>();
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
