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
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
