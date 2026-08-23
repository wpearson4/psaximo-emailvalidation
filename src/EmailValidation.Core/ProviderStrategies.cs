namespace EmailValidation.Core;

public sealed class MailProviderStrategyResolver(IEnumerable<IMailProviderStrategy> strategies) : IMailProviderStrategyResolver
{
    private readonly IReadOnlyList<IMailProviderStrategy> _strategies = strategies.ToArray();

    public IMailProviderStrategy Resolve(ProviderDetectionResult provider) =>
        _strategies.FirstOrDefault(strategy => strategy.CanHandle(provider))
        ?? throw new InvalidOperationException("A GenericSmtpStrategy must be registered as the provider fallback.");
}

public abstract class MailProviderStrategyBase(MailProvider handledProvider) : IMailProviderStrategy
{
    public virtual bool CanHandle(ProviderDetectionResult provider) => provider.Provider == handledProvider;

    public Task<ProviderValidationResult> EvaluateAsync(
        ProviderValidationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Evaluate(context));
    }

    protected abstract ProviderValidationResult Evaluate(ProviderValidationContext context);

    protected static ProviderValidationResult Interpret(
        ProviderValidationContext context,
        SmtpResponseCategory acceptedCategory,
        AcceptanceStrength acceptedStrength,
        string acceptedExplanation)
    {
        var category = ResolveCategory(context.MailboxProbe);
        var reasons = new List<ReasonCode> { ReasonCode.ProviderDetected };
        AddStageReasons(context.MailboxProbe, reasons);
        var explanation = acceptedExplanation;
        var strength = AcceptanceStrength.None;

        if (category == SmtpResponseCategory.Accepted)
        {
            category = acceptedCategory;
            strength = acceptedStrength;
            reasons.Add(ReasonCode.MailboxAccepted);
            if (acceptedCategory == SmtpResponseCategory.GatewayAccepted)
            {
                reasons.Add(ReasonCode.GatewayAccepted);
                reasons.Add(ReasonCode.MailboxAcceptanceAmbiguous);
            }
        }
        else
        {
            explanation = category switch
            {
                SmtpResponseCategory.RecipientRejected => "The recipient was explicitly rejected.",
                SmtpResponseCategory.MailboxFull => "The recipient mailbox exists but is currently full.",
                SmtpResponseCategory.Greylisted => "The provider temporarily greylisted verification.",
                SmtpResponseCategory.RateLimited => "The provider rate-limited verification.",
                SmtpResponseCategory.VerificationBlocked => "The provider blocked recipient verification.",
                SmtpResponseCategory.SmtpUtf8Unsupported => "The destination does not advertise SMTPUTF8 required by the recipient.",
                SmtpResponseCategory.LocalCooldown => "The SMTP probe was deferred because a local MX-scoped cooldown is active.",
                SmtpResponseCategory.Timeout => "The SMTP operation timed out.",
                _ => "The provider did not return conclusive mailbox evidence."
            };
            AddCategoryReason(category, reasons);
        }

        if (context.History.LikelyCatchAllCount > 0)
            reasons.Add(ReasonCode.HistoricalCatchAllBehavior);
        if (context.History.VerificationBlockedCount > 1)
            reasons.Add(ReasonCode.HistoricalVerificationBlocked);

        return new(
            context.Domain.Provider.Provider,
            context.Domain.Provider.Confidence,
            category,
            strength,
            reasons.Distinct().ToArray(),
            explanation,
            context.Domain.Provider.GatewayProvider,
            context.Domain.Provider.MailboxProvider,
            category == SmtpResponseCategory.Accepted ? 0.70 : category == SmtpResponseCategory.MailboxFull ? 0.85 : 0,
            category == SmtpResponseCategory.Accepted
                ? VerificationReliabilityLevel.Medium
                : category == SmtpResponseCategory.MailboxFull
                    ? VerificationReliabilityLevel.High
                    : VerificationReliabilityLevel.Unknown);
    }

    internal static SmtpResponseCategory ToCategory(SmtpMailboxStatus status) => status switch
    {
        SmtpMailboxStatus.NotAttempted => SmtpResponseCategory.NotAttempted,
        SmtpMailboxStatus.Accepted => SmtpResponseCategory.Accepted,
        SmtpMailboxStatus.Rejected => SmtpResponseCategory.RecipientRejected,
        SmtpMailboxStatus.MailboxFull => SmtpResponseCategory.MailboxFull,
        SmtpMailboxStatus.TemporaryFailure => SmtpResponseCategory.TemporaryFailure,
        SmtpMailboxStatus.ConnectionFailure => SmtpResponseCategory.ConnectionRejected,
        SmtpMailboxStatus.Timeout => SmtpResponseCategory.Timeout,
        SmtpMailboxStatus.Blocked => SmtpResponseCategory.VerificationBlocked,
        _ => SmtpResponseCategory.Unknown
    };

    internal static SmtpResponseCategory ResolveCategory(SmtpProbeResult probe)
    {
        var category = probe.Evidence?.Category ?? ToCategory(probe.Status);
        if (category != SmtpResponseCategory.RecipientRejected) return category;

        // Session-aware results must prove that MAIL FROM succeeded and that the
        // rejection actually came from RCPT TO. Legacy callers without session
        // evidence retain their previous contract, but can never manufacture this
        // condition from a pre-RCPT command stamped into SmtpEvidence.
        if (probe.SessionEvidence is not null)
            return probe.SessionEvidence.HasStrongRecipientRejection
                ? category
                : SmtpResponseCategory.VerificationBlocked;
        return probe.Evidence?.Command is null or SmtpCommand.RcptTo
            ? category
            : SmtpResponseCategory.VerificationBlocked;
    }

    internal static void AddStageReasons(SmtpProbeResult probe, ICollection<ReasonCode> reasons)
    {
        var evidence = probe.Evidence;
        if (evidence is null) return;
        if (evidence.Command == SmtpCommand.MailFrom && evidence.ResponseCode is >= 500 and < 600)
        {
            reasons.Add(ReasonCode.SenderIdentityRejected);
            if (evidence.SanitizedResponse?.Contains("domain", StringComparison.OrdinalIgnoreCase) == true)
                reasons.Add(ReasonCode.SenderDomainRejected);
        }
        if (evidence.TextClassification == SmtpResponseTextClassification.PolicyRejection)
            reasons.Add(ReasonCode.PolicyBlock);
        if (evidence.TextClassification == SmtpResponseTextClassification.RelayDenied)
            reasons.Add(ReasonCode.RelayDenied);
        if (evidence.SanitizedResponse?.Contains("auth", StringComparison.OrdinalIgnoreCase) == true)
            reasons.Add(ReasonCode.AuthenticationRequired);
    }

    private static void AddCategoryReason(SmtpResponseCategory category, List<ReasonCode> reasons)
    {
        switch (category)
        {
            case SmtpResponseCategory.RecipientRejected: reasons.Add(ReasonCode.MailboxRejected); break;
            case SmtpResponseCategory.MailboxFull: reasons.Add(ReasonCode.MailboxFull); break;
            case SmtpResponseCategory.Greylisted: reasons.Add(ReasonCode.Greylisted); break;
            case SmtpResponseCategory.RateLimited: reasons.Add(ReasonCode.RateLimited); break;
            case SmtpResponseCategory.VerificationBlocked:
            case SmtpResponseCategory.MailboxUnknown:
                reasons.Add(ReasonCode.ProviderVerificationBlocked);
                break;
            case SmtpResponseCategory.SmtpUtf8Unsupported:
                reasons.Add(ReasonCode.SmtpUtf8Unsupported);
                break;
            case SmtpResponseCategory.LocalCooldown:
                reasons.Add(ReasonCode.LocalCooldown);
                reasons.Add(ReasonCode.RetryRecommended);
                break;
            case SmtpResponseCategory.Timeout: reasons.Add(ReasonCode.SmtpTimeout); break;
            case SmtpResponseCategory.TemporaryFailure: reasons.Add(ReasonCode.TemporarySmtpFailure); break;
            case SmtpResponseCategory.ConnectionRejected: reasons.Add(ReasonCode.SmtpConnectionFailure); break;
        }
    }
}

