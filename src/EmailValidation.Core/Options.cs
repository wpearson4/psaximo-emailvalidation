namespace EmailValidation.Core;

public sealed class EmailValidationOptions
{
    public SmtpOptions Smtp { get; set; } = new();
    public CatchAllOptions CatchAll { get; set; } = new();
    public DnsOptions Dns { get; set; } = new();
    public IntelligenceOptions Intelligence { get; set; } = new();
}

public sealed class SmtpOptions
{
    public bool Enabled { get; set; }
    /// <summary>Legacy single-sender setting. Used when ProbeSenders is empty.</summary>
    public string ProbeSender { get; set; } = string.Empty;
    public List<ProbeSenderOptions> ProbeSenders { get; set; } = [];
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public int CommandTimeoutSeconds { get; set; } = 10;
    public int RetryCount { get; set; } = 1;
    public int GlobalConcurrency { get; set; } = 2;
    public int PerDomainConcurrency { get; set; } = 1;
    public int PerProviderConcurrency { get; set; } = 2;
    public int DelayBetweenDomainRequestsMilliseconds { get; set; } = 500;
    public int GreylistingRetryDelayMilliseconds { get; set; } = 2000;
    public int MaxMxAttempts { get; set; } = 3;
    public int MaxSenderAttemptsPerValidation { get; set; } = 2;
    public int MaxSmtpSessionsPerAddress { get; set; } = 8;
    public int SenderCooldownSeconds { get; set; } = 300;
    public int ProbeSenderHealthCacheMinutes { get; set; } = 60;
}

public sealed class ProbeSenderOptions
{
    public string Address { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
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
        ["outlok.com"] = "outlook.com"
    };
    public string[] ToxicDomains { get; set; } = [];
    public string[] KnownSpamTrapAddresses { get; set; } = [];
    public string[] AbuseRiskAddresses { get; set; } = [];
    public Dictionary<string, string> SuppressedAddresses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MxForwardingSuffixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
