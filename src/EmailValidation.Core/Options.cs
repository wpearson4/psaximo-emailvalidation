namespace EmailValidation.Core;

public sealed class EmailValidationOptions
{
    public DomainIntelligenceOptions DomainIntelligence { get; set; } = new();
    public DnsSecurityOptions DnsSecurity { get; set; } = new();
    public AuthenticationIntelligenceOptions AuthenticationIntelligence { get; set; } = new();
    public DisposableEmailOptions DisposableEmail { get; set; } = new();
    public RiskIntelligenceOptions RiskIntelligence { get; set; } = new();
    public SmtpOptions Smtp { get; set; } = new();
    public SmtpResponseIntelligenceOptions SmtpResponseIntelligence { get; set; } = new();
    public SchedulingOptions Scheduling { get; set; } = new();
    public ProbeSenderSourceOptions ProbeSenderSource { get; set; } = new();
    public ProbeSenderRotationOptions ProbeSenderRotation { get; set; } = new();
    public OutboundIdentityOptions OutboundIdentities { get; set; } = new();
    public SmtpReputationProtectionOptions SmtpReputationProtection { get; set; } = new();
    public CatchAllOptions CatchAll { get; set; } = new();
    public DnsOptions Dns { get; set; } = new();
    public IntelligenceOptions Intelligence { get; set; } = new();
    public PersistenceOptions Persistence { get; set; } = new();
    public ResultReuseOptions ResultReuse { get; set; } = new();
    public ValidationPolicyOptions Policy { get; set; } = new();
    public RevalidationOptions Revalidation { get; set; } = new();
    public ValidationJobsOptions Jobs { get; set; } = new();
    public EmailColumnDetectionOptions ColumnDetection { get; set; } = new();
    public EmailValidationProjectionOptions Projection { get; set; } = new();
    public ClassificationModelOptions ClassificationModel { get; set; } = new();
}

public sealed class ClassificationModelOptions
{
    public ModelRolloutMode Mode { get; set; } = ModelRolloutMode.Disabled;
    public string ArtifactPath { get; set; } = string.Empty;
    public string ArtifactChecksum { get; set; } = string.Empty;
    public double LikelyValidThreshold { get; set; } = 0.8;
    public double LikelyInvalidThreshold { get; set; } = 0.2;
    public double AbstentionLowerBound { get; set; } = 0.4;
    public double AbstentionUpperBound { get; set; } = 0.6;
    public double MinimumVerificationReliability { get; set; } = 0.25;
    public double MaximumMissingFeatureFraction { get; set; } = 0.35;
    public string DecisionPolicyVersion { get; set; } = "classification-decision-policy-v1";
}

public sealed class EmailValidationProjectionOptions
{
    public bool Enabled { get; set; }
    public string Environment { get; set; } = "dev";
    public ProjectionOutboxOptions Outbox { get; set; } = new();
    public ProjectionServiceBusOptions ServiceBus { get; set; } = new();
    public ProjectionElasticsearchOptions Elasticsearch { get; set; } = new();
    public ProjectionPrivacyOptions Privacy { get; set; } = new();
    public ProjectionReconciliationOptions Reconciliation { get; set; } = new();
}

public sealed class ProjectionOutboxOptions
{
    public string CollectionName { get; set; } = "EmailValidationProjectionOutbox";
    public string CheckpointCollectionName { get; set; } = "EmailValidationProjectionCheckpoints";
    public int BatchSize { get; set; } = 100;
    public int DispatchIntervalSeconds { get; set; } = 5;
    public int LockDurationSeconds { get; set; } = 60;
    public int PublishedRetentionDays { get; set; } = 7;
    public int MaximumPublishAttempts { get; set; } = 20;
}

public sealed class ProjectionServiceBusOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string TopicName { get; set; } = "email-validation-observations";
    public string SubscriptionName { get; set; } = "email-validation-elasticsearch-projector";
    public bool ProvisionEntities { get; set; }
    public int MaxDeliveryCount { get; set; } = 10;
    public int PrefetchCount { get; set; } = 200;
    public int MaxAutoLockRenewalMinutes { get; set; } = 10;
}

public sealed class ProjectionElasticsearchOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DataStreamName { get; set; } = "email-validation-observations-dev-v1";
    public int MaximumBatchSize { get; set; } = 500;
    public int MaximumBatchBytes { get; set; } = 5 * 1024 * 1024;
    public int ReceiveWaitSeconds { get; set; } = 5;
    public int RetryLimit { get; set; } = 5;
    public int RetryBackoffMilliseconds { get; set; } = 1_000;
}

