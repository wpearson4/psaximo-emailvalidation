namespace EmailValidation.Core;

public interface IEmailValidator
{
    Task<EmailValidationResult> ValidateAsync(string email, EmailValidationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The live validation pipeline before persistence/reuse orchestration.</summary>
public interface IEmailValidationExecutor
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

public interface IMailRoutingAnalyzer
{
    Task<MailRoutingIntelligence> AnalyzeAsync(
        string domain,
        CancellationToken cancellationToken = default);
}

public interface IDnsSecurityAnalyzer
{
    Task<DnsSecurityIntelligence> AnalyzeAsync(
        string domain,
        CancellationToken cancellationToken = default);
}

public interface IEmailAuthenticationAnalyzer
{
    Task<EmailAuthenticationIntelligence> AnalyzeAsync(
        string domain,
        CancellationToken cancellationToken = default);
}

public interface IDisposableEmailDetector
{
    bool IsDisposable(string domain);
}

public interface IDisposableDomainIntelligenceProvider
{
    DisposableDomainResult Evaluate(string domain);
}

public interface IDisposableEmailDomainProvider
{
    ValueTask<DisposableDomainResult> GetAsync(
        string domain,
        CancellationToken cancellationToken = default);
}

public interface IRoleAccountDetector
{
    bool IsRoleAccount(string localPart);

    RoleAddressDetectionResult Detect(NormalizedEmailAddress email) => IsRoleAccount(email.LocalPart)
        ? new RoleAddressDetectionResult(true, RoleAddressType.Other, email.LocalPart, "legacy")
        : RoleAddressDetectionResult.NotRole;
}

public interface IRoleAddressDetector
{
    RoleAddressDetectionResult Detect(NormalizedEmailAddress email);
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

public interface ISpamTrapRiskProvider
{
    Task<SpamTrapRiskAssessment> EvaluateAsync(
        EmailRiskContext context,
        CancellationToken cancellationToken = default);
}

public interface ICatchAllDeliverabilityPredictor
{
    Task<CatchAllPrediction?> PredictAsync(
        CatchAllPredictionContext context,
        CancellationToken cancellationToken = default);
}

public interface ISmtpProviderDetector
{
    ProviderDetectionResult Detect(SmtpSessionEvidence evidence);
}

public enum DomainIntelligenceSource { MemoryCache, PersistentStore, LiveAnalysis, JoinedInFlight }

public sealed record DomainIntelligenceAcquisition(
    DomainIntelligence Intelligence,
    DomainIntelligenceSource Source,
    int CatchAllProbes,
    long AnalysisDurationMs,
    ValidationPlan Plan);

public interface IDomainIntelligenceService
{
    Task<DomainIntelligence> GetAsync(
        string domain,
        CancellationToken cancellationToken = default);

    Task<DomainIntelligenceAcquisition> AcquireAsync(
        string domain,
        bool allowCatchAllProbe,
        CancellationToken cancellationToken = default);
}

public sealed record DomainIntelligenceReuseDecision(
    bool CanReuse,
    bool CatchAllCompatible,
    string Reason);

public interface IDomainIntelligenceFreshnessPolicy
{
    DomainIntelligenceReuseDecision Evaluate(
        DomainIntelligence existing,
        DomainIntelligence? current,
        DateTimeOffset now);
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

public interface IDomainPacingJitter
{
    TimeSpan Apply(TimeSpan interval, int maximumJitterMilliseconds);
}

public interface IProbeSenderAffinityStore
{
    ProbeSenderAffinity? GetAffinity(string recipientDomain);
    void SetAffinity(string recipientDomain, string sender);
    void Remove(string recipientDomain);
    void RemoveSender(string sender);
    void MarkIncompatible(string recipientDomain, string sender);
    IReadOnlySet<string> GetIncompatibleSenders(string recipientDomain);
    int Count { get; }
    ProbeSenderAffinitySnapshot GetSnapshot();
}

public interface IDomainBackoffPolicy
{
    DomainBackoffDecision Evaluate(
        MailProvider provider,
        SmtpResponseCategory category,
        int consecutiveTemporaryFailures,
        DateTimeOffset now);
}

public interface IProviderPolicyResolver
{
    ProviderPolicy Resolve(MailProvider provider);
}

public interface IDomainValidationScheduler
{
    Task<IReadOnlyList<ValidationWorkResult>> ScheduleAsync(
        IReadOnlyList<ValidationWorkItem> items,
        CancellationToken cancellationToken = default);
    async IAsyncEnumerable<ValidationWorkResult> ScheduleStreamingAsync(
        IReadOnlyList<ValidationWorkItem> items,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var results = await ScheduleAsync(items, cancellationToken);
        foreach (var result in results) yield return result;
    }
    DomainSchedulerSnapshot GetSnapshot();
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
    ProviderThrottleAvailability GetAvailability(SmtpThrottleContext context) => ProviderThrottleAvailability.Available;
    ValueTask<ISmtpThrottleLease> AcquireAsync(SmtpThrottleContext context, CancellationToken cancellationToken = default);
    void RecordOutcome(SmtpThrottleContext context, SmtpProbeResult result) { }
    void RecordProviderRetry(MailProvider provider, bool exhausted) { }
    SmtpSchedulingSnapshot GetSnapshot() => new(0, 0, 0, 0, 0, 0, 0, 0);
}

public interface ISmtpThrottleLease : IAsyncDisposable
{
    bool Acquired { get; }
    DateTimeOffset? RetryAfter { get; }
    string? Reason { get; }
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

    Task<DomainIntelligence?> GetAsync(string domain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryGet(domain, out var data);
        return Task.FromResult(data);
    }

    Task StoreAsync(DomainIntelligence data, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Store(data, lifetime);
        return Task.CompletedTask;
    }
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

    Task RecordAsync(DeliveryOutcomeRecord outcome, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This recorder does not support prediction snapshots.");
}

public interface IDeliveryOutcomeStore : IDeliveryOutcomeRecorder
{
    Task<IReadOnlyList<DeliveryOutcomeRecord>> QueryAsync(
        CalibrationQuery query,
        CancellationToken cancellationToken = default);
}

public interface IValidationIntelligenceStore
{
    Task<DomainIntelligence?> GetDomainAsync(string domain, CancellationToken cancellationToken = default);
    Task<MailboxIntelligence?> GetMailboxAsync(string normalizedEmail, CancellationToken cancellationToken = default);
    Task SaveDomainAsync(DomainIntelligence intelligence, CancellationToken cancellationToken = default);
    Task SaveMailboxAsync(MailboxIntelligence intelligence, CancellationToken cancellationToken = default);
}

public interface IEmailValidationPersistenceInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IValidationPersistenceMetrics
{
    void RecordValidationRequest();
    void RecordRead(string recordType, bool found, TimeSpan elapsed);
    void RecordWrite(string recordType, bool succeeded);
    void RecordMailboxReuse(bool liveSmtpAvoided);
    void RecordMemoryCacheLookup(bool hit);
    void RecordReuseMiss(ValidationReuseRejectionReason reason);
    void RecordLiveValidation();
    void RecordSingleFlight(bool joinedExistingOperation);
    void RecordCacheWrite();
    void RecordCacheInvalidation();
    void RecordStaleMailboxRefresh();
    void RecordDomainReuse();
    void RecordCatchAllDiscovered();
    void RecordCatchAllReuse(bool catchAllProbeAvoided, bool mailboxProbeAvoided);
    void RecordCatchAllRefreshed(bool expired, bool classificationChanged);
    void RecordSmtpUtf8(bool required, bool supported);
    ValidationPersistenceSnapshot GetSnapshot();
}

public interface IValidationPlanBuilder
{
    ValidationPlan Build(
        DomainIntelligence? intelligence,
        bool smtpEnabled,
        bool domainIntelligenceReused,
        ValidationPolicyVersions currentPolicy,
        DateTimeOffset now);
}

public interface IValidationResultCache
{
    Task<EmailValidationResult?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);
    Task SetAsync(
        string key,
        EmailValidationResult result,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public interface IValidationSingleFlight
{
    Task<EmailValidationResult> ExecuteAsync(
        string key,
        Func<CancellationToken, Task<EmailValidationResult>> factory,
        CancellationToken cancellationToken = default);
    Task<ValidationSingleFlightResult> ExecuteWithStatusAsync(
        string key,
        Func<CancellationToken, Task<EmailValidationResult>> factory,
        CancellationToken cancellationToken = default);
}

public interface IValidationResultReusePolicy
{
    ValidationReuseDecision Evaluate(
        MailboxIntelligence intelligence,
        DomainIntelligence? currentDomain,
        EmailValidationRequest request,
        ValidationPolicyVersions currentPolicy,
        DateTimeOffset now);

    bool CanReuse(
        MailboxIntelligence intelligence,
        DomainIntelligence? currentDomain,
        EmailValidationRequest request,
        ValidationPolicyVersions currentPolicy,
        DateTimeOffset now) => Evaluate(intelligence, currentDomain, request, currentPolicy, now).CanReuse;
}

public interface IConfidenceCalibrationService
{
    Task<CalibrationResult> EvaluateAsync(
        CalibrationQuery query,
        CancellationToken cancellationToken = default);
}

public interface IRiskDataSource
{
    Task<RiskDataResult> LookupAsync(
        EmailRiskContext context,
        CancellationToken cancellationToken = default);
}

public interface IEmailRiskIntelligence
{
    Task<EmailRiskResult> EvaluateAsync(
        EmailRiskContext context,
        CancellationToken cancellationToken = default);
}

public interface IGlobalSuppressionStore
{
    Task<SuppressionEntry?> GetAsync(string normalizedEmail, CancellationToken cancellationToken = default);
    Task AddAsync(SuppressionEntry entry, CancellationToken cancellationToken = default);
}

public interface IValidationQualityMetrics
{
    void Record(EmailValidationResult result);
    ValidationQualitySnapshot GetSnapshot();
}
