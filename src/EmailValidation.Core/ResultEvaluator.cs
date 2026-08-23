namespace EmailValidation.Core;

public sealed class ResultEvaluator : IResultEvaluator
{
    public ResultEvaluation Evaluate(
        EmailValidationStatus status,
        EmailValidationChecks checks,
        DomainIntelligence domain,
        EmailAddressIntelligence address,
        ProviderValidationResult providerValidation,
        SmtpEvidence? smtpEvidence,
        HistoricalSignalSummary history)
    {
        var details = new List<DetailedStatus>();
        var reasons = new List<ReasonCode>();
        var provenance = new List<EvidenceProvenance>();

        if (domain.Provider.Provider != MailProvider.Unknown)
            provenance.Add(new("Provider", EvidenceSource.Mx, domain.Provider.Confidence,
                $"MX topology matched {domain.Provider.Provider}."));
        if (history.ObservationCount > 0)
            provenance.Add(new("HistoricalBehavior", EvidenceSource.HistoricalObservation,
                history.VerificationReliability, $"{history.ObservationCount} topology-scoped observations were evaluated."));

        AddRouting(domain, details, reasons, provenance);
        AddMailbox(providerValidation, smtpEvidence, details, reasons, provenance);
        AddDomainIntelligence(checks, domain, details, reasons, provenance);
        AddAddressIntelligence(address, details, reasons, provenance);

        if (checks.RoleAccount)
        {
            Add(details, DetailedStatus.RoleBased);
            provenance.Add(new("RoleBased", EvidenceSource.LocalIntelligence, 0.95,
                "The local part matches the configured role-account set."));
        }
        if (checks.CatchAll == CatchAllStatus.LikelyCatchAll)
        {
            Add(details, DetailedStatus.CatchAll);
            provenance.Add(new("CatchAll", EvidenceSource.Smtp,
                domain.CatchAll.Confidence, domain.CatchAll.Detail ?? "Random-recipient SMTP evidence."));
        }
        if (checks.RoleAccount && checks.CatchAll == CatchAllStatus.LikelyCatchAll)
        {
            Add(details, DetailedStatus.RoleBasedCatchAll);
            reasons.Add(ReasonCode.RoleBasedCatchAll);
        }

        var bounceRisk = DeriveBounceRisk(checks, domain, providerValidation);
        var recommendation = DeriveRecommendation(status, checks, domain, address);
        var primary = SelectPrimary(details);
        return new(
            primary,
            details,
            new ValidationRisk(bounceRisk, checks.RoleAccount, address.SpamTrapRisk.Status, address.AbuseRisk.Status),
            recommendation,
            provenance,
            reasons.Distinct().ToArray());
    }

    private static void AddRouting(
        DomainIntelligence domain,
        List<DetailedStatus> details,
        List<ReasonCode> reasons,
        List<EvidenceProvenance> provenance)
    {
        if (domain.Dns.Status == DnsStatus.DomainNotFound)
        {
            Add(details, DetailedStatus.DomainNotFound);
            provenance.Add(new("Domain", EvidenceSource.Dns, 0.99, "DNS returned NXDOMAIN."));
        }
        if (domain.Dns.ExplicitNullMx || !domain.Dns.MxPresent)
        {
            Add(details, DetailedStatus.NoMailRouting);
            reasons.Add(ReasonCode.NoMailRouting);
            provenance.Add(new("MailRouting", EvidenceSource.Mx, 0.99,
                domain.Dns.ExplicitNullMx ? "The domain publishes an explicit null MX." : "No usable mail route was found."));
        }
        if (domain.MailInfrastructure.Status == MailInfrastructureStatus.Unroutable && domain.Dns.MxPresent)
        {
            Add(details, DetailedStatus.UnroutableMailInfrastructure);
            reasons.Add(ReasonCode.UnroutableMailInfrastructure);
            provenance.Add(new("MailInfrastructure", EvidenceSource.Mx, domain.MailInfrastructure.Confidence,
                "No published MX host resolved to a usable address."));
        }
    }

