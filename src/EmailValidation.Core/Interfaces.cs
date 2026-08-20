namespace EmailValidation.Core;

public interface IEmailValidator
{
    Task<EmailValidationResult> ValidateAsync(string email, EmailValidationRequest request, CancellationToken cancellationToken = default);
}

public interface IEmailNormalizer
{
    NormalizationResult Normalize(string input);
}

public interface IDnsMailResolver
{
    Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default);
}

public interface IDisposableEmailDetector
{
    bool IsDisposable(string domain);
}

public interface IDisposableDomainIntelligenceProvider
{
    DisposableDomainResult Evaluate(string domain);
}

public interface IRoleAccountDetector
{
    bool IsRoleAccount(string localPart);
}

public interface IEmailTypoDetector
{
    TypoDetectionResult Detect(string localPart, string domain);
}

public interface IFreeEmailProviderDetector
{
    bool IsFreeProvider(string domain);
}

public interface IToxicDomainDetector
{
    Task<ToxicDomainResult> EvaluateAsync(string domain, CancellationToken cancellationToken = default);
}

public interface ISpamTrapRiskDetector
{
    Task<SpamTrapRiskResult> EvaluateAsync(string email, CancellationToken cancellationToken = default);
}

public interface IAbuseRiskProvider
{
    Task<AbuseRiskResult> EvaluateAsync(string email, CancellationToken cancellationToken = default);
}

public interface ISuppressionIntelligenceProvider
{
    Task<SuppressionResult> EvaluateAsync(string email, CancellationToken cancellationToken = default);
}

public interface IMxForwardDetector
{
    MxForwardResult Evaluate(string domain, IReadOnlyList<MxRecord> mxRecords);
}

public interface IDomainAgeProvider
{
    Task<DomainAgeResult> GetAgeAsync(string domain, CancellationToken cancellationToken = default);
}

public interface IMailInfrastructureInspector
{
    Task<MailInfrastructureResult> InspectAsync(
        string domain,
        DnsLookupResult dns,
        CancellationToken cancellationToken = default);
}

public interface IEmailIdentityIntelligenceProvider
{
    Task<EmailIdentityResult> EvaluateAsync(string email, CancellationToken cancellationToken = default);
}

public interface IEmailIntelligenceEvaluator
{
    Task<EmailAddressIntelligence> EvaluateAsync(
        string email,
        string localPart,
        string domain,
        CancellationToken cancellationToken = default);
}

public interface IDomainIntelligenceEvaluator
{
    Task<SupplementalDomainIntelligence> EvaluateAsync(
        string domain,
        DnsLookupResult dns,
        CancellationToken cancellationToken = default);
}

public interface IResultEvaluator
{
    ResultEvaluation Evaluate(
        EmailValidationStatus status,
        EmailValidationChecks checks,
        DomainIntelligence domain,
        EmailAddressIntelligence address,
        ProviderValidationResult providerValidation,
        SmtpEvidence? smtpEvidence,
        HistoricalSignalSummary history);
}

public interface IMailProviderDetector
{
    MailProvider Detect(IReadOnlyList<MxRecord> records);
    ProviderDetectionResult DetectWithConfidence(IReadOnlyList<MxRecord> records);
}

public interface ISmtpMailboxProbe
{
    Task<SmtpProbeResult> ProbeAsync(string mxHost, string recipient, CancellationToken cancellationToken = default);
    Task<SmtpProbeResult> ProbeAsync(
        string mxHost,
        string recipient,
        MailProvider provider,
        CancellationToken cancellationToken = default) => ProbeAsync(mxHost, recipient, cancellationToken);
}

public interface IProbeSenderHealthChecker
{
    Task<ProbeSenderHealth> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IProbeSenderPool
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<ProbeSenderSelection?> GetSenderAsync(
        ProbeSenderContext context,
        CancellationToken cancellationToken = default);
    Task RecordOutcomeAsync(ProbeSenderOutcome outcome, CancellationToken cancellationToken = default);
    ProbeSenderPoolSnapshot GetSnapshot();
}

public interface IProbeSenderSource
{
    Task<IReadOnlyCollection<ProbeSenderCandidate>> GetCandidatesAsync(
        int limit,
        CancellationToken cancellationToken = default);
}

public interface IProbeSenderRotationPolicy
{
    ProbeSenderRotationDecision Evaluate(
        ProbeSenderRuntimeStatistics sender,
        int validationThreshold,
        DateTimeOffset now,
        bool alternateAvailable);
}

public interface IProbeSenderJitter
{
    int Apply(int target, int percent);
}

public interface ISmtpSessionBudget
{
    IDisposable Begin(int maximumSessions);
    bool TryConsume();
}

public interface ISmtpResponseClassifier
{
    SmtpEvidence Classify(
        SmtpCommand command,
        int? responseCode,
        string? response,
        TimeSpan elapsed,
        MailProvider provider,
        string mxHost,
        int attempt = 1);
}

/// <summary>
/// Process-local prototype throttle. The context deliberately carries dimensions that a
/// future distributed implementation can enforce by IP, domain, MX provider, or tenant.
/// </summary>
public interface ISmtpProbeThrottle
{
    ValueTask<IAsyncDisposable> AcquireAsync(SmtpThrottleContext context, CancellationToken cancellationToken = default);
}

public interface ICatchAllDetector
{
    Task<CatchAllDetectionResult> DetectAsync(
        string domain,
        string mxHost,
        MailProvider provider,
        CancellationToken cancellationToken = default);
}

public interface IDomainValidationCache
{
    bool TryGet(string domain, out DomainIntelligence? data);
    void Store(DomainIntelligence data, TimeSpan lifetime);
    int Count { get; }
}

public interface IEmailClassificationEngine
{
    ClassificationResult Classify(EmailValidationChecks checks, DnsStatus dnsStatus);
    ClassificationResult Classify(EmailClassificationEvidence evidence);
}

public interface IMailProviderStrategy
{
    bool CanHandle(ProviderDetectionResult provider);
    Task<ProviderValidationResult> EvaluateAsync(
        ProviderValidationContext context,
        CancellationToken cancellationToken = default);
}

public interface IMailProviderStrategyResolver
{
    IMailProviderStrategy Resolve(ProviderDetectionResult provider);
}

public interface IValidationObservationStore
{
    Task<IReadOnlyList<ValidationObservation>> GetDomainObservationsAsync(
        string domain,
        CancellationToken cancellationToken = default);
    Task RecordAsync(ValidationObservation observation, CancellationToken cancellationToken = default);
}

public interface IHistoricalSignalAggregator
{
    HistoricalSignalSummary Aggregate(IReadOnlyList<ValidationObservation> observations);
}

public interface IDeliveryOutcomeRecorder
{
    Task RecordOutcomeAsync(DeliveryOutcome outcome, CancellationToken cancellationToken = default);
}
