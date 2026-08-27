using EmailValidation.Core;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace EmailValidation.Application;

/// <summary>
/// Converts response intelligence into conservative mailbox, retry, cooldown, and health effects.
/// Only recipient-specific evidence observed at RCPT TO can invalidate a mailbox.
/// </summary>
public sealed class SmtpResponseDecisionPolicy(IOptions<EmailValidationOptions> options) : ISmtpResponseDecisionPolicy
{
    private readonly string _version = options.Value.SmtpResponseIntelligence.DecisionPolicyVersion;

    public SmtpResponseDecision Decide(SmtpResponseIntelligence classification)
    {
        var reason = classification.Reason;
        var stage = classification.Stage;

        if (reason == SmtpNormalizedReason.CommandAccepted)
            return Decision(SmtpMailboxImpact.None, SmtpRetryDisposition.None, SmtpCooldownScope.None,
                SmtpHealthImpact.Success, false, SmtpResponseCategory.Accepted, "smtp_command_accepted");

        if (stage == SmtpCommand.RcptTo && reason == SmtpNormalizedReason.RecipientAccepted)
            return Decision(SmtpMailboxImpact.Valid, SmtpRetryDisposition.None, SmtpCooldownScope.None,
                SmtpHealthImpact.Success, false, SmtpResponseCategory.Accepted, "recipient_accepted");

        if (stage == SmtpCommand.RcptTo && reason is
            SmtpNormalizedReason.MailboxNotFound or SmtpNormalizedReason.MailboxDisabled or
            SmtpNormalizedReason.MailboxInactive or SmtpNormalizedReason.RecipientRejected)
            return Decision(SmtpMailboxImpact.Invalid, SmtpRetryDisposition.None, SmtpCooldownScope.None,
                SmtpHealthImpact.PermanentFailure, false, SmtpResponseCategory.RecipientRejected,
                "recipient_specific_permanent_rejection");

        if (stage == SmtpCommand.RcptTo && reason == SmtpNormalizedReason.MailboxFull)
            return Decision(SmtpMailboxImpact.Provisional, SmtpRetryDisposition.RetryWithBackoff,
                SmtpCooldownScope.Domain, SmtpHealthImpact.TemporaryFailure, false,
                SmtpResponseCategory.MailboxFull, "mailbox_full_is_provisional");

        if (stage == SmtpCommand.MailFrom && reason is
            SmtpNormalizedReason.SenderInvalid or SmtpNormalizedReason.SenderRejected or
            SmtpNormalizedReason.SenderPolicyRejected)
            return Decision(SmtpMailboxImpact.None, SmtpRetryDisposition.None,
                SmtpCooldownScope.OutboundIdentity,
                classification.ReplyClass == 4 ? SmtpHealthImpact.TemporaryFailure : SmtpHealthImpact.PermanentFailure, true,
                SmtpResponseCategory.VerificationBlocked, "sender_specific_mail_from_rejection");

        if (reason == SmtpNormalizedReason.Greylisted)
            return Decision(SmtpMailboxImpact.Provisional, SmtpRetryDisposition.RetryWithBackoff,
                SmtpCooldownScope.Domain, SmtpHealthImpact.TemporaryFailure, false,
                SmtpResponseCategory.Greylisted, "greylist_backoff");

        if (reason is SmtpNormalizedReason.ProviderRateLimit or SmtpNormalizedReason.ProviderConnectionLimit)
            return Decision(SmtpMailboxImpact.Provisional, SmtpRetryDisposition.Cooldown,
                SmtpCooldownScope.MxProvider, SmtpHealthImpact.Restriction, false,
                SmtpResponseCategory.RateLimited, "provider_scoped_rate_or_connection_limit");

        if (reason is SmtpNormalizedReason.IpPolicyBlock or SmtpNormalizedReason.ReputationBlocked)
            return Decision(SmtpMailboxImpact.Provisional, SmtpRetryDisposition.Cooldown,
                SmtpCooldownScope.SourceIp, SmtpHealthImpact.Restriction, false,
                SmtpResponseCategory.VerificationBlocked, "source_ip_scoped_policy_block");

        if (reason is SmtpNormalizedReason.PolicyBlock or SmtpNormalizedReason.VerificationRefused)
            return Decision(SmtpMailboxImpact.Provisional, SmtpRetryDisposition.Cooldown,
                SmtpCooldownScope.MxProvider, SmtpHealthImpact.Restriction, false,
                SmtpResponseCategory.VerificationBlocked, "provider_verification_restriction");

        if (reason is SmtpNormalizedReason.TemporaryFailure or SmtpNormalizedReason.ProviderUnavailable or
            SmtpNormalizedReason.RoutingTemporaryFailure)
            return Decision(SmtpMailboxImpact.Provisional, SmtpRetryDisposition.RetryWithBackoff,
                SmtpCooldownScope.Domain, SmtpHealthImpact.TemporaryFailure, false,
                SmtpResponseCategory.TemporaryFailure, "temporary_failure_backoff");

        if (reason == SmtpNormalizedReason.ConnectionTimeout)
            return Decision(SmtpMailboxImpact.Provisional, SmtpRetryDisposition.RetryWithBackoff,
                SmtpCooldownScope.Domain, SmtpHealthImpact.TemporaryFailure, false,
                SmtpResponseCategory.Timeout, "connection_timeout_backoff");

        if (reason is SmtpNormalizedReason.ConnectionFailure or SmtpNormalizedReason.GreetingRejected)
            return Decision(SmtpMailboxImpact.Provisional, SmtpRetryDisposition.RetryWithBackoff,
                SmtpCooldownScope.Domain, SmtpHealthImpact.TemporaryFailure, false,
                SmtpResponseCategory.ConnectionRejected, "connection_or_greeting_failure");

        if (reason is SmtpNormalizedReason.EhloRejected or SmtpNormalizedReason.ProtocolFailure or
            SmtpNormalizedReason.DnsFailure or SmtpNormalizedReason.TlsFailure or
            SmtpNormalizedReason.RoutingPermanentFailure)
            return Decision(SmtpMailboxImpact.None, SmtpRetryDisposition.None, SmtpCooldownScope.None,
                SmtpHealthImpact.None, false, SmtpResponseCategory.ProtocolFailure,
                "non_recipient_protocol_or_routing_failure");

        return Decision(SmtpMailboxImpact.None, SmtpRetryDisposition.None, SmtpCooldownScope.None,
            SmtpHealthImpact.None, false, SmtpResponseCategory.Unknown, "unknown_response_is_conservative");
    }