    private static void AddMailbox(
        ProviderValidationResult provider,
        SmtpEvidence? smtpEvidence,
        List<DetailedStatus> details,
        List<ReasonCode> reasons,
        List<EvidenceProvenance> provenance)
    {
        var detail = provider.EffectiveCategory switch
        {
            SmtpResponseCategory.Accepted => DetailedStatus.MailboxAccepted,
            SmtpResponseCategory.RecipientRejected => DetailedStatus.MailboxRejected,
            SmtpResponseCategory.MailboxFull => DetailedStatus.MailboxFull,
            SmtpResponseCategory.Greylisted => DetailedStatus.Greylisted,
            SmtpResponseCategory.RateLimited => DetailedStatus.RateLimited,
            SmtpResponseCategory.VerificationBlocked or SmtpResponseCategory.MailboxUnknown => DetailedStatus.VerificationBlocked,
            SmtpResponseCategory.SmtpUtf8Unsupported => DetailedStatus.SmtpUtf8Unsupported,
            SmtpResponseCategory.LocalCooldown => DetailedStatus.LocalCooldown,
            SmtpResponseCategory.TemporaryFailure => DetailedStatus.TemporaryFailure,
            SmtpResponseCategory.Timeout => DetailedStatus.Timeout,
            _ => DetailedStatus.Unknown
        };
        if (detail == DetailedStatus.Unknown) return;
        Add(details, detail);
        if (detail == DetailedStatus.MailboxRejected &&
            smtpEvidence?.TextClassification == SmtpResponseTextClassification.RecipientDoesNotExist)
            Add(details, DetailedStatus.MailboxNotFound);
        if (detail == DetailedStatus.MailboxFull) reasons.Add(ReasonCode.MailboxFull);
        if (detail == DetailedStatus.TemporaryFailure) reasons.Add(ReasonCode.TemporaryFailure);
        if (detail == DetailedStatus.Timeout) reasons.Add(ReasonCode.Timeout);
        var confidence = detail switch
        {
            DetailedStatus.MailboxRejected => 0.95,
            DetailedStatus.MailboxFull => 0.90,
            _ => provider.VerificationReliability
        };
        provenance.Add(new("Mailbox", EvidenceSource.Smtp, confidence, provider.Explanation));
    }

    private static void AddDomainIntelligence(
        EmailValidationChecks checks,
        DomainIntelligence domain,
        List<DetailedStatus> details,
        List<ReasonCode> reasons,
        List<EvidenceProvenance> provenance)
    {
        if (checks.DisposableDomain)
        {
            Add(details, DetailedStatus.Disposable);
            provenance.Add(new("DisposableDomain",
                domain.DisposableIntelligence.EvidenceSource ?? EvidenceSource.LocalIntelligence,
                domain.DisposableIntelligence.Confidence,
                "The domain matches configured disposable-domain intelligence."));
        }
        if (domain.ToxicDomain.Status is ToxicDomainStatus.KnownToxic or ToxicDomainStatus.LikelyToxic)
        {
            Add(details, DetailedStatus.ToxicDomain);
            reasons.Add(ReasonCode.ToxicDomain);
            provenance.Add(new("ToxicDomain",
                domain.ToxicDomain.EvidenceSource ?? EvidenceSource.ConfiguredIntelligenceProvider,
                domain.ToxicDomain.Confidence,
                "The domain matches configured toxic-domain intelligence."));
        }
        if (domain.MxForward.Status is MxForwardStatus.ConfirmedForwarding or MxForwardStatus.LikelyForwarding)
        {
            Add(details, DetailedStatus.MxForward);
            reasons.Add(ReasonCode.MxForward);
            provenance.Add(new("MxForward",
                domain.MxForward.EvidenceSource ?? EvidenceSource.ConfiguredIntelligenceProvider,
                domain.MxForward.Confidence,
                $"The MX matches configured forwarding provider {domain.MxForward.ForwardingProvider}."));
        }
        if (domain.FreeEmailProvider)
        {
            reasons.Add(ReasonCode.FreeEmailProvider);
            provenance.Add(new("FreeEmailProvider", EvidenceSource.LocalIntelligence, 0.95,
                "The domain matches the configured free-email provider set."));
        }
        if (domain.DomainAge.IsKnown)
            provenance.Add(new("DomainAge", domain.DomainAge.EvidenceSource ?? EvidenceSource.ConfiguredIntelligenceProvider,
                domain.DomainAge.Confidence, $"Domain age is {domain.DomainAge.DomainAgeDays} days."));
    }

