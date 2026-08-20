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
        ProbeSenderHealth senderHealth)
    {
        var session = probe.SessionEvidence;
        if (session?.HasStrongRecipientRejection == true)
            return "High confidence because MAIL FROM succeeded and RCPT TO returned a recipient-specific permanent rejection.";
        if (status == EmailValidationStatus.Invalid)
            return "High confidence because definitive syntax, DNS, mail-routing, or recipient evidence established invalidity.";
        if (!senderHealth.IsOperational && senderHealth.Status != ProbeSenderHealthStatus.NotChecked)
            return "Low confidence because the configured probe sender was not healthy enough for recipient validation.";

        if (session?.MailFrom is { ResponseCode: >= 500 and < 600 })
            return "Low confidence because the server rejected the configured probe sender before recipient validation occurred.";
        if (mxValidation?.Consensus == MxConsensus.Conflicting)
            return "Low confidence because the consulted MX hosts returned conflicting recipient evidence.";
        if (probe.Status == SmtpMailboxStatus.Accepted &&
            domain.CatchAll.Status is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll)
            return "High confidence because the target recipient was accepted while randomized recipients were consistently rejected.";
        if (domain.CatchAll.Status == CatchAllStatus.LikelyCatchAll)
            return "Mailbox existence is uncertain because the domain accepts randomized recipients.";
        if (probe.Status == SmtpMailboxStatus.Accepted)
            return "The target was accepted, but unresolved catch-all or gateway behavior limits mailbox certainty.";
        return "Evidence is incomplete or ambiguous, so the result is intentionally conservative.";
    }
}
