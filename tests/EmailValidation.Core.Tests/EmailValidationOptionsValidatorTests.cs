using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace EmailValidation.Core.Tests;

public sealed class EmailValidationOptionsValidatorTests
{
    [Fact]
    public void InvalidSenderSourceConfiguration_ReturnsActionableFailures()
    {
        var options = new EmailValidationOptions
        {
            ProbeSenderSource = new ProbeSenderSourceOptions
            {
                Endpoint = "not-a-uri",
                Index = "",
                EmailField = "",
                QueryLimit = 6_000,
                RefreshThreshold = 6_000,
                QueryJson = ""
            }
        };

        var result = new EmailValidationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Endpoint", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("Index", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("EmailField", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("QueryLimit", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("Query is required", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultOperationalConfiguration_IsAccepted()
    {
        var options = new EmailValidationOptions
        {
            ProbeSenderSource = new ProbeSenderSourceOptions
            {
                Index = "authorized-senders",
                QueryJson = "{\"match_all\":{}}"
            }
        };

        var result = new EmailValidationOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void AppConfigurationKeys_BindMongoPersistenceWithoutAzureConnectivity()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EmailValidation:Persistence:Enabled"] = "true",
            ["EmailValidation:Persistence:Provider"] = "MongoDB",
            ["EmailValidation:Persistence:ConnectionString"] = "mongodb://configured-by-key-vault.invalid/psaximo",
            ["EmailValidation:Persistence:DatabaseName"] = "psaximo",
            ["EmailValidation:Persistence:DomainCollection"] = "EmailValidationDomainIntelligence",
            ["EmailValidation:Persistence:MailboxCollection"] = "EmailValidationMailboxIntelligence"
        }).Build();

        var options = configuration.GetSection("EmailValidation").Get<EmailValidationOptions>();

        Assert.NotNull(options);
        Assert.Equal("MongoDB", options.Persistence.Provider);
        Assert.Equal("psaximo", options.Persistence.DatabaseName);
        Assert.Equal("EmailValidationDomainIntelligence", options.Persistence.DomainCollection);
        Assert.Equal("EmailValidationMailboxIntelligence", options.Persistence.MailboxCollection);
    }

    [Fact]
    public void MongoConfiguration_MissingSecretReferenceFailsClearly()
    {
        var options = new EmailValidationOptions
        {
            Persistence = new PersistenceOptions
            {
                Enabled = true,
                Provider = "MongoDB",
                DatabaseName = "psaximo"
            }
        };

        var result = new EmailValidationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("App Configuration/Key Vault", StringComparison.Ordinal));
    }

    [Fact]
    public void AzureBootstrap_UsesConfiguredEndpointAndEnvironmentLabel()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [EmailValidationAzureConfiguration.EndpointKey] = "https://example.azconfig.io",
            [EmailValidationAzureConfiguration.ConnectionStringKey] =
                "Endpoint=https://example.azconfig.io;Id=read-only;Secret=local-bootstrap"
        }).Build();

        Assert.Equal("https://example.azconfig.io", EmailValidationAzureConfiguration.ResolveEndpoint(configuration));
        Assert.Equal(
            "Endpoint=https://example.azconfig.io;Id=read-only;Secret=local-bootstrap",
            EmailValidationAzureConfiguration.ResolveConnectionString(configuration));
        Assert.Equal("Development", EmailValidationAzureConfiguration.ResolveLabel(configuration, "Development"));
        Assert.True(EmailValidationAzureConfiguration.MatchesSecret(
            new Uri("https://example.vault.azure.net/secrets/Mongo/version"),
            "https://example.vault.azure.net/secrets/Mongo"));
        Assert.False(EmailValidationAzureConfiguration.MatchesSecret(
            new Uri("https://example.vault.azure.net/secrets/Other"),
            "https://example.vault.azure.net/secrets/Mongo"));
    }
}
