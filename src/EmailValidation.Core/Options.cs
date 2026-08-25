namespace EmailValidation.Core;

public sealed class EmailValidationOptions
{
    public DomainIntelligenceOptions DomainIntelligence { get; set; } = new();
    public DnsSecurityOptions DnsSecurity { get; set; } = new();
    public AuthenticationIntelligenceOptions AuthenticationIntelligence { get; set; } = new();
    public DisposableEmailOptions DisposableEmail { get; set; } = new();
    public RiskIntelligenceOptions RiskIntelligence { get; set; } = new();
    public SmtpOptions Smtp { get; set; } = new();
    public SchedulingOptions Scheduling { get; set; } = new();
    public ProbeSenderSourceOptions ProbeSenderSource { get; set; } = new();
    public ProbeSenderRotationOptions ProbeSenderRotation { get; set; } = new();
    public CatchAllOptions CatchAll { get; set; } = new();
    public DnsOptions Dns { get; set; } = new();
    public IntelligenceOptions Intelligence { get; set; } = new();
    public PersistenceOptions Persistence { get; set; } = new();
    public ResultReuseOptions ResultReuse { get; set; } = new();
    public ValidationPolicyOptions Policy { get; set; } = new();
    public RevalidationOptions Revalidation { get; set; } = new();
    public ValidationJobsOptions Jobs { get; set; } = new();
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