public sealed class ProjectionPrivacyOptions
{
    public bool IncludeRecipientDomain { get; set; } = true;
    public bool IncludeRawEmail { get; set; }
    public string EmailHashKey { get; set; } = string.Empty;
    public string EmailHashKeyVersion { get; set; } = "v1";
}

public sealed class ProjectionReconciliationOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 10;
    public int OverlapMinutes { get; set; } = 15;
    public int BatchSize { get; set; } = 500;
    public int MaximumEventsPerRun { get; set; } = 5_000;
}

public sealed class SmtpReputationProtectionOptions
{
    public bool Enabled { get; set; } = true;
    public SmtpReputationProtectionMode Mode { get; set; } = SmtpReputationProtectionMode.Observe;
    public string NetworkBlock { get; set; } = "64.182.22.160/28";
    public int WindowMinutes { get; set; } = 60;
    public int FailureFallbackMinutes { get; set; } = 5;
    public string PolicyVersion { get; set; } = "2026.08.1";
    public SmtpMailboxReputationOptions Mailbox { get; set; } = new();
    public SmtpCircuitBreakerOptions CircuitBreaker { get; set; } = new();
    public SmtpUnknownRecipientPressureOptions UnknownRecipientPressure { get; set; } = new();
    public SmtpPolicyBlockPressureOptions PolicyBlockPressure { get; set; } = new();
}

public sealed class SmtpMailboxReputationOptions
{
    public int MinimumMinutesBetweenLiveProbes { get; set; } = 60;
    public int MaximumLiveProbesPer24Hours { get; set; } = 2;
}

public sealed class SmtpCircuitBreakerOptions
{
    public bool Enabled { get; set; } = true;
    public int MinimumObservationsBeforeEvaluation { get; set; } = 20;
    public int CooldownMinutes { get; set; } = 30;
    public int HalfOpenMaximumProbes { get; set; } = 2;
    public int RecoverySuccessesRequired { get; set; } = 3;
    public int ProviderIdentityPolicyBlockCount { get; set; } = 3;
    public int ProviderAffectedIdentityCount { get; set; } = 2;
    public int NetworkAffectedProviderCount { get; set; } = 2;
    public int NetworkAffectedIdentityCount { get; set; } = 3;
}

public sealed class SmtpUnknownRecipientPressureOptions
{
    public bool Enabled { get; set; } = true;
    public int MinimumRcptObservations { get; set; } = 20;
    public double OpenRatio { get; set; } = 0.50;
}

public sealed class SmtpPolicyBlockPressureOptions
{
    public bool Enabled { get; set; } = true;
    public int MinimumObservations { get; set; } = 10;
    public double DegradedRatio { get; set; } = 0.15;
    public double OpenRatio { get; set; } = 0.30;
}

public sealed class SmtpResponseIntelligenceOptions
{
    public SmtpResponseIntelligenceMode Mode { get; set; } = SmtpResponseIntelligenceMode.Shadow;
    public string ClassificationVersion { get; set; } = "smtp-response-rules-1.0.0";
    public string DecisionPolicyVersion { get; set; } = "smtp-response-policy-1.0.0";
    public int MaximumResponseCharacters { get; set; } = 4096;
    public int RegexTimeoutMilliseconds { get; set; } = 100;
}

public sealed class EmailColumnDetectionOptions
{
    public int MaximumNonEmptySamplesPerColumn { get; set; } = 100;
    public int MaximumRowsInspected { get; set; } = 10_000;
    public int MinimumNonEmptySamples { get; set; } = 3;
    public int MinimumEmailLikeSamples { get; set; } = 2;
    public double MinimumEmailRatio { get; set; } = 0.60;
    public double HeaderSupportedMinimumEmailRatio { get; set; } = 0.55;
    public double InvalidEmailShapeWeight { get; set; } = 0.50;
    public double HeaderConfidenceBoost { get; set; } = 0.05;
}

