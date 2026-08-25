using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
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
    public const string ConnectionStringFileKey = "Azure:AppConfigurationConnectionStringFile";
    public const string MongoSecretUriKey = "Azure:MongoConnectionSecretUri";
    public const string MongoConnectionStringFileKey = "Azure:MongoConnectionStringFile";
    public const string MongoConnectionStringKey = "EmailValidation:Persistence:ConnectionString";
    public const string ServiceBusSecretUriKey = "Azure:ServiceBusConnectionSecretUri";
    public const string ServiceBusConnectionStringKey = "EmailValidation:Revalidation:ServiceBus:ConnectionString";
    public const string JobsServiceBusSecretUriKey = "Azure:JobsServiceBusConnectionSecretUri";
    public const string JobsServiceBusConnectionStringFileKey = "Azure:JobsServiceBusConnectionStringFile";
    public const string JobsServiceBusConnectionStringKey = "EmailValidation:Jobs:ServiceBusConnectionString";
    public const string LabelKey = "Azure:AppConfigurationLabel";
    public const string EndpointEnvironmentVariable = "AZURE_APPCONFIG_ENDPOINT";

    public static IConfigurationBuilder AddEmailValidationAzureAppConfiguration(
        this IConfigurationBuilder builder,
        IHostEnvironment environment)
    {
        var bootstrap = builder.Build();
        var endpoint = ResolveEndpoint(bootstrap);
        var connectionString = ResolveConnectionString(bootstrap);
        var localMongoConnectionString = ResolveSecret(
            bootstrap,
            MongoConnectionStringKey,
            MongoConnectionStringFileKey,
            "MongoDB connection string");
        var mongoSecretUri = bootstrap[MongoSecretUriKey]?.Trim() ?? string.Empty;
        var localServiceBusConnectionString = bootstrap[ServiceBusConnectionStringKey]?.Trim() ?? string.Empty;
        var serviceBusSecretUri = bootstrap[ServiceBusSecretUriKey]?.Trim() ?? string.Empty;
        var localJobsServiceBusConnectionString = ResolveSecret(
            bootstrap,
            JobsServiceBusConnectionStringKey,
            JobsServiceBusConnectionStringFileKey,
            "validation jobs Service Bus connection string");
        var jobsServiceBusSecretUri = bootstrap[JobsServiceBusSecretUriKey]?.Trim() ?? string.Empty;
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
                        var hasMongoOverride = !string.IsNullOrWhiteSpace(localMongoConnectionString) &&
                            !string.IsNullOrWhiteSpace(mongoSecretUri);
                        var hasServiceBusOverride = !string.IsNullOrWhiteSpace(localServiceBusConnectionString) &&
                            !string.IsNullOrWhiteSpace(serviceBusSecretUri);
                        var hasJobsServiceBusOverride = !string.IsNullOrWhiteSpace(localJobsServiceBusConnectionString) &&
                            !string.IsNullOrWhiteSpace(jobsServiceBusSecretUri);
                        if (!hasMongoOverride && !hasServiceBusOverride && !hasJobsServiceBusOverride)
                        {
                            keyVault.SetCredential(credential);
                            return;
                        }

                        keyVault.SetSecretResolver(async secretUri =>
                        {
                            if (MatchesSecret(secretUri, mongoSecretUri))
                                return localMongoConnectionString;
                            if (MatchesSecret(secretUri, serviceBusSecretUri))
                                return localServiceBusConnectionString;
                            if (MatchesSecret(secretUri, jobsServiceBusSecretUri))
                                return localJobsServiceBusConnectionString;
                            var segments = secretUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                            if (segments.Length < 2 || !string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("The Azure Key Vault reference URI is invalid.");
                            var vault = new Uri($"{secretUri.Scheme}://{secretUri.Host}");
                            var secretClient = new SecretClient(vault, credential);
                            var response = await secretClient.GetSecretAsync(
                                segments[1], segments.Length > 2 ? segments[2] : null).ConfigureAwait(false);
                            return response.Value.Value;
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

    public static string ResolveConnectionString(IConfiguration configuration)
        => ResolveSecret(
            configuration,
            ConnectionStringKey,
            ConnectionStringFileKey,
            "Azure App Configuration bootstrap secret");

    public static string ResolveSecret(
        IConfiguration configuration,
        string valueKey,
        string fileKey,
        string description)
    {
        var configured = configuration[valueKey]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var path = configuration[fileKey]?.Trim();
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{description} file '{path}' could not be read.", exception);
        }
    }

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