public sealed class Microsoft365Strategy() : MailProviderStrategyBase(MailProvider.Microsoft365)
{
    public override bool CanHandle(ProviderDetectionResult provider) =>
        provider.Provider is MailProvider.Microsoft365 or MailProvider.MicrosoftConsumer;

    protected override ProviderValidationResult Evaluate(ProviderValidationContext context)
    {
        var category = ResolveCategory(context.MailboxProbe);
        var catchAll = context.Domain.CatchAll.Status;
        var reasons = new List<ReasonCode> { ReasonCode.ProviderDetected };
        AddStageReasons(context.MailboxProbe, reasons);
        var effectiveCategory = category;
        var strength = AcceptanceStrength.None;
        var mailboxProvider = MailProvider.Unknown;
        var reliability = 0d;
        string explanation;

        if (category == SmtpResponseCategory.Accepted &&
            catchAll is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll)
        {
            effectiveCategory = SmtpResponseCategory.Accepted;
            strength = AcceptanceStrength.High;
            mailboxProvider = context.Domain.Provider.Provider;
            reliability = Math.Max(0.88, context.Domain.CatchAll.Confidence);
            reasons.Add(ReasonCode.MailboxAccepted);
            explanation = "Exchange Online Protection accepted the target while randomized recipients were rejected.";
        }
        else if (category == SmtpResponseCategory.Accepted)
        {
            effectiveCategory = SmtpResponseCategory.GatewayAccepted;
            strength = AcceptanceStrength.Low;
            reliability = catchAll == CatchAllStatus.LikelyCatchAll ? 0.20 : 0.30;
            reasons.Add(ReasonCode.MailboxAccepted);
            reasons.Add(ReasonCode.GatewayAccepted);
            reasons.Add(ReasonCode.MailboxAcceptanceAmbiguous);
            explanation = catchAll == CatchAllStatus.LikelyCatchAll
                ? "Exchange Online Protection accepted both the target and randomized recipients; mailbox existence cannot be established."
                : "Exchange Online Protection accepted the target at the gateway without reliable recipient differentiation.";
        }
        else
        {
            explanation = category switch
            {
                SmtpResponseCategory.RecipientRejected =>
                    "Exchange Online Protection explicitly rejected the recipient.",
                SmtpResponseCategory.MailboxFull => "Exchange Online Protection reports that the recipient mailbox is full.",
                SmtpResponseCategory.Greylisted => "Exchange Online Protection temporarily greylisted verification.",
                SmtpResponseCategory.RateLimited => "Exchange Online Protection rate-limited verification.",
                SmtpResponseCategory.VerificationBlocked => "Exchange Online Protection blocked or obscured recipient verification.",
                SmtpResponseCategory.LocalCooldown => "The Exchange Online Protection probe was deferred by a local MX-scoped cooldown.",
                SmtpResponseCategory.TemporaryFailure => "Exchange Online Protection returned a temporary failure.",
                _ => "Exchange Online Protection did not return conclusive mailbox evidence."
            };
            AddMicrosoftCategoryReason(category, reasons);
            if (category == SmtpResponseCategory.RecipientRejected)
            {
                reliability = catchAll is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll ? 0.96 : 0.88;
                reasons.Add(ReasonCode.MicrosoftRecipientRejected);
            }
            else if (category == SmtpResponseCategory.MailboxFull)
            {
                reliability = 0.85;
            }
        }

        // Once a tenant has enough observations, its behavior is more informative
        // than a provider-wide assumption. Current recipient evidence still retains
        // weight so a stale history cannot fully override the active SMTP exchange.
        if (context.History.ObservationCount >= 4)
            reliability = (reliability * 0.40) + (context.History.VerificationReliability * 0.60);

        reliability = Math.Clamp(reliability, 0, 1);
        var reliabilityLevel = reliability switch
        {
            >= 0.80 => VerificationReliabilityLevel.High,
            >= 0.50 => VerificationReliabilityLevel.Medium,
            > 0 => VerificationReliabilityLevel.Low,
            _ => VerificationReliabilityLevel.Unknown
        };

        return new ProviderValidationResult(
            context.Domain.Provider.Provider,
            context.Domain.Provider.Confidence,
            effectiveCategory,
            strength,
            reasons.Distinct().ToArray(),
            explanation,
            GatewayProvider.MicrosoftExchangeOnlineProtection,
            mailboxProvider,
            Math.Round(reliability, 2),
            reliabilityLevel);
    }

