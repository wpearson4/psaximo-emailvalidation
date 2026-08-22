using Azure;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Hosting;

namespace EmailValidation.Infrastructure;

public sealed class EmailValidationConfigurationException(string message, Exception innerException)
    : Exception(message, innerException);

public static class EmailValidationAzureConfiguration
{
    public const string EndpointKey = "Azure:AppConfigurationEndpoint";
    public const string ConnectionStringKey = "Azure:AppConfigurationConnectionString";
    public const string LabelKey = "Azure:AppConfigurationLabel";
    public const string EndpointEnvironmentVariable = "AZURE_APPCONFIG_ENDPOINT";

    public static IConfigurationBuilder AddEmailValidationAzureAppConfiguration(
        this IConfigurationBuilder builder,
        IHostEnvironment environment)
    {
        var bootstrap = builder.Build();
        var endpoint = ResolveEndpoint(bootstrap);
        var connectionString = ResolveConnectionString(bootstrap);
        if (string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"Azure App Configuration bootstrap is missing. Configure {ConnectionStringKey}, {EndpointKey}, or {EndpointEnvironmentVariable}.");

        var label = ResolveLabel(bootstrap, environment.EnvironmentName);
        var credential = new DefaultAzureCredential();
        try
        {
            builder.AddAzureAppConfiguration(options =>
            {
                if (string.IsNullOrWhiteSpace(connectionString))
                    options.Connect(new Uri(endpoint), credential);
                else
                    options.Connect(connectionString);
                options
                    .Select("EmailValidation:*", LabelFilter.Null)
                    .Select("EmailValidation:*", label)
                    .ConfigureKeyVault(keyVault => keyVault.SetCredential(credential));
            });
            return builder;
        }
        catch (RequestFailedException exception)
        {
            throw new EmailValidationConfigurationException(
                $"Azure App Configuration could not be loaded from '{endpoint}' with label '{label}'. Verify Azure login and data-plane access.",
                exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new EmailValidationConfigurationException(
                $"Azure authentication failed while loading App Configuration '{endpoint}'. Run 'az login' and verify the active subscription.",
                exception);
        }
    }

    public static string ResolveEndpoint(IConfiguration configuration)
    {
        var configured = configuration[EndpointKey];
        return string.IsNullOrWhiteSpace(configured)
            ? Environment.GetEnvironmentVariable(EndpointEnvironmentVariable) ?? string.Empty
            : configured.Trim();
    }

    public static string ResolveConnectionString(IConfiguration configuration) =>
        configuration[ConnectionStringKey]?.Trim() ?? string.Empty;

    public static string ResolveLabel(IConfiguration configuration, string environmentName)
    {
        var configured = configuration[LabelKey];
        return string.IsNullOrWhiteSpace(configured) ? environmentName : configured.Trim();
    }
}
