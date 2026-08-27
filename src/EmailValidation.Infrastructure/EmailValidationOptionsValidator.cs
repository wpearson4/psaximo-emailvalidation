using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using EmailValidation.Application;
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
        var revalidation = options.Revalidation;
        var domainIntelligence = options.DomainIntelligence;
        var jobs = options.Jobs;
        var columnDetection = options.ColumnDetection;
        var smtpResponseIntelligence = options.SmtpResponseIntelligence;
        var outboundIdentities = options.OutboundIdentities;
        var reputation = options.SmtpReputationProtection;
        var failures = new List<string>();
        ValidateOutboundIdentities(outboundIdentities, failures);
        ValidateSmtpReputation(reputation, outboundIdentities, failures);
        if (string.IsNullOrWhiteSpace(smtpResponseIntelligence.ClassificationVersion) ||
            string.IsNullOrWhiteSpace(smtpResponseIntelligence.DecisionPolicyVersion))
            failures.Add("SMTP response intelligence classification and decision policy versions are required.");
        if (smtpResponseIntelligence.MaximumResponseCharacters is < 256 or > 16_384)
            failures.Add("EmailValidation:SmtpResponseIntelligence:MaximumResponseCharacters must be between 256 and 16384.");
        if (smtpResponseIntelligence.RegexTimeoutMilliseconds is < 10 or > 1_000)
            failures.Add("EmailValidation:SmtpResponseIntelligence:RegexTimeoutMilliseconds must be between 10 and 1000.");
        if (failures.Count == 0)
        {
            try
            {
                _ = new SmtpResponseRuleRegistry(Options.Create(options));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                failures.Add($"SMTP response intelligence rules are invalid: {exception.Message}");
            }
        }
        if (columnDetection.MaximumNonEmptySamplesPerColumn < 1 ||
            columnDetection.MaximumRowsInspected < columnDetection.MaximumNonEmptySamplesPerColumn ||
            columnDetection.MinimumNonEmptySamples < 1 ||
            columnDetection.MinimumEmailLikeSamples < 1)
            failures.Add("EmailValidation:ColumnDetection sample and inspection limits are invalid.");
        if (columnDetection.MinimumEmailRatio is <= 0 or > 1 ||
            columnDetection.HeaderSupportedMinimumEmailRatio is <= 0 or > 1 ||
            columnDetection.HeaderSupportedMinimumEmailRatio > columnDetection.MinimumEmailRatio ||
            columnDetection.InvalidEmailShapeWeight is < 0 or > 1 ||
            columnDetection.HeaderConfidenceBoost is < 0 or > 1)
            failures.Add("EmailValidation:ColumnDetection confidence thresholds must be between zero and one.");
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
            if (string.IsNullOrWhiteSpace(persistence.DomainCollection) || string.IsNullOrWhiteSpace(persistence.MailboxCollection) ||
                string.IsNullOrWhiteSpace(persistence.LifecycleCollection) || string.IsNullOrWhiteSpace(persistence.CommercialResourceCollection) ||
                string.IsNullOrWhiteSpace(persistence.OutboundIdentityHealthCollection) ||
                string.IsNullOrWhiteSpace(persistence.SmtpReputationStateCollection))
                failures.Add("EmailValidation MongoDB collection names are required.");
            if (string.Equals(persistence.DomainCollection, persistence.MailboxCollection, StringComparison.OrdinalIgnoreCase))
                failures.Add("EmailValidation MongoDB domain and mailbox collection names must be different.");
            if (new[] { persistence.DomainCollection, persistence.MailboxCollection, persistence.LifecycleCollection,
                    persistence.CommercialResourceCollection, persistence.OutboundIdentityHealthCollection,
                    persistence.SmtpReputationStateCollection }
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 6)
                failures.Add("EmailValidation MongoDB domain, mailbox, lifecycle, commercial resource, outbound identity health, and SMTP reputation collections must be different.");
        }
        if (revalidation.Enabled)
        {
            if (!persistence.Enabled || !string.Equals(persistence.Provider, "MongoDB", StringComparison.OrdinalIgnoreCase))
                failures.Add("EmailValidation revalidation requires MongoDB persistence for its durable lifecycle and outbox.");
            if (revalidation.DefaultMaxAttempts < 1)
                failures.Add("EmailValidation:Revalidation:DefaultMaxAttempts must be at least 1.");
            if (revalidation.OutboxDispatchIntervalSeconds <= 0 || revalidation.OutboxBatchSize <= 0 ||
                revalidation.OutboxLeaseSeconds <= 0)
                failures.Add("EmailValidation revalidation outbox intervals, batch size, and lease must be positive.");
            if (string.IsNullOrWhiteSpace(revalidation.ServiceBus.ConnectionString))
                failures.Add("EmailValidation:Revalidation:ServiceBus:ConnectionString is required when revalidation is enabled.");
            if (string.IsNullOrWhiteSpace(revalidation.ServiceBus.QueueName))
                failures.Add("EmailValidation:Revalidation:ServiceBus:QueueName is required when revalidation is enabled.");
            if (revalidation.ServiceBus.MaxDeliveryCount < 1 || revalidation.ServiceBus.MaxConcurrentCalls < 1 ||
                revalidation.ServiceBus.PrefetchCount < 0 || revalidation.ServiceBus.MaxAutoLockRenewalMinutes < 1)
                failures.Add("EmailValidation Service Bus delivery, concurrency, prefetch, and lock-renewal settings are invalid.");
            if (revalidation.ServiceBus.EnableDuplicateDetection && revalidation.ServiceBus.DuplicateDetectionMinutes < 1)
                failures.Add("EmailValidation duplicate detection history must be at least one minute when enabled.");
        }
        if (jobs.Enabled)
        {
            if (!persistence.Enabled || !string.Equals(persistence.Provider, "MongoDB", StringComparison.OrdinalIgnoreCase))
                failures.Add("EmailValidation jobs require MongoDB persistence.");
            if (string.IsNullOrWhiteSpace(jobs.ServiceBusConnectionString) || string.IsNullOrWhiteSpace(jobs.QueueName))
                failures.Add("EmailValidation job Service Bus connection string and queue name are required.");
            if (jobs.MaximumItemsPerJob < 1 || jobs.ChunkSize < 1 || jobs.MaximumConcurrency < 1 ||
                jobs.MaximumResultPageSize < 1 || jobs.MaxConcurrentCalls < 1 || jobs.MaxAutoLockRenewalMinutes < 1 ||
                jobs.MaxDeliveryCount < 1)
                failures.Add("EmailValidation job limits and concurrency settings must be positive.");
            if (string.IsNullOrWhiteSpace(jobs.JobCollection) || string.IsNullOrWhiteSpace(jobs.ItemCollection) ||
                string.Equals(jobs.JobCollection, jobs.ItemCollection, StringComparison.OrdinalIgnoreCase))
                failures.Add("EmailValidation job collection names are required and must be different.");
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
        if (domainIntelligence.MemoryCacheMinutes < 0 || domainIntelligence.PersistentFreshnessHours < 0 ||
            domainIntelligence.MinimumFreshnessMinutes < 0 || domainIntelligence.MaximumFreshnessHours < 0)
            failures.Add("EmailValidation:DomainIntelligence freshness windows cannot be negative.");
        if (domainIntelligence.MaximumConcurrentAnalyses < 1)
            failures.Add("EmailValidation:DomainIntelligence:MaximumConcurrentAnalyses must be at least 1.");
        if (string.IsNullOrWhiteSpace(domainIntelligence.PolicyVersion))
            failures.Add("EmailValidation:DomainIntelligence:PolicyVersion is required.");
        if (options.DisposableEmail.CacheMinutes < 0)
            failures.Add("EmailValidation:DisposableEmail:CacheMinutes cannot be negative.");
        if (string.IsNullOrWhiteSpace(options.DisposableEmail.DatasetVersion))
            failures.Add("EmailValidation:DisposableEmail:DatasetVersion is required.");
        if (string.IsNullOrWhiteSpace(options.RiskIntelligence.RoleRuleVersion))
            failures.Add("EmailValidation:RiskIntelligence:RoleRuleVersion is required.");
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

    private static void ValidateOutboundIdentities(
        OutboundIdentityOptions options,
        List<string> failures)
    {
        if (!options.Enabled) return;
        if (string.IsNullOrWhiteSpace(options.InterfaceName))
            failures.Add("EmailValidation:OutboundIdentities:InterfaceName is required.");
        if (!options.RequireAddressToBeBound)
            failures.Add("EmailValidation:OutboundIdentities:RequireAddressToBeBound must remain true.");
        if (!string.Equals(options.SelectionAlgorithm, "RendezvousHash", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(options.SelectionAlgorithmVersion))
            failures.Add("EmailValidation:OutboundIdentities must use a versioned RendezvousHash selection algorithm.");
        if (!TryParseIpv4Cidr(options.AllowedCidr, out var network, out var broadcast))
            failures.Add("EmailValidation:OutboundIdentities:AllowedCidr must be a valid IPv4 CIDR.");
        if (!IPAddress.TryParse(options.GatewayAddress, out var gateway) ||
            gateway.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            failures.Add("EmailValidation:OutboundIdentities:GatewayAddress must be a valid IPv4 address.");
        else
        {
            var gatewayValue = BinaryPrimitives.ReadUInt32BigEndian(gateway.GetAddressBytes());
            if (gatewayValue < network || gatewayValue > broadcast)
                failures.Add("EmailValidation:OutboundIdentities:GatewayAddress must belong to the approved CIDR.");
        }
        if (options.PolicyBlockCooldownMinutes < 1 || options.QuarantineFailureThreshold < 1 ||
            options.QuarantineMinutes < 1)
            failures.Add("EmailValidation outbound identity cooldown and quarantine policy values must be positive.");
        var readiness = options.DnsReadiness;
        if (readiness.Enabled && readiness.Mode == OutboundIdentityDnsReadinessMode.Enforced &&
            !options.RequireForwardConfirmedReverseDns)
            failures.Add("Enforced outbound identity DNS readiness requires RequireForwardConfirmedReverseDns=true.");
        if (string.IsNullOrWhiteSpace(readiness.ValidationPolicyVersion))
            failures.Add("EmailValidation:OutboundIdentities:DnsReadiness:ValidationPolicyVersion is required.");
        if (readiness.MinimumFreshnessMinutes < 1 || readiness.MaximumFreshnessHours < 1 ||
            readiness.FallbackFreshnessMinutes < 1 || readiness.NegativeCacheMinutes < 1 ||
            readiness.TransientFailureRetrySeconds < 15 || readiness.LastKnownGoodGraceMinutes < 0 ||
            readiness.RefreshAheadMinutes < 0 || readiness.MaximumConcurrentLookups is < 1 or > 32 ||
            readiness.RefreshJitterPercent is < 0 or > 50)
            failures.Add("EmailValidation outbound identity DNS readiness cache, refresh, grace, and concurrency values are invalid.");
        if (options.Identities.Count == 0 || options.IdentityGroups.Count == 0 || options.ProviderGroups.Count == 0)
            failures.Add("Enabled outbound identity configuration requires identities, identity groups, and provider mappings.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var expectedPtrNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ehloNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in options.Identities)
        {
            if (string.IsNullOrWhiteSpace(identity.IdentityId) || !ids.Add(identity.IdentityId))
                failures.Add("EmailValidation outbound identity IDs must be present and unique.");
            if (!IPAddress.TryParse(identity.Address, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                failures.Add($"Outbound identity '{identity.IdentityId}' has an invalid IPv4 address.");
                continue;
            }
            var numeric = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
            if (!addresses.Add(address.ToString()))
                failures.Add($"Outbound identity address '{address}' is duplicated.");
            if (numeric < network || numeric > broadcast)
                failures.Add($"Outbound identity address '{address}' is outside the approved CIDR.");
            if (numeric == network || numeric == broadcast || address.Equals(gateway))
                failures.Add($"Outbound identity address '{address}' is reserved and cannot be assigned.");
            if (!string.IsNullOrWhiteSpace(identity.InterfaceName) &&
                !string.Equals(identity.InterfaceName, options.InterfaceName, StringComparison.Ordinal))
                failures.Add($"Outbound identity '{identity.IdentityId}' uses an unapproved interface.");
            if (!OutboundIdentityHostName.TryNormalize(identity.ExpectedPtrHostName, out var expectedPtr) ||
                !expectedPtrNames.Add(expectedPtr))
                failures.Add("EmailValidation outbound identity expected PTR hostnames must be valid FQDNs and unique.");
            if (!OutboundIdentityHostName.TryNormalize(identity.EhloHostName, out var ehlo) ||
                !ehloNames.Add(ehlo))
                failures.Add("EmailValidation outbound identity EHLO hostnames must be valid FQDNs and unique.");
            if (readiness.RequireEhloMatch && !string.Equals(expectedPtr, ehlo, StringComparison.OrdinalIgnoreCase))
                failures.Add($"Outbound identity '{identity.IdentityId}' EHLO hostname must match its expected PTR hostname.");
        }

        foreach (var mapping in options.ProviderGroups)
        {
            if (!Enum.TryParse<MailProvider>(mapping.Key, true, out _) ||
                string.IsNullOrWhiteSpace(mapping.Value) || !options.IdentityGroups.ContainsKey(mapping.Value))
                failures.Add($"Outbound provider group mapping '{mapping.Key}' is invalid.");
        }
        foreach (var group in options.IdentityGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Key) || group.Value.Length == 0)
                failures.Add("Outbound identity groups must have a name and at least one identity.");
            if (group.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Value.Length)
                failures.Add($"Outbound identity group '{group.Key}' contains duplicate identity references.");
            foreach (var identityId in group.Value)
                if (!ids.Contains(identityId))
                    failures.Add($"Outbound identity group '{group.Key}' references unknown identity '{identityId}'.");
        }
    }

    private static void ValidateSmtpReputation(
        SmtpReputationProtectionOptions options,
        OutboundIdentityOptions outboundIdentities,
        List<string> failures)
    {
        if (!options.Enabled) return;
        if (!TryParseIpv4Cidr(options.NetworkBlock, out _, out _))
            failures.Add("EmailValidation:SmtpReputationProtection:NetworkBlock must be a valid IPv4 CIDR.");
        if (outboundIdentities.Enabled && !string.Equals(
                options.NetworkBlock, outboundIdentities.AllowedCidr, StringComparison.OrdinalIgnoreCase))
            failures.Add("SMTP reputation network block must match the configured outbound identity CIDR.");
        if (string.IsNullOrWhiteSpace(options.PolicyVersion))
            failures.Add("EmailValidation:SmtpReputationProtection:PolicyVersion is required.");
        if (options.WindowMinutes < 1 || options.FailureFallbackMinutes < 1 ||
            options.Mailbox.MinimumMinutesBetweenLiveProbes < 0 ||
            options.Mailbox.MaximumLiveProbesPer24Hours < 1)
            failures.Add("SMTP reputation window, fallback, and mailbox budget values are invalid.");
        var circuit = options.CircuitBreaker;
        if (circuit.MinimumObservationsBeforeEvaluation < 1 || circuit.CooldownMinutes < 1 ||
            circuit.HalfOpenMaximumProbes < 1 || circuit.RecoverySuccessesRequired < 1 ||
            circuit.ProviderIdentityPolicyBlockCount < 1 || circuit.ProviderAffectedIdentityCount < 2 ||
            circuit.NetworkAffectedProviderCount < 2 || circuit.NetworkAffectedIdentityCount < 2)
            failures.Add("SMTP reputation circuit-breaker thresholds are invalid.");
        if (options.UnknownRecipientPressure.MinimumRcptObservations < 1 ||
            options.UnknownRecipientPressure.OpenRatio is <= 0 or > 1 ||
            options.PolicyBlockPressure.MinimumObservations < 1 ||
            options.PolicyBlockPressure.DegradedRatio is < 0 or > 1 ||
            options.PolicyBlockPressure.OpenRatio is <= 0 or > 1 ||
            options.PolicyBlockPressure.DegradedRatio >= options.PolicyBlockPressure.OpenRatio)
            failures.Add("SMTP reputation pressure thresholds and ratios are invalid.");
    }

    private static bool TryParseIpv4Cidr(string value, out uint network, out uint broadcast)
    {
        network = 0;
        broadcast = 0;
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32) return false;
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        network = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes()) & mask;
        broadcast = network | ~mask;
        return true;
    }
}