    private static void AddMicrosoftCategoryReason(SmtpResponseCategory category, List<ReasonCode> reasons)
    {
        switch (category)
        {
            case SmtpResponseCategory.RecipientRejected: reasons.Add(ReasonCode.MailboxRejected); break;
            case SmtpResponseCategory.MailboxFull: reasons.Add(ReasonCode.MailboxFull); break;
            case SmtpResponseCategory.Greylisted: reasons.Add(ReasonCode.Greylisted); break;
            case SmtpResponseCategory.RateLimited: reasons.Add(ReasonCode.RateLimited); break;
            case SmtpResponseCategory.VerificationBlocked:
            case SmtpResponseCategory.MailboxUnknown:
                reasons.Add(ReasonCode.ProviderVerificationBlocked);
                break;
            case SmtpResponseCategory.SmtpUtf8Unsupported:
                reasons.Add(ReasonCode.SmtpUtf8Unsupported);
                break;
            case SmtpResponseCategory.LocalCooldown:
                reasons.Add(ReasonCode.LocalCooldown);
                reasons.Add(ReasonCode.RetryRecommended);
                break;
            case SmtpResponseCategory.TemporaryFailure: reasons.Add(ReasonCode.TemporarySmtpFailure); break;
            case SmtpResponseCategory.Timeout: reasons.Add(ReasonCode.SmtpTimeout); break;
            case SmtpResponseCategory.ConnectionRejected: reasons.Add(ReasonCode.SmtpConnectionFailure); break;
        }
    }
}