    private static void AddAddressIntelligence(
        EmailAddressIntelligence address,
        List<DetailedStatus> details,
        List<ReasonCode> reasons,
        List<EvidenceProvenance> provenance)
    {
        if (address.Typo.TypoDetected)
        {
            Add(details, DetailedStatus.TypoDetected);
            reasons.Add(ReasonCode.TypoDetected);
            reasons.Add(ReasonCode.SuggestedDomainCorrection);
            provenance.Add(new("Typo", address.Typo.EvidenceSource, address.Typo.Confidence,
                $"Suggested correction: {address.Typo.SuggestedEmail}."));
        }
        if (address.SpamTrapRisk.Status is SpamTrapRiskStatus.PossibleSpamTrap or
            SpamTrapRiskStatus.LikelySpamTrap or SpamTrapRiskStatus.KnownSpamTrap)
        {
            Add(details, address.SpamTrapRisk.Status == SpamTrapRiskStatus.PossibleSpamTrap
                ? DetailedStatus.PossibleTrap
                : DetailedStatus.SpamTrapRisk);
            reasons.Add(address.SpamTrapRisk.Status == SpamTrapRiskStatus.KnownSpamTrap
                ? ReasonCode.KnownSpamTrap
                : ReasonCode.PossibleSpamTrap);
            provenance.Add(new("SpamTrapRisk",
                address.SpamTrapRisk.EvidenceSource ?? EvidenceSource.Heuristic,
                address.SpamTrapRisk.Confidence,
                address.SpamTrapRisk.Status == SpamTrapRiskStatus.KnownSpamTrap
                    ? "The address matches configured authoritative trap intelligence."
                    : "A conservative heuristic indicates possible trap risk; this is not confirmation."));
        }
        if (address.AbuseRisk.Status == AbuseRiskStatus.KnownRisk)
        {
            Add(details, DetailedStatus.AbuseRisk);
            reasons.Add(ReasonCode.AbuseRisk);
            provenance.Add(new("AbuseRisk",
                address.AbuseRisk.EvidenceSource ?? EvidenceSource.ConfiguredIntelligenceProvider,
                address.AbuseRisk.Confidence,
                "The address matches configured abuse-risk intelligence."));
        }
        if (address.Suppression.Status == SuppressionStatus.Suppressed)
        {
            Add(details, DetailedStatus.GlobalSuppression);
            reasons.Add(ReasonCode.SuppressionMatch);
            provenance.Add(new("Suppression",
                address.Suppression.EvidenceSource ?? EvidenceSource.ConfiguredIntelligenceProvider,
                0.99,
                address.Suppression.Reason ?? "The address appears in the configured suppression source."));
        }
        if (address.Identity.AliasStatus == IdentityStatus.Detected)
        {
            Add(details, DetailedStatus.Alias);
            reasons.Add(ReasonCode.Alias);
        }
        if (address.Identity.AlternateAddressStatus == IdentityStatus.Detected)
        {
            Add(details, DetailedStatus.AlternateAddress);
            reasons.Add(ReasonCode.AlternateAddress);
        }
    }