public sealed class ValidationJobsOptions
{
    public bool Enabled { get; set; }
    public string QueueName { get; set; } = "email-validation-jobs";
    public string ServiceBusConnectionString { get; set; } = string.Empty;
    public bool ProvisionQueue { get; set; }
    public int MaximumItemsPerJob { get; set; } = 100_000;
    public int ChunkSize { get; set; } = 100;
    public int MaximumConcurrency { get; set; } = 8;
    public int MaximumResultPageSize { get; set; } = 1_000;
    public int MaxConcurrentCalls { get; set; } = 2;
    public int MaxAutoLockRenewalMinutes { get; set; } = 30;
    public int MaxDeliveryCount { get; set; } = 10;
    public string JobCollection { get; set; } = "EmailValidationJobs";
    public string ItemCollection { get; set; } = "EmailValidationJobItems";
    public bool EnableSmtpByDefault { get; set; } = true;
}

public sealed class DomainIntelligenceOptions
{
    public bool Enabled { get; set; } = true;
    public int MemoryCacheMinutes { get; set; } = 30;
    public int PersistentFreshnessHours { get; set; } = 24;
    public int MinimumFreshnessMinutes { get; set; } = 5;
    public int MaximumFreshnessHours { get; set; } = 24;
    public int MaximumConcurrentAnalyses { get; set; } = 16;
    public string PolicyVersion { get; set; } = "2.0.0";
}

public sealed class DnsSecurityOptions
{
    public bool Enabled { get; set; } = true;
}

public sealed class AuthenticationIntelligenceOptions
{
    public bool SpfEnabled { get; set; } = true;
    public bool DmarcEnabled { get; set; } = true;
    public bool DkimObservationEnabled { get; set; } = true;
}

public sealed class DisposableEmailOptions
{
    public bool Enabled { get; set; } = true;
    public int CacheMinutes { get; set; } = 60;
    public string DatasetVersion { get; set; } = "configured-1";
}

public sealed class RiskIntelligenceOptions
{
    public bool RoleDetectionEnabled { get; set; } = true;
    public bool SpamTrapDetectionEnabled { get; set; } = true;
    public string RoleRuleVersion { get; set; } = "1.0.0";
}

public sealed class RevalidationOptions
{
    public bool Enabled { get; set; }
    public int DefaultMaxAttempts { get; set; } = 2;
    public int OutboxDispatchIntervalSeconds { get; set; } = 30;
    public int OutboxBatchSize { get; set; } = 100;
    public int OutboxLeaseSeconds { get; set; } = 60;
    public ServiceBusRevalidationOptions ServiceBus { get; set; } = new();
}

public sealed class ServiceBusRevalidationOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string QueueName { get; set; } = "email-validation-retry";
    public bool ProvisionQueue { get; set; }
    public bool EnableDuplicateDetection { get; set; }
    public int DuplicateDetectionMinutes { get; set; } = 10;
    public int MaxDeliveryCount { get; set; } = 10;
    public int MaxConcurrentCalls { get; set; } = 4;
    public int PrefetchCount { get; set; }
    public int MaxAutoLockRenewalMinutes { get; set; } = 10;
}

public sealed class PersistenceOptions
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "Json";
    public string StoragePath { get; set; } = "data/email-validation-intelligence";
    public int MaximumObservationsPerDomain { get; set; } = 200;
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string DomainCollection { get; set; } = "EmailValidationDomainIntelligence";
    public string MailboxCollection { get; set; } = "EmailValidationMailboxIntelligence";
    public string LifecycleCollection { get; set; } = "EmailValidationLifecycle";
    public string CommercialResourceCollection { get; set; } = "EmailValidationCommercialResources";
    public string OutboundIdentityHealthCollection { get; set; } = "EmailValidationOutboundIdentityHealth";
    public string SmtpReputationStateCollection { get; set; } = "EmailValidationSmtpReputationState";
    public string FeatureSnapshotCollection { get; set; } = "EmailValidationFeatureSnapshots";
    public string OutcomeObservationCollection { get; set; } = "EmailValidationOutcomeObservations";
}

public sealed class ResultReuseOptions
{
    public bool Enabled { get; set; } = true;
    public bool MemoryCacheEnabled { get; set; } = true;
    public bool SingleFlightEnabled { get; set; } = true;
    public int MemoryCacheSizeLimit { get; set; } = 10_000;
    public int StrongPositiveMinutes { get; set; } = 60;
    public int StrongNegativeMinutes { get; set; } = 240;
    public int RiskyMinutes { get; set; } = 30;
    public int TransientMinutes { get; set; } = 2;
}

