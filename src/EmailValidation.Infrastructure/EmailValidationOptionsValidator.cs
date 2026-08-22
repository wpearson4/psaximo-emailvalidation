using System.Text.Json;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class EmailValidationOptionsValidator : IValidateOptions<EmailValidationOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailValidationOptions options)
    {
        var source = options.ProbeSenderSource;
        var rotation = options.ProbeSenderRotation;
        var scheduling = options.Scheduling;
        var persistence = options.Persistence;
        var reuse = options.ResultReuse;
        var catchAll = options.CatchAll;
        var policy = options.Policy;
        var failures = new List<string>();
        if (!string.Equals(source.Provider, "Elasticsearch", StringComparison.OrdinalIgnoreCase))
            failures.Add("EmailValidation:ProbeSenderSource:Provider must be Elasticsearch.");
        if (!Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
            failures.Add("EmailValidation:ProbeSenderSource:Endpoint must be an absolute HTTP or HTTPS URI.");
        if (string.IsNullOrWhiteSpace(source.Index))
            failures.Add("EmailValidation:ProbeSenderSource:Index is required.");
        if (string.IsNullOrWhiteSpace(source.EmailField))
            failures.Add("EmailValidation:ProbeSenderSource:EmailField is required.");
        if (source.QueryLimit is < 10 or > 5_000)
            failures.Add("EmailValidation:ProbeSenderSource:QueryLimit must be between 10 and 5000.");
        if (source.RefreshThreshold < 1 || source.RefreshThreshold >= source.QueryLimit)
            failures.Add("EmailValidation:ProbeSenderSource:RefreshThreshold must be positive and less than QueryLimit.");
        if (source.RefreshIntervalSeconds <= 0 || source.StaleAfterMinutes <= 0 || source.RecentlyUsedLimit <= 0)
            failures.Add("Probe sender refresh and recently-used limits must be greater than zero.");
        if (string.IsNullOrWhiteSpace(source.QueryJson))
            failures.Add("EmailValidation:ProbeSenderSource:Query is required.");
        else
        {
            try
            {
                using var query = JsonDocument.Parse(source.QueryJson);
                if (query.RootElement.ValueKind != JsonValueKind.Object)
                    failures.Add("EmailValidation:ProbeSenderSource:Query must be a JSON object.");
            }
            catch (JsonException)
            {
                failures.Add("EmailValidation:ProbeSenderSource:Query is not valid JSON.");
            }
        }
        if (rotation.MaxValidationsPerSender <= 0 || rotation.MaxActiveMinutes <= 0 ||
            rotation.MaxSenderAttemptsPerValidation <= 0 || rotation.SenderCooldownSeconds <= 0)
            failures.Add("Probe sender rotation thresholds and attempt limits must be greater than zero.");
        if (rotation.JitterPercent is < 0 or > 50)
            failures.Add("EmailValidation:ProbeSenderRotation:JitterPercent must be between 0 and 50.");
        if (rotation.MinimumMailFromSuccessRate is < 0 or > 1)
            failures.Add("EmailValidation:ProbeSenderRotation:MinimumMailFromSuccessRate must be between 0 and 1.");
        if (rotation.SenderAffinityMinutes <= 0 || rotation.SenderCompatibilityMinutes <= 0)
            failures.Add("Probe sender affinity and compatibility lifetimes must be greater than zero.");
        if (scheduling.GlobalConcurrency < 0 || scheduling.PerDomainConcurrency < 0 ||
            scheduling.PerProviderConcurrency < 0 || scheduling.MaxActiveDomains <= 0)
            failures.Add("Scheduling concurrency cannot be negative and active-domain limits must be positive (zero concurrency uses the legacy setting).");
        if (scheduling.DomainMinIntervalMilliseconds < -1 ||
            scheduling.DomainIntervalJitterMilliseconds < 0 ||
            scheduling.ProviderMinIntervalMilliseconds < 0)
            failures.Add("Scheduling intervals and jitter must be non-negative (DomainMinIntervalMilliseconds may be -1 for legacy fallback).");
        if (scheduling.TemporaryFailureBackoffMilliseconds <= 0 ||
            scheduling.MaximumBackoffMilliseconds < scheduling.TemporaryFailureBackoffMilliseconds)
            failures.Add("Scheduling backoff must be positive and MaximumBackoffMilliseconds must not be smaller than the initial backoff.");
        if (scheduling.DefaultProviderPolicy is not null)
            ValidateProviderPolicy("EmailValidation:Scheduling:DefaultProviderPolicy", scheduling.DefaultProviderPolicy, failures);
        foreach (var entry in scheduling.ProviderPolicies)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                failures.Add("EmailValidation:Scheduling:ProviderPolicies keys cannot be empty.");
            ValidateProviderPolicy($"EmailValidation:Scheduling:ProviderPolicies:{entry.Key}", entry.Value, failures);
        }
        if (!new[] { "Json", "MongoDB" }.Contains(persistence.Provider, StringComparer.OrdinalIgnoreCase))
            failures.Add("EmailValidation:Persistence:Provider must be Json or MongoDB.");
        if (persistence.Enabled && string.Equals(persistence.Provider, "Json", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(persistence.StoragePath))
            failures.Add("EmailValidation:Persistence:StoragePath is required when JSON persistence is enabled.");
        if (persistence.Enabled && string.Equals(persistence.Provider, "MongoDB", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(persistence.ConnectionString))
                failures.Add("EmailValidation:Persistence:ConnectionString is required for MongoDB and must be resolved through App Configuration/Key Vault.");
            if (string.IsNullOrWhiteSpace(persistence.DatabaseName))
                failures.Add("EmailValidation:Persistence:DatabaseName is required for MongoDB.");
            if (string.IsNullOrWhiteSpace(persistence.DomainCollection) || string.IsNullOrWhiteSpace(persistence.MailboxCollection))
                failures.Add("EmailValidation MongoDB collection names are required.");
            if (string.Equals(persistence.DomainCollection, persistence.MailboxCollection, StringComparison.OrdinalIgnoreCase))
                failures.Add("EmailValidation MongoDB domain and mailbox collection names must be different.");
        }
        if (persistence.MaximumObservationsPerDomain <= 0)
            failures.Add("EmailValidation:Persistence:MaximumObservationsPerDomain must be greater than zero.");
        if (reuse.StrongPositiveMinutes < 0 || reuse.StrongNegativeMinutes < 0 || reuse.RiskyMinutes < 0 ||
            reuse.TransientMinutes < 0)
            failures.Add("EmailValidation:ResultReuse freshness windows cannot be negative.");
        if (reuse.MemoryCacheSizeLimit <= 0)
            failures.Add("EmailValidation:ResultReuse:MemoryCacheSizeLimit must be greater than zero.");
        if (catchAll.MinimumReusableConfidence is < 0 or > 1)
            failures.Add("EmailValidation:CatchAll:MinimumReusableConfidence must be between zero and one.");
        if (catchAll.CacheMinutes < 0)
            failures.Add("EmailValidation:CatchAll:CacheMinutes cannot be negative.");
        if (new[]
            {
                policy.ValidationEngineVersion,
                policy.ClassificationPolicyVersion,
                policy.ConfidenceModelVersion,
                policy.ProviderStrategyVersion
            }.Any(string.IsNullOrWhiteSpace))
            failures.Add("EmailValidation:Policy versions are required.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateProviderPolicy(
        string path,
        ProviderPolicyOptions policy,
        List<string> failures)
    {
        if (policy.PerProviderConcurrency < 1)
            failures.Add($"{path}:PerProviderConcurrency must be at least 1.");
        if (policy.PerDomainConcurrency is < 1)
            failures.Add($"{path}:PerDomainConcurrency must be at least 1 when configured.");
        if (policy.DelayMilliseconds < 0)
            failures.Add($"{path}:DelayMilliseconds cannot be negative.");
        if (policy.MinIntervalMilliseconds is < 0)
            failures.Add($"{path}:MinIntervalMilliseconds cannot be negative.");
        if (policy.PolicyBlockCooldownMinutes < 0)
            failures.Add($"{path}:PolicyBlockCooldownMinutes cannot be negative.");
        if (policy.MaxRetries < 0)
            failures.Add($"{path}:MaxRetries cannot be negative.");
    }
}
