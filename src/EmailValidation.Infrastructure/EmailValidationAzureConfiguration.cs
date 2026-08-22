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
    public const string MongoSecretUriKey = "Azure:MongoConnectionSecretUri";
    public const string MongoConnectionStringKey = "EmailValidation:Persistence:ConnectionString";
    public const string LabelKey = "Azure:AppConfigurationLabel";
    public const string EndpointEnvironmentVariable = "AZURE_APPCONFIG_ENDPOINT";

    public static IConfigurationBuilder AddEmailValidationAzureAppConfiguration(
        this IConfigurationBuilder builder,
        IHostEnvironment environment)
    {
        var bootstrap = builder.Build();
        var endpoint = ResolveEndpoint(bootstrap);
        var connectionString = ResolveConnectionString(bootstrap);
        var localMongoConnectionString = bootstrap[MongoConnectionStringKey]?.Trim() ?? string.Empty;
        var mongoSecretUri = bootstrap[MongoSecretUriKey]?.Trim() ?? string.Empty;
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
                    .ConfigureKeyVault(keyVault =>
                    {
                        if (string.IsNullOrWhiteSpace(localMongoConnectionString) ||
                            string.IsNullOrWhiteSpace(mongoSecretUri))
                        {
                            keyVault.SetCredential(credential);
                            return;
                        }

                        keyVault.SetSecretResolver(secretUri =>
                        {
                            if (MatchesSecret(secretUri, mongoSecretUri))
                                return ValueTask.FromResult(localMongoConnectionString);
                            throw new InvalidOperationException(
                                "No local override is configured for an Azure Key Vault reference.");
                        });
                    });
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
            var target = string.IsNullOrWhiteSpace(connectionString)
                ? $"loading App Configuration '{endpoint}'"
                : "resolving an Azure Key Vault reference";
            throw new EmailValidationConfigurationException(
                $"Azure authentication failed while {target}. Run 'az login' and verify the active subscription, or configure the required local connection string.",
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

    public static bool MatchesSecret(Uri actualSecretUri, string configuredSecretUri)
    {
        if (!Uri.TryCreate(configuredSecretUri, UriKind.Absolute, out var configured)) return false;
        var configuredPath = configured.AbsolutePath.TrimEnd('/');
        var actualPath = actualSecretUri.AbsolutePath.TrimEnd('/');
        return string.Equals(actualSecretUri.Host, configured.Host, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(actualPath, configuredPath, StringComparison.OrdinalIgnoreCase) ||
             actualPath.StartsWith($"{configuredPath}/", StringComparison.OrdinalIgnoreCase));
    }
}
