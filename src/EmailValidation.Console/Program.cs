using EmailValidation.ConsoleApp;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
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

return await host.Services.GetRequiredService<ConsoleApplication>().RunAsync(args, cancellation.Token);
