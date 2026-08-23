namespace EmailValidation.Core;

/// <summary>
/// Produces one human-readable explanation from the same structured evidence
/// used by classification. Output formatting does not invent its own rationale.
/// </summary>
public static class EvidenceConfidenceExplainer
{
    public static string Explain(
        EmailValidationStatus status,
        DomainIntelligence domain,
        SmtpProbeResult probe,
        MxValidationEvidence? mxValidation,
        ProbeSenderHealth senderHealth,
        ProviderValidationResult? providerValidation = null)
    {
        var session = probe.SessionEvidence;
        if (session?.HasStrongRecipientRejection == true)
            return "High confidence because MAIL FROM succeeded and RCPT TO returned a recipient-specific permanent rejection.";
        if (status == EmailValidationStatus.Invalid)
            return "High confidence because definitive syntax, DNS, mail-routing, or recipient evidence established invalidity.";
        if (!senderHealth.IsOperational && senderHealth.Status != ProbeSenderHealthStatus.NotChecked)
            return "Low confidence because the configured probe sender was not healthy enough for recipient validation.";

        if (session?.MailFrom is { ResponseCode: >= 500 and < 600 })
            return IsSourceOrProviderRestriction(probe)
                ? "Low confidence because a provider or source-IP policy blocked verification before recipient validation occurred."
                : "Low confidence because MAIL FROM was rejected before recipient validation occurred.";
        if (mxValidation?.Consensus == MxConsensus.Conflicting)
            return "Low confidence because the consulted MX hosts returned conflicting recipient evidence.";
        if (domain.CatchAll.Status == CatchAllStatus.LikelyCatchAll && !probe.ProbeAttempted)
        {
            var reason = string.IsNullOrWhiteSpace(domain.CatchAll.Detail)
                ? "the domain consistently accepts randomized recipients"
                : domain.CatchAll.Detail.Trim().TrimEnd('.');
            return $"Persisted domain evidence: {reason}. Individual mailbox existence cannot be confirmed, and no new mailbox SMTP check was performed.";
        }
        var category = providerValidation?.EffectiveCategory ?? probe.Evidence?.Category ?? SmtpResponseCategory.Unknown;
        var inconclusiveReason = category switch
        {
            SmtpResponseCategory.LocalCooldown => probe.RetryAfter is { } retryAfter
                ? $"Live SMTP verification was not attempted because a local MX-scoped cooldown is active until {retryAfter:O}; retry is recommended."
                : "Live SMTP verification was not attempted because a local MX-scoped cooldown is active; retry is recommended.",
            SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.MailboxUnknown =>
                "High confidence that validation is inconclusive because the provider blocked mailbox verification.",
            SmtpResponseCategory.SmtpUtf8Unsupported =>
                "High confidence that validation is inconclusive because the destination does not advertise SMTPUTF8 required by this address.",
            SmtpResponseCategory.Greylisted =>
                "High confidence that validation is inconclusive because the provider temporarily greylisted the probe.",
            SmtpResponseCategory.RateLimited =>
                "High confidence that validation is inconclusive because the provider rate-limited the probe.",
            SmtpResponseCategory.TemporaryFailure =>
                "High confidence that validation is inconclusive because the provider returned a temporary failure.",
            SmtpResponseCategory.Timeout =>
                "High confidence that validation is inconclusive because the SMTP verification timed out.",
            SmtpResponseCategory.ConnectionRejected =>
                "High confidence that validation is inconclusive because the SMTP connection was rejected before mailbox verification.",
            SmtpResponseCategory.NotAttempted =>
                "The classification is Unknown because live SMTP mailbox verification was not performed.",
            _ => null
        };
        if (inconclusiveReason is not null) return inconclusiveReason;
        if (probe.Status == SmtpMailboxStatus.Accepted &&
            domain.CatchAll.Status is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll)
            return "High confidence because the target recipient was accepted while randomized recipients were consistently rejected.";
        if (domain.CatchAll.Status == CatchAllStatus.LikelyCatchAll)
            return "Mailbox existence is uncertain because the domain accepts randomized recipients.";
        if (probe.Status == SmtpMailboxStatus.Accepted)
            return "The target was accepted, but unresolved catch-all or gateway behavior limits mailbox certainty.";
        return "Evidence is incomplete or ambiguous, so the result is intentionally conservative.";
    }

    private static bool IsSourceOrProviderRestriction(SmtpProbeResult probe)
    {
        var evidence = probe.Evidence;
        if (evidence?.Category == SmtpResponseCategory.RateLimited ||
            evidence?.TextClassification is SmtpResponseTextClassification.AntiAbuse or
                SmtpResponseTextClassification.RateLimit or
                SmtpResponseTextClassification.RelayDenied or
                SmtpResponseTextClassification.VerificationUnavailable)
            return true;
        var response = evidence?.SanitizedResponse ?? probe.Response ?? string.Empty;
        string[] markers =
            ["source ip", "your ip", "ip address", "blacklist", "spamhaus", "reputation", "anti-abuse", "reverse dns", "forward-confirmed"];
        return markers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