public sealed class GoogleWorkspaceStrategy() : MailProviderStrategyBase(MailProvider.GoogleWorkspace)
{
    protected override ProviderValidationResult Evaluate(ProviderValidationContext context) => Interpret(
        context,
        SmtpResponseCategory.GatewayAccepted,
        context.Domain.CatchAll.Status is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll
            ? AcceptanceStrength.Medium : AcceptanceStrength.Low,
        "Google Workspace accepted RCPT TO; final mailbox routing remains provider-controlled.");
}

public sealed class ProofpointStrategy() : MailProviderStrategyBase(MailProvider.Proofpoint)
{
    protected override ProviderValidationResult Evaluate(ProviderValidationContext context) => Interpret(
        context,
        SmtpResponseCategory.GatewayAccepted,
        AcceptanceStrength.Low,
        "Proofpoint gateway acceptance does not prove the downstream mailbox exists.");
}

public sealed class MimecastStrategy() : MailProviderStrategyBase(MailProvider.Mimecast)
{
    protected override ProviderValidationResult Evaluate(ProviderValidationContext context) => Interpret(
        context,
        SmtpResponseCategory.GatewayAccepted,
        AcceptanceStrength.Low,
        "Mimecast gateway acceptance does not prove the downstream mailbox exists.");
}

public sealed class GenericSmtpStrategy() : MailProviderStrategyBase(MailProvider.GenericSmtp)
{
    public override bool CanHandle(ProviderDetectionResult provider) =>
        provider.Provider is MailProvider.GenericSmtp or MailProvider.Unknown or
            MailProvider.AmazonSes or MailProvider.Fastmail or MailProvider.Zoho or MailProvider.Yahoo or
            MailProvider.AppleICloud or MailProvider.Comcast or MailProvider.Proton;

    protected override ProviderValidationResult Evaluate(ProviderValidationContext context)
    {
        var catchAllIsNegative = context.Domain.CatchAll.Status is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll;
        if (context.Domain.Provider.Provider is MailProvider.AppleICloud or MailProvider.Proton)
            return Interpret(
                context,
                SmtpResponseCategory.GatewayAccepted,
                AcceptanceStrength.Low,
                "The hosted provider accepted RCPT TO, but mailbox existence remains provider-controlled and requires secondary evidence.");
        if (context.Domain.Provider.Provider == MailProvider.Comcast)
            return Interpret(
                context,
                SmtpResponseCategory.Accepted,
                AcceptanceStrength.Medium,
                "Comcast accepted the recipient; the result is retained as conservative SMTP evidence.");
        return Interpret(
            context,
            SmtpResponseCategory.Accepted,
            catchAllIsNegative ? AcceptanceStrength.High : AcceptanceStrength.Medium,
            catchAllIsNegative
                ? "The recipient was accepted while randomized recipients were rejected."
                : "The recipient was accepted, but catch-all behavior limits certainty.");
    }
}
