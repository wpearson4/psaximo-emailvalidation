namespace EmailValidation.Core;

public sealed class EmailValidationOptions
{
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
}

public sealed class ResultReuseOptions
{
    public bool Enabled { get; set; } = true;
    public int StrongPositiveMinutes { get; set; } = 60;
    public int StrongNegativeMinutes { get; set; } = 240;
    public int RiskyMinutes { get; set; } = 30;
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
        ["info", "support", "admin", "sales", "billing", "contact", "help", "office", "marketing", "webmaster"];
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
