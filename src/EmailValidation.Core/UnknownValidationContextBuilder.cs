namespace EmailValidation.Core;

/// <summary>
/// Projects already-normalized validation evidence into actionable context without
/// changing the underlying classification or retry policy.
/// </summary>
public static class UnknownValidationContextBuilder
{
    public static UnknownValidationContext? Build(EmailValidationResult result)
    {
        if (result.Status != EmailValidationStatus.Unknown) return null;

        var category = result.ProviderValidation?.EffectiveCategory
            ?? result.Diagnostics?.SmtpResponseCategory
            ?? result.SmtpEvidence?.Category
            ?? SmtpResponseCategory.NotAttempted;
        var reasons = result.ReasonCodes;

        if (reasons.Contains(ReasonCode.ProbeSenderNotConfigured) ||
            reasons.Contains(ReasonCode.ProbeSenderUnhealthy))
            return Create(
                result,
                UnknownCause.ProbeSenderUnavailable,
                result.ProbeSenderHealth?.Detail ??
                    "A healthy authorized probe sender was not available for mailbox verification.",
                false,
                "Configure or restore a healthy authorized probe sender, then start a new validation.",
                category);

        if (reasons.Contains(ReasonCode.DnsTimeout))
            return Create(result, UnknownCause.DnsTimeout,
                "The domain lookup timed out before mail routing could be established.", true,
                "Retry after resolver or network connectivity recovers.", category);

        if (reasons.Contains(ReasonCode.DnsFailure))
            return Create(result, UnknownCause.DnsFailure,
                "The domain lookup failed without definitive evidence that the domain is invalid.", true,
                "Retry after checking resolver health and DNS connectivity.", category);

        if (category == SmtpResponseCategory.LocalCooldown || reasons.Contains(ReasonCode.LocalCooldown))
            return Create(result, UnknownCause.LocalCooldown,
                result.RetryAfter is { } retryAfter
                    ? $"Mailbox probing was deferred by local pacing until {retryAfter:O}."
                    : "Mailbox probing was deferred by a local domain or provider cooldown.",
                true,
                "Retry at or after the indicated retry time; do not bypass the cooldown.", category);

        if (category == SmtpResponseCategory.Greylisted || reasons.Contains(ReasonCode.Greylisted))
            return Create(result, UnknownCause.Greylisted,
                "The destination temporarily greylisted the SMTP probe.", true,
                "Retry after the provider's greylisting delay.", category);

        if (category == SmtpResponseCategory.RateLimited || reasons.Contains(ReasonCode.RateLimited))
            return Create(result, UnknownCause.RateLimited,
                "The destination rate-limited mailbox verification.", true,
                "Wait for the provider cooldown and retry without rotating sender or source identity.", category);

        if (category is SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.MailboxUnknown ||
            reasons.Contains(ReasonCode.ProviderBlockedVerification) ||
            reasons.Contains(ReasonCode.ProviderVerificationBlocked) ||
            reasons.Contains(ReasonCode.PolicyBlock))
            return Create(result, UnknownCause.ProviderVerificationBlocked,
                "The destination did not provide recipient-specific verification evidence.", true,
                "Wait for the provider cooldown and retry later; do not attempt to circumvent the provider policy.", category);

        if (category == SmtpResponseCategory.SmtpUtf8Unsupported ||
            reasons.Contains(ReasonCode.SmtpUtf8Unsupported))
            return Create(result, UnknownCause.SmtpUtf8Unsupported,
                "The address requires SMTPUTF8, but the destination MX did not advertise that capability.", false,
                "Use delivery outcome evidence or another supported verification method for this address.", category);

        if (category == SmtpResponseCategory.Timeout || reasons.Contains(ReasonCode.SmtpTimeout) ||
            reasons.Contains(ReasonCode.Timeout))
            return Create(result, UnknownCause.SmtpTimeout,
                "The SMTP session timed out before recipient-specific evidence was obtained.", true,
                "Check outbound TCP/25 connectivity and retry after the destination becomes responsive.", category);

        if (category == SmtpResponseCategory.ConnectionRejected ||
            reasons.Contains(ReasonCode.SmtpConnectionFailure))
            return Create(result, UnknownCause.SmtpConnectionFailure,
                "The SMTP connection was rejected or failed before recipient verification completed.", true,
                "Check outbound TCP/25 connectivity and retry later.", category);

        if (category == SmtpResponseCategory.TemporaryFailure ||
            reasons.Contains(ReasonCode.TemporarySmtpFailure) || reasons.Contains(ReasonCode.TemporaryFailure))
            return Create(result, UnknownCause.TemporarySmtpFailure,
                "The destination returned a temporary SMTP failure.", true,
                "Retry after the destination or provider cooldown clears.", category);

        if (reasons.Contains(ReasonCode.MxResultsConflicting))
            return Create(result, UnknownCause.ConflictingMxEvidence,
                "The consulted MX hosts returned conflicting mailbox evidence.", true,
                "Retry later so the MX hosts can be evaluated again; do not treat the mailbox as valid or invalid yet.", category);

        if (reasons.Contains(ReasonCode.SmtpDisabled) || category == SmtpResponseCategory.NotAttempted)
            return Create(result, UnknownCause.LiveVerificationDisabled,
                "Live SMTP mailbox verification was disabled by the request or application configuration.", false,
                "Enable live SMTP validation and start a new validation if recipient-level evidence is required.", category);

        if (category is SmtpResponseCategory.ProtocolFailure or SmtpResponseCategory.Unknown)
            return Create(result, UnknownCause.AmbiguousSmtpResponse,
                "The SMTP response could not be interpreted as recipient acceptance or rejection.", true,
                "Review the sanitized SMTP stage evidence and retry only after the transient condition changes.", category);

        return Create(result, UnknownCause.InsufficientEvidence,
            result.ConfidenceReason ?? "The available evidence was insufficient for a safe mailbox classification.",
            false,
            "Review the reason codes and evidence before deciding whether another validation attempt is useful.", category);
    }

    public static UnknownValidationContext ExecutionFailure(string summary) => new(
        UnknownCause.ExecutionFailure,
        summary,
        true,
        "Retry the validation after checking application and dependency health.");

    private static UnknownValidationContext Create(
        EmailValidationResult result,
        UnknownCause cause,
        string summary,
        bool retryable,
        string recommendedAction,
        SmtpResponseCategory category)
    {
        var session = result.SmtpSessionEvidence;
        var evidence = result.SmtpEvidence;
        var failedStage = session?.FailedStage is { } stage
            ? session.Stages.LastOrDefault(item => item.Stage == stage)
            : null;
        return new(
            cause,
            summary,
            retryable,
            recommendedAction,
            category,
            session?.FailedStage ?? evidence?.Command,
            evidence?.ResponseCode ?? failedStage?.ResponseCode,
            evidence?.EnhancedStatusCode ?? failedStage?.EnhancedStatusCode,
            session?.MxHost ?? evidence?.MxHost ?? result.SelectedMx,
            result.RetryAfter);
    }
}
