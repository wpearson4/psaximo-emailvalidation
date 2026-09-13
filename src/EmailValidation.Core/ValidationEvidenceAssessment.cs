namespace EmailValidation.Core;

/// <summary>
/// Derives presentation metadata from the same structured evidence used by classification. These labels explain
/// evidence provenance and catch-all basis; they do not change the underlying deliverability decision.
/// </summary>
public static class ValidationEvidenceAssessment
{
    public static EvidenceQuality Quality(
        EmailValidationStatus status,
        DomainIntelligence domain,
        SmtpProbeResult probe,
        ProviderValidationResult provider)
    {
        if (domain.Dns.Status == DnsStatus.DomainNotFound || domain.Dns.ExplicitNullMx ||
            !domain.Dns.MxPresent || domain.MailInfrastructure.Status == MailInfrastructureStatus.Unroutable)
            return EvidenceQuality.Conclusive;
        if (provider.ReasonCodes.Contains(ReasonCode.MxResultsConflicting))
            return EvidenceQuality.Partial;
        if (probe.SessionEvidence?.HasStrongRecipientRejection == true ||
            SmtpRecipientEvidencePolicy.HasRecipientMailboxFull(probe))
            return EvidenceQuality.Conclusive;
        if (provider.ReasonCodes.Contains(ReasonCode.ProviderEvidenceConflicting))
            return EvidenceQuality.Partial;

        var category = provider.EffectiveCategory;
        if (category == SmtpResponseCategory.LocalCooldown ||
            probe.Disposition is SmtpProbeDisposition.LocalCooldown or SmtpProbeDisposition.NotAttempted)
            return EvidenceQuality.NotAttempted;
        if (category is SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.MailboxUnknown or
            SmtpResponseCategory.SmtpUtf8Unsupported ||
            probe.Disposition == SmtpProbeDisposition.RemoteBlocked)
            return EvidenceQuality.Blocked;
        if (category is SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited or
            SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Timeout or
            SmtpResponseCategory.ConnectionRejected or SmtpResponseCategory.ProtocolFailure or
            SmtpResponseCategory.Unknown or SmtpResponseCategory.NotAttempted or
            SmtpResponseCategory.GatewayAccepted)
            return EvidenceQuality.Partial;
        if (status == EmailValidationStatus.CatchAll ||
            domain.CatchAll.EffectiveRecipientBehavior is
                DomainRecipientBehavior.CatchAll or DomainRecipientBehavior.AcceptAll ||
            domain.CatchAll.ReasonCode == CatchAllReasonCode.AcceptAllCandidate)
            return EvidenceQuality.Partial;
        if (domain.CatchAll.Status == CatchAllStatus.Unknown &&
            domain.CatchAll.Probes > 0 &&
            domain.CatchAll.Accepted == 0 &&
            domain.CatchAll.Rejected == 0 &&
            domain.CatchAll.Ambiguous == domain.CatchAll.Probes)
            return EvidenceQuality.Partial;
        return EvidenceQuality.Conclusive;
    }

    public static CatchAllClassification CatchAllType(
        EmailValidationStatus status,
        DomainIntelligence domain,
        ProviderValidationResult provider,
        HistoricalSignalSummary history)
    {
        if (status != EmailValidationStatus.CatchAll) return CatchAllClassification.None;
        if (domain.CatchAll.EffectiveRecipientBehavior == DomainRecipientBehavior.CatchAll)
            return domain.CatchAll.Confidence >= 0.95
                ? CatchAllClassification.Confirmed
                : CatchAllClassification.Likely;
        return CatchAllClassification.Likely;
    }
}