public sealed class ValidationPolicyOptions
{
    public string ValidationEngineVersion { get; set; } = "1.1.0";
    public string ClassificationPolicyVersion { get; set; } = "2.2.0";
    public string ConfidenceModelVersion { get; set; } = "3.1.0";
    public string ProviderStrategyVersion { get; set; } = "1.1.0";

    public ValidationPolicyVersions ToVersions() => new(
        ValidationEngineVersion,
        ClassificationPolicyVersion,
        ConfidenceModelVersion,
        ProviderStrategyVersion);
}

public sealed class SchedulingOptions
{
    /// <summary>Zero preserves the legacy Smtp.GlobalConcurrency setting.</summary>
    public int GlobalConcurrency { get; set; }
    /// <summary>Zero preserves the corresponding legacy Smtp setting.</summary>
    public int PerDomainConcurrency { get; set; }
    public int PerProviderConcurrency { get; set; }
    /// <summary>Negative preserves the legacy Smtp.DelayBetweenDomainRequestsMilliseconds setting.</summary>
    public int DomainMinIntervalMilliseconds { get; set; } = -1;
    public int DomainIntervalJitterMilliseconds { get; set; } = 250;
    public int ProviderMinIntervalMilliseconds { get; set; }
    public int MaxActiveDomains { get; set; } = 1000;
    public int TemporaryFailureBackoffMilliseconds { get; set; } = 5000;
    public int MaximumBackoffMilliseconds { get; set; } = 120000;
    public ProviderPolicyOptions? DefaultProviderPolicy { get; set; }
    public Dictionary<string, ProviderPolicyOptions> ProviderPolicies { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderPolicyOptions
{
    /// <summary>Optional compatibility override for the existing domain-level constraint.</summary>
    public int? PerDomainConcurrency { get; set; }
    public int PerProviderConcurrency { get; set; } = 2;
    public int DelayMilliseconds { get; set; } = 1000;
    public int PolicyBlockCooldownMinutes { get; set; } = 15;
    public int MaxRetries { get; set; } = 1;

    /// <summary>Legacy configuration alias. When supplied, it takes precedence over DelayMilliseconds.</summary>
    public int? MinIntervalMilliseconds { get; set; }
}

public sealed class SmtpOptions
{
    public bool Enabled { get; set; }
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public int CommandTimeoutSeconds { get; set; } = 10;
    public int RetryCount { get; set; } = 1;
    public int GlobalConcurrency { get; set; } = 2;
    public int PerDomainConcurrency { get; set; } = 1;
    public int PerProviderConcurrency { get; set; } = 2;
    public int DelayBetweenDomainRequestsMilliseconds { get; set; } = 500;
    public int GreylistingRetryDelayMilliseconds { get; set; } = 2000;
    public int MaxMxAttempts { get; set; } = 3;
    public int MaxSmtpSessionsPerAddress { get; set; } = 8;
    public int ProbeSenderHealthCacheMinutes { get; set; } = 60;
}

public sealed class OutboundIdentityOptions
{
    public bool Enabled { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
    public string AllowedCidr { get; set; } = string.Empty;
    public string GatewayAddress { get; set; } = string.Empty;
    public bool RequireAddressToBeBound { get; set; } = true;
    public bool RequireForwardConfirmedReverseDns { get; set; } = true;
    public OutboundIdentityDnsReadinessOptions DnsReadiness { get; set; } = new();
    public string SelectionAlgorithm { get; set; } = "RendezvousHash";
    public string SelectionAlgorithmVersion { get; set; } = "v1";
    public int PolicyBlockCooldownMinutes { get; set; } = 60;
    public int QuarantineFailureThreshold { get; set; } = 3;
    public int QuarantineMinutes { get; set; } = 240;
    public Dictionary<string, string> ProviderGroups { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> IdentityGroups { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<OutboundIdentityConfiguration> Identities { get; set; } = [];
}

public sealed class OutboundIdentityConfiguration
{
    public string IdentityId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string InterfaceName { get; set; } = string.Empty;
    public string ExpectedPtrHostName { get; set; } = string.Empty;
    public string EhloHostName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class OutboundIdentityDnsReadinessOptions
{
    public bool Enabled { get; set; } = true;
    public OutboundIdentityDnsReadinessMode Mode { get; set; } = OutboundIdentityDnsReadinessMode.Observe;
    public ForwardConfirmedReverseDnsValidationMode ValidationMode { get; set; } =
        ForwardConfirmedReverseDnsValidationMode.StrictOneToOne;
    public bool RequireExpectedPtr { get; set; } = true;
    public bool RequireForwardConfirmation { get; set; } = true;
    public bool RequireEhloMatch { get; set; } = true;
    public bool AllowLastKnownGoodOnTransientFailure { get; set; } = true;
    public int MinimumFreshnessMinutes { get; set; } = 5;
    public int MaximumFreshnessHours { get; set; } = 24;
    public int FallbackFreshnessMinutes { get; set; } = 60;
    public int NegativeCacheMinutes { get; set; } = 5;
    public int TransientFailureRetrySeconds { get; set; } = 60;
    public int LastKnownGoodGraceMinutes { get; set; } = 15;
    public int RefreshAheadMinutes { get; set; } = 5;
    public int MaximumConcurrentLookups { get; set; } = 4;
    public int RefreshJitterPercent { get; set; } = 10;
    public string ValidationPolicyVersion { get; set; } = "2026.08.1";
}

public sealed class ProbeSenderSourceOptions
{
    public string Provider { get; set; } = "Elasticsearch";
    public string Endpoint { get; set; } = "http://localhost:9200";
    public string Index { get; set; } = string.Empty;
    public string EmailField { get; set; } = "business_email";
    public int QueryLimit { get; set; } = 500;
    public int RefreshThreshold { get; set; } = 100;
    public int RefreshIntervalSeconds { get; set; } = 300;
    public int StaleAfterMinutes { get; set; } = 30;
    public int RecentlyUsedLimit { get; set; } = 1_000;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Populated from the Query configuration object during application startup.</summary>
    public string QueryJson { get; set; } = string.Empty;
}

public sealed class ProbeSenderRotationOptions
{
    public int MaxValidationsPerSender { get; set; } = 50;
    public int MaxActiveMinutes { get; set; } = 15;
    public int MaxSenderAttemptsPerValidation { get; set; } = 2;
    public int SenderCooldownSeconds { get; set; } = 300;
    public int JitterPercent { get; set; } = 20;
    public int MinimumSuccessRateSampleSize { get; set; } = 10;
    public double MinimumMailFromSuccessRate { get; set; } = 0.8;
    public int SenderAffinityMinutes { get; set; } = 60;
    public bool RotateOnSenderSpecificFailure { get; set; } = true;
    public int SenderCompatibilityMinutes { get; set; } = 60;
}

public sealed class CatchAllOptions
{
    public bool Enabled { get; set; } = true;
    public int ProbeCount { get; set; } = 1;
    public int MinimumAcceptedProbes { get; set; } = 2;
    public int MaxProbeCount { get; set; } = 3;
    public int CacheMinutes { get; set; } = 1440;
    public double MinimumReusableConfidence { get; set; } = 0.90;
}

public sealed class DnsOptions
{
    public int TimeoutSeconds { get; set; } = 5;
    public int RetryCount { get; set; } = 1;
    public int CacheMinutes { get; set; } = 60;
}

public sealed class IntelligenceOptions
{
    public string[] RoleAccounts { get; set; } =
        ["info", "support", "admin", "sales", "billing", "contact", "help", "office", "marketing",
            "abuse", "postmaster", "webmaster", "security", "hr", "careers"];
    public string[] DisposableDomains { get; set; } =
        ["10minutemail.com", "guerrillamail.com", "mailinator.com", "temp-mail.org", "yopmail.com", "trashmail.com"];
    public string[] FreeEmailDomains { get; set; } =
        ["gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "msn.com", "yahoo.com", "aol.com", "icloud.com", "me.com", "proton.me", "protonmail.com", "fastmail.com"];
    public Dictionary<string, string> CommonDomainTypos { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmal.com"] = "gmail.com",
        ["gmial.com"] = "gmail.com",
        ["hotmial.com"] = "hotmail.com",
        ["hotnail.com"] = "hotmail.com",
        ["yaho.com"] = "yahoo.com",
        ["outlok.com"] = "outlook.com"
    };
    public string[] ToxicDomains { get; set; } = [];
    public string[] KnownSpamTrapAddresses { get; set; } = [];
    public string[] AbuseRiskAddresses { get; set; } = [];
    public Dictionary<string, string> SuppressedAddresses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MxForwardingSuffixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