    private static BounceRisk DeriveBounceRisk(
        EmailValidationChecks checks,
        DomainIntelligence domain,
        ProviderValidationResult provider) => provider.EffectiveCategory switch
        {
            SmtpResponseCategory.RecipientRejected => BounceRisk.High,
            SmtpResponseCategory.MailboxFull => BounceRisk.Moderate,
            SmtpResponseCategory.Accepted when checks.CatchAll is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll => BounceRisk.Low,
            SmtpResponseCategory.Accepted or SmtpResponseCategory.GatewayAccepted => BounceRisk.Moderate,
            _ when domain.Dns.ExplicitNullMx || !domain.Dns.MxPresent ||
                domain.MailInfrastructure.Status == MailInfrastructureStatus.Unroutable => BounceRisk.High,
            _ => BounceRisk.Unknown
        };

    private static SendRecommendation DeriveRecommendation(
        EmailValidationStatus status,
        EmailValidationChecks checks,
        DomainIntelligence domain,
        EmailAddressIntelligence address)
    {
        var reasons = new List<string>();
        if (checks.DisposableDomain) reasons.Add("Disposable");
        if (checks.RoleAccount && checks.CatchAll == CatchAllStatus.LikelyCatchAll) reasons.Add("RoleBasedCatchAll");
        if (domain.ToxicDomain.Status is ToxicDomainStatus.KnownToxic or ToxicDomainStatus.LikelyToxic) reasons.Add("ToxicDomain");
        if (address.SpamTrapRisk.Status is SpamTrapRiskStatus.PossibleSpamTrap or SpamTrapRiskStatus.LikelySpamTrap or SpamTrapRiskStatus.KnownSpamTrap) reasons.Add("SpamTrapRisk");
        if (address.AbuseRisk.Status == AbuseRiskStatus.KnownRisk) reasons.Add("AbuseRisk");
        if (address.Suppression.Status == SuppressionStatus.Suppressed) reasons.Add("SuppressionMatch");

        if (reasons.Count > 0) return new(false, RecommendationRisk.High, reasons);
        if (status is EmailValidationStatus.Invalid or EmailValidationStatus.LikelyInvalid)
            return new(false, RecommendationRisk.High,
                [status == EmailValidationStatus.Invalid ? "TechnicallyInvalid" : "LikelyInvalid"]);
        if (status is EmailValidationStatus.Valid or EmailValidationStatus.LikelyValid)
            return new(true, status == EmailValidationStatus.Valid ? RecommendationRisk.Low : RecommendationRisk.Moderate, []);
        if (status == EmailValidationStatus.CatchAll)
            return new(true, RecommendationRisk.Moderate, ["CatchAll"]);
        return new(null, status == EmailValidationStatus.Risky ? RecommendationRisk.Moderate : RecommendationRisk.Unknown, []);
    }

    private static DetailedStatus SelectPrimary(IReadOnlyList<DetailedStatus> details)
    {
        DetailedStatus[] priority =
        [
            DetailedStatus.DomainNotFound, DetailedStatus.NoMailRouting, DetailedStatus.UnroutableMailInfrastructure,
            DetailedStatus.MailboxRejected, DetailedStatus.MailboxFull, DetailedStatus.GlobalSuppression,
            DetailedStatus.SpamTrapRisk, DetailedStatus.PossibleTrap, DetailedStatus.ToxicDomain,
            DetailedStatus.Disposable, DetailedStatus.RoleBasedCatchAll, DetailedStatus.CatchAll,
            DetailedStatus.Greylisted, DetailedStatus.RateLimited, DetailedStatus.VerificationBlocked,
            DetailedStatus.LocalCooldown, DetailedStatus.TemporaryFailure, DetailedStatus.Timeout, DetailedStatus.TypoDetected,
            DetailedStatus.MailboxAccepted, DetailedStatus.RoleBased, DetailedStatus.MxForward,
            DetailedStatus.Alias, DetailedStatus.AlternateAddress
        ];
        return priority.FirstOrDefault(details.Contains, DetailedStatus.Unknown);
    }

    private static void Add(List<DetailedStatus> details, DetailedStatus detail)
    {
        if (!details.Contains(detail)) details.Add(detail);
    }
}
