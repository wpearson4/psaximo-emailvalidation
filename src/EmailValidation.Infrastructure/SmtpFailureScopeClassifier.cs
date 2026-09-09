using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

internal static class SmtpFailureScopeClassifier
{
    private enum FailureKind
    {
        MailFromAccepted,
        RecipientOutcome,
        SenderInvalid,
        SenderTemporaryFailure,
        ProviderRestriction,
        Inconclusive
    }

    private static readonly string[] SenderMarkers =
        ["sender", "mail from", "from address", "return path", "return-path"];
    private static readonly string[] SourceOrProviderMarkers =
        ["rate limit", "too many", "throttl", "source ip", "your ip", "ip address", "blacklist", "spamhaus", "reputation", "anti-abuse", "reverse dns", "forward-confirmed"];
    private static readonly string[] GenericProviderPolicyMarkers =
        ["access denied", "blocked by policy", "rejected by policy", "authentication required", "relay denied", "relaying denied", "unable to relay"];

    private static FailureKind Classify(SmtpProbeResult result)
    {
        var session = result.SessionEvidence;
        var evidence = result.Evidence;
        var response = evidence?.SanitizedResponse ?? result.Response ?? string.Empty;
        if (evidence is { IntelligenceMode: SmtpResponseIntelligenceMode.Enforced, Decision: { } decision })
        {
            if (decision.AllowSenderRotation)
                return decision.HealthImpact == SmtpHealthImpact.TemporaryFailure
                    ? FailureKind.SenderTemporaryFailure
                    : FailureKind.SenderInvalid;
            if (decision.CooldownScope is SmtpCooldownScope.MxProvider or SmtpCooldownScope.SourceIp)
                return FailureKind.ProviderRestriction;
        }
        var explicitlySenderSpecific = SenderMarkers.Any(marker =>
            response.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (!explicitlySenderSpecific &&
            (evidence?.Category == SmtpResponseCategory.RateLimited ||
             SourceOrProviderMarkers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
             GenericProviderPolicyMarkers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
             evidence?.TextClassification is SmtpResponseTextClassification.AntiAbuse or
                 SmtpResponseTextClassification.RateLimit or
                 SmtpResponseTextClassification.VerificationUnavailable))
            return FailureKind.ProviderRestriction;
        if (session?.MailFromSucceeded == true)
            return session.RecipientStageReached
                ? FailureKind.RecipientOutcome
                : FailureKind.MailFromAccepted;
        if (session?.FailedStage != SmtpCommand.MailFrom && evidence?.Command != SmtpCommand.MailFrom)
            return FailureKind.Inconclusive;
        if (SourceOrProviderMarkers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return FailureKind.ProviderRestriction;
        if (!explicitlySenderSpecific &&
            (GenericProviderPolicyMarkers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
             evidence?.TextClassification is SmtpResponseTextClassification.AntiAbuse or
                 SmtpResponseTextClassification.RateLimit or
                 SmtpResponseTextClassification.RelayDenied or
                 SmtpResponseTextClassification.VerificationUnavailable))
            return FailureKind.ProviderRestriction;
        if (evidence?.ResponseCode is >= 500 and < 600)
            return FailureKind.SenderInvalid;
        if (explicitlySenderSpecific && (evidence?.ResponseCode is >= 400 and < 500 ||
            evidence?.Category is SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted))
            return FailureKind.SenderTemporaryFailure;
        return FailureKind.Inconclusive;
    }

    internal static ValidationFailureScope Scope(SmtpProbeResult result)
    {
        if (result.Evidence is { IntelligenceMode: SmtpResponseIntelligenceMode.Enforced, Decision: { } decision })
            return decision.CooldownScope switch
            {
                SmtpCooldownScope.OutboundIdentity => ValidationFailureScope.Sender,
                SmtpCooldownScope.SourceIp => ValidationFailureScope.SourceIp,
                SmtpCooldownScope.MxProvider => ValidationFailureScope.Provider,
                SmtpCooldownScope.Domain => ValidationFailureScope.Domain,
                _ => decision.MailboxImpact == SmtpMailboxImpact.Invalid
                    ? ValidationFailureScope.Recipient
                    : ValidationFailureScope.Unknown
            };

        var outcome = Classify(result);
        if (outcome is FailureKind.SenderInvalid or FailureKind.SenderTemporaryFailure)
            return ValidationFailureScope.Sender;
        if (outcome == FailureKind.RecipientOutcome)
            return ValidationFailureScope.Recipient;
        if (outcome == FailureKind.ProviderRestriction)
        {
            var response = result.Evidence?.SanitizedResponse ?? result.Response ?? string.Empty;
            return response.Contains("source ip", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("your ip", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("ip address", StringComparison.OrdinalIgnoreCase)
                ? ValidationFailureScope.SourceIp
                : ValidationFailureScope.Provider;
        }
        return result.Evidence?.Category is SmtpResponseCategory.ConnectionRejected or SmtpResponseCategory.Timeout
            ? ValidationFailureScope.Connection
            : ValidationFailureScope.Unknown;
    }
}
