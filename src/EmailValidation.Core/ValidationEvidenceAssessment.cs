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

        var category = provider.EffectiveCategory;
        if (category == SmtpResponseCategory.LocalCooldown ||
            probe.Disposition is SmtpProbeDisposition.LocalCooldown or SmtpProbeDisposition.NotAttempted)
            return EvidenceQuality.NotAttempted;
        if (category is SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.MailboxUnknown ||
            probe.Disposition == SmtpProbeDisposition.RemoteBlocked)
            return EvidenceQuality.Blocked;
        if (category is SmtpResponseCategory.Greylisted or SmtpResponseCategory.RateLimited or
            SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Timeout or
            SmtpResponseCategory.ConnectionRejected or SmtpResponseCategory.ProtocolFailure or
            SmtpResponseCategory.Unknown or SmtpResponseCategory.NotAttempted or
            SmtpResponseCategory.GatewayAccepted)
            return EvidenceQuality.Partial;
        if (status == EmailValidationStatus.CatchAll)
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
        if (domain.CatchAll.Status == CatchAllStatus.LikelyCatchAll)
            return domain.CatchAll.Confidence >= 0.95
                ? CatchAllClassification.Confirmed
                : CatchAllClassification.Likely;
        if (history.LikelyCatchAllCount >= 2 ||
            (domain.Provider.Provider != MailProvider.GoogleWorkspace && history.RandomRecipientAcceptedCount >= 2))
            return CatchAllClassification.Historical;
        if (provider.EffectiveCategory == SmtpResponseCategory.GatewayAccepted)
            return CatchAllClassification.GatewayAmbiguous;
        return CatchAllClassification.Likely;
    }
}