    private SmtpResponseDecision Decision(
        SmtpMailboxImpact mailboxImpact,
        SmtpRetryDisposition retry,
        SmtpCooldownScope cooldown,
        SmtpHealthImpact health,
        bool allowSenderRotation,
        SmtpResponseCategory category,
        string reason)
    {
        var resultState = mailboxImpact is SmtpMailboxImpact.Valid or SmtpMailboxImpact.Invalid
            ? ValidationResultState.Final
            : retry != SmtpRetryDisposition.None || allowSenderRotation || category == SmtpResponseCategory.Unknown
                ? ValidationResultState.Provisional
                : ValidationResultState.Final;
        return new(mailboxImpact, resultState, retry, cooldown, health, allowSenderRotation,
            category, reason, _version);
    }
}

/// <summary>
/// Runs legacy and candidate interpretation under an explicit rollout mode. Shadow mode records
/// comparisons and attaches immutable candidate evidence while preserving every legacy canonical field.
/// </summary>
public sealed class SmtpResponseClassificationOrchestrator(
    ICanonicalSmtpResponseClassifier canonical,
    ISmtpResponseIntelligenceClassifier candidate,
    ISmtpResponseDecisionPolicy decisions,
    ISmtpResponseIntelligenceMetrics metrics,
    IOptions<EmailValidationOptions> options) : ISmtpResponseClassifier
{
    private readonly SmtpResponseIntelligenceMode _mode = options.Value.SmtpResponseIntelligence.Mode;

    public SmtpEvidence Classify(
        SmtpCommand command,
        int? responseCode,
        string? response,
        TimeSpan elapsed,
        MailProvider provider,
        string mxHost,
        int attempt = 1,
        SmtpResponseObservationContext? observation = null)
    {
        var context = new SmtpResponseClassificationContext(
            command, responseCode, response, elapsed, provider, mxHost, attempt, observation);
        var current = canonical.Classify(context);
        if (_mode == SmtpResponseIntelligenceMode.Disabled)
        {
            metrics.Record(new(_mode, provider, command, current.Category, current.Category,
                SmtpNormalizedReason.UnknownProviderResponse, true));
            return current;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var classification = candidate.Classify(context);
            var decision = decisions.Decide(classification);
            var agreement = current.Category == decision.CanonicalCategory;
            var baseline = ProjectCanonical(current);
            watch.Stop();
            metrics.Record(new(_mode, provider, command, current.Category, decision.CanonicalCategory,
                classification.Reason, agreement, classification.RuleEvaluationFailed,
                watch.Elapsed.TotalMilliseconds,
                baseline.Reason == classification.Reason,
                baseline.Decision.MailboxImpact == decision.MailboxImpact,
                baseline.Decision.ResultState == decision.ResultState,
                baseline.Decision.RetryDisposition == decision.RetryDisposition,
                baseline.Decision.CooldownScope == decision.CooldownScope,
                baseline.Decision.AllowSenderRotation == decision.AllowSenderRotation,
                baseline.Decision.HealthImpact == decision.HealthImpact));

            if (_mode == SmtpResponseIntelligenceMode.Shadow)
                return current with
                {
                    Intelligence = classification,
                    Decision = decision,
                    IntelligenceMode = _mode,
                    CanonicalOutcomeChanged = false
                };

            return current with
            {
                ResponseCode = classification.ReplyCode,
                EnhancedStatusCode = classification.EnhancedStatusCode,
                Category = decision.CanonicalCategory,
                TextClassification = ToTextClassification(classification.Reason),
                SanitizedResponse = classification.SanitizedResponse,
                Intelligence = classification,
                Decision = decision,
                IntelligenceMode = _mode,
                CanonicalOutcomeChanged = !agreement
            };
        }
        catch (Exception exception) when (IsRecoverableClassificationFailure(exception))
        {
            watch.Stop();
            metrics.Record(new(_mode, provider, command, current.Category, SmtpResponseCategory.Unknown,
                SmtpNormalizedReason.UnknownProviderResponse, false, true, watch.Elapsed.TotalMilliseconds));
            if (_mode == SmtpResponseIntelligenceMode.Shadow) return current;
            var fallback = new SmtpResponseIntelligence(
                command, responseCode, responseCode / 100, current.EnhancedStatusCode,
                SmtpNormalizedReason.UnknownProviderResponse, SmtpEvidenceStrength.None,
                provider, "classification-fallback", "unknown-provider-response",
                current.SanitizedResponse, ObservedAtUtc: current.Timestamp, RuleEvaluationFailed: true);
            var fallbackDecision = decisions.Decide(fallback);
            return current with
            {
                Category = SmtpResponseCategory.Unknown,
                TextClassification = SmtpResponseTextClassification.Unknown,
                Intelligence = fallback,
                Decision = fallbackDecision,
                IntelligenceMode = _mode,
                CanonicalOutcomeChanged = current.Category != SmtpResponseCategory.Unknown
            };
        }
    }

    private static SmtpResponseTextClassification ToTextClassification(SmtpNormalizedReason reason) => reason switch
    {
        SmtpNormalizedReason.RecipientAccepted => SmtpResponseTextClassification.Success,
        SmtpNormalizedReason.CommandAccepted => SmtpResponseTextClassification.Success,
        SmtpNormalizedReason.MailboxNotFound or SmtpNormalizedReason.RecipientRejected =>
            SmtpResponseTextClassification.RecipientDoesNotExist,
        SmtpNormalizedReason.MailboxDisabled or SmtpNormalizedReason.MailboxInactive =>
            SmtpResponseTextClassification.MailboxUnavailable,
        SmtpNormalizedReason.MailboxFull => SmtpResponseTextClassification.MailboxFull,
        SmtpNormalizedReason.Greylisted => SmtpResponseTextClassification.Greylisting,
        SmtpNormalizedReason.ProviderRateLimit or SmtpNormalizedReason.ProviderConnectionLimit =>
            SmtpResponseTextClassification.RateLimit,
        SmtpNormalizedReason.PolicyBlock or SmtpNormalizedReason.SenderPolicyRejected =>
            SmtpResponseTextClassification.PolicyRejection,
        SmtpNormalizedReason.VerificationRefused => SmtpResponseTextClassification.VerificationUnavailable,
        SmtpNormalizedReason.TemporaryFailure or SmtpNormalizedReason.ProviderUnavailable or
            SmtpNormalizedReason.RoutingTemporaryFailure => SmtpResponseTextClassification.TemporaryCondition,
        _ => SmtpResponseTextClassification.Unknown
    };

    private static CanonicalProjection ProjectCanonical(SmtpEvidence evidence)
    {
        var reason = evidence.Category switch
        {
            SmtpResponseCategory.Accepted => SmtpNormalizedReason.RecipientAccepted,
            SmtpResponseCategory.RecipientRejected => SmtpNormalizedReason.MailboxNotFound,
            SmtpResponseCategory.MailboxFull => SmtpNormalizedReason.MailboxFull,
            SmtpResponseCategory.Greylisted => SmtpNormalizedReason.Greylisted,
            SmtpResponseCategory.RateLimited => SmtpNormalizedReason.ProviderRateLimit,
            SmtpResponseCategory.VerificationBlocked => SmtpNormalizedReason.PolicyBlock,
            SmtpResponseCategory.TemporaryFailure => SmtpNormalizedReason.TemporaryFailure,
            SmtpResponseCategory.Timeout => SmtpNormalizedReason.ConnectionTimeout,
            SmtpResponseCategory.ConnectionRejected => SmtpNormalizedReason.ConnectionFailure,
            SmtpResponseCategory.ProtocolFailure => SmtpNormalizedReason.ProtocolFailure,
            _ => SmtpNormalizedReason.UnknownProviderResponse
        };
        var mailbox = evidence.Category switch
        {
            SmtpResponseCategory.Accepted => SmtpMailboxImpact.Valid,
            SmtpResponseCategory.RecipientRejected => SmtpMailboxImpact.Invalid,
            SmtpResponseCategory.MailboxFull or SmtpResponseCategory.Greylisted or
                SmtpResponseCategory.RateLimited or SmtpResponseCategory.VerificationBlocked or
                SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Timeout or
                SmtpResponseCategory.ConnectionRejected => SmtpMailboxImpact.Provisional,
            _ => SmtpMailboxImpact.None
        };
        var retry = evidence.Category is SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited or
            SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.TemporaryFailure or
            SmtpResponseCategory.Timeout or SmtpResponseCategory.ConnectionRejected
            ? SmtpRetryDisposition.RetryWithBackoff
            : SmtpRetryDisposition.None;
        var cooldown = evidence.Category is SmtpResponseCategory.RateLimited or SmtpResponseCategory.VerificationBlocked
            ? SmtpCooldownScope.MxProvider
            : retry == SmtpRetryDisposition.None ? SmtpCooldownScope.None : SmtpCooldownScope.Domain;
        var state = mailbox is SmtpMailboxImpact.Valid or SmtpMailboxImpact.Invalid
            ? ValidationResultState.Final
            : retry != SmtpRetryDisposition.None ? ValidationResultState.Provisional : ValidationResultState.Final;
        return new(reason, new(mailbox, state, retry, cooldown, SmtpHealthImpact.None, false,
            evidence.Category, "canonical_projection", "canonical"));
    }

    private sealed record CanonicalProjection(SmtpNormalizedReason Reason, SmtpResponseDecision Decision);

    private static bool IsRecoverableClassificationFailure(Exception exception) =>
        exception is not OutOfMemoryException and not AccessViolationException;
}
