namespace EmailValidation.Core;

/// <summary>
/// Deterministic, explainable evidence weighting. Values describe confidence in the
/// selected classification; they are not calibrated delivery probabilities.
/// </summary>
public sealed class EmailClassificationEngine : IEmailClassificationEngine
{
    private const double SyntaxWeight = 0.20;
    private const double DomainWeight = 0.20;
    private const double MxWeight = 0.15;
    private const double ProviderWeight = 0.10;

    public ClassificationResult Classify(EmailClassificationEvidence evidence)
    {
        var contributions = new List<ConfidenceContribution>();
        var reasons = new List<ReasonCode>();

        if (!evidence.SyntaxValid)
            return Result(EmailValidationStatus.Invalid, 0.99, reasons, contributions, "Invalid syntax", 0.99);
        Add(contributions, "Syntax certainty", SyntaxWeight, "The address parsed successfully.");

        if (evidence.DnsStatus == DnsStatus.DomainNotFound)
            return Result(EmailValidationStatus.Invalid, 0.99, [ReasonCode.DomainNotFound], contributions, "NXDOMAIN", 0.79);
        if (evidence.DnsStatus == DnsStatus.Timeout)
            return Result(EmailValidationStatus.Unknown, 0.88, [ReasonCode.DnsTimeout], contributions, "DNS timeout", 0.68);
        if (evidence.DnsStatus == DnsStatus.Failure)
            return Result(EmailValidationStatus.Unknown, 0.85, [ReasonCode.DnsFailure], contributions, "DNS failure", 0.65);

        var domain = evidence.Domain;
        if (domain is null)
            return Result(EmailValidationStatus.Unknown, 0.80, [ReasonCode.DnsFailure], contributions, "Missing domain evidence", 0.60);
        Add(contributions, "Domain validity", DomainWeight, "The domain exists in DNS.");

        if (domain.Dns.ExplicitNullMx)
            return Result(EmailValidationStatus.Invalid, 0.99, [ReasonCode.NullMailExchanger, ReasonCode.NoMailExchanger], contributions, "Explicit null MX", 0.79);
        if (!domain.Dns.MxPresent)
            return Result(EmailValidationStatus.Invalid, 0.97, [ReasonCode.NoMailExchanger], contributions, "No usable mail route", 0.77);
        if (domain.MailInfrastructure.Status == MailInfrastructureStatus.Unroutable)
            return Result(EmailValidationStatus.Invalid, 0.97, [ReasonCode.UnroutableMailInfrastructure], contributions,
                "Published MX hosts are unroutable", 0.77);
        Add(contributions, "MX validation", MxWeight, "A usable mail route was discovered.");

        if (domain.Dns.UsedAddressFallback) reasons.Add(ReasonCode.ImplicitMxFallback);
        if (domain.Provider.Provider is not MailProvider.Unknown)
        {
            reasons.Add(ReasonCode.ProviderDetected);
            Add(contributions, "Provider confidence", ProviderWeight * domain.Provider.Confidence,
                $"Provider detected as {domain.Provider.Provider} with {domain.Provider.Confidence:P0} confidence.");
        }

        var catchAll = domain.CatchAll;
        switch (catchAll.Status)
        {
            case CatchAllStatus.NotCatchAll:
            case CatchAllStatus.LikelyNotCatchAll:
                Add(contributions, "Catch-all unlikely", 0.10 * Math.Max(0.5, catchAll.Confidence),
                    "Randomized recipients were rejected.");
                break;
            case CatchAllStatus.LikelyCatchAll:
                reasons.Add(ReasonCode.CatchAllDetected);
                reasons.Add(ReasonCode.CatchAllLikely);
                break;
            case CatchAllStatus.Unknown:
            case CatchAllStatus.NotAttempted:
                reasons.Add(ReasonCode.CatchAllUnknown);
                reasons.Add(ReasonCode.CatchAllUncertain);
                Add(contributions, "Catch-all uncertainty", -0.05, "Catch-all behavior could not be established.");
                break;
        }

        if (domain.Disposable)
        {
            reasons.Add(ReasonCode.DisposableDomain);
            Add(contributions, "Disposable-domain risk", -0.20, "The domain appears on the configured disposable list.");
        }
        if (domain.FreeEmailProvider) reasons.Add(ReasonCode.FreeEmailProvider);
        if (domain.ToxicDomain.Status is ToxicDomainStatus.KnownToxic or ToxicDomainStatus.LikelyToxic)
        {
            reasons.Add(ReasonCode.ToxicDomain);
        }
        if (evidence.RoleAccount)
        {
            reasons.Add(ReasonCode.RoleAccount);
            Add(contributions, "Role-account risk", -0.08, "The local part is a configured role account.");
        }
        var address = evidence.AddressIntelligence;
        if (address?.Typo.TypoDetected == true)
        {
            reasons.Add(ReasonCode.TypoDetected);
            reasons.Add(ReasonCode.SuggestedDomainCorrection);
            Add(contributions, "Domain typo", -0.10, "A high-confidence correction exists for the input domain.");
        }
        if (address?.SpamTrapRisk.Status is SpamTrapRiskStatus.PossibleSpamTrap or
            SpamTrapRiskStatus.LikelySpamTrap or SpamTrapRiskStatus.KnownSpamTrap)
        {
            reasons.Add(address.SpamTrapRisk.Status == SpamTrapRiskStatus.KnownSpamTrap
                ? ReasonCode.KnownSpamTrap
                : ReasonCode.PossibleSpamTrap);
        }
        if (address?.AbuseRisk.Status == AbuseRiskStatus.KnownRisk) reasons.Add(ReasonCode.AbuseRisk);
        if (address?.Suppression.Status == SuppressionStatus.Suppressed) reasons.Add(ReasonCode.SuppressionMatch);

        if (evidence.History.LikelyCatchAllCount > 0 ||
            (domain.Provider.Provider != MailProvider.GoogleWorkspace && evidence.History.RandomRecipientAcceptedCount >= 2))
            reasons.Add(ReasonCode.HistoricalCatchAllBehavior);
        if (evidence.History.VerificationBlockedCount > 1)
            reasons.Add(ReasonCode.HistoricalVerificationBlocked);

        var providerResult = evidence.Mailbox?.ProviderEvaluation;
        if (providerResult is null)
        {
            reasons.Add(ReasonCode.SmtpDisabled);
            return FinalizeResult(EmailValidationStatus.Unknown, 0.72, reasons, contributions);
        }

        reasons.AddRange(providerResult.ReasonCodes);
        var category = providerResult.EffectiveCategory;
        switch (category)
        {
            case SmtpResponseCategory.RecipientRejected:
                reasons.Add(ReasonCode.MailboxRejected);
                if (evidence.Mailbox?.Probe.SessionEvidence?.HasStrongRecipientRejection != true)
                {
                    // A RCPT rejection without proof that MAIL FROM succeeded is
                    // negative evidence, but it cannot support mailbox certainty.
                    if (evidence.Mailbox?.Probe.Evidence?.Command == SmtpCommand.RcptTo)
                        return Result(EmailValidationStatus.LikelyInvalid, 0.75, reasons, contributions,
                            "Recipient rejection with incomplete session provenance", 0.12);

                    reasons.Remove(ReasonCode.MailboxRejected);
                    reasons.Add(ReasonCode.ProviderVerificationBlocked);
                    return Result(EmailValidationStatus.Unknown, 0.70, reasons, contributions,
                        "Recipient stage was not proven", 0.05);
                }
                return Result(EmailValidationStatus.Invalid, 0.96, reasons, contributions,
                    "Explicit recipient rejection", 0.21);
            case SmtpResponseCategory.MailboxFull:
                reasons.Add(ReasonCode.MailboxFull);
                return Result(EmailValidationStatus.Risky, 0.90, reasons, contributions,
                    "Mailbox exists but is currently full", 0.18);
            case SmtpResponseCategory.Greylisted:
                reasons.Add(ReasonCode.Greylisted);
                return Result(EmailValidationStatus.Unknown, 0.88, reasons, contributions,
                    "Greylisting", 0.23);
            case SmtpResponseCategory.RateLimited:
                reasons.Add(ReasonCode.RateLimited);
                return Result(EmailValidationStatus.Unknown, 0.88, reasons, contributions,
                    "Rate limiting", 0.23);
            case SmtpResponseCategory.TemporaryFailure:
                reasons.Add(ReasonCode.TemporarySmtpFailure);
                return Result(EmailValidationStatus.Unknown, 0.85, reasons, contributions,
                    "Temporary SMTP failure", 0.20);
            case SmtpResponseCategory.VerificationBlocked:
            case SmtpResponseCategory.MailboxUnknown:
                reasons.Add(ReasonCode.ProviderBlockedVerification);
                reasons.Add(ReasonCode.ProviderVerificationBlocked);
                return Result(EmailValidationStatus.Unknown, 0.87, reasons, contributions,
                    "Provider verification unavailable", 0.22);
            case SmtpResponseCategory.Timeout:
                reasons.Add(ReasonCode.SmtpTimeout);
                return Result(EmailValidationStatus.Unknown, 0.88, reasons, contributions,
                    "SMTP timeout", 0.23);
            case SmtpResponseCategory.ConnectionRejected:
                reasons.Add(ReasonCode.SmtpConnectionFailure);
                return Result(EmailValidationStatus.Unknown, 0.84, reasons, contributions,
                    "SMTP connection rejected", 0.19);
            case SmtpResponseCategory.ProtocolFailure:
            case SmtpResponseCategory.Unknown:
                return Result(EmailValidationStatus.Unknown, 0.80, reasons, contributions,
                    "Ambiguous SMTP response", 0.15);
            case SmtpResponseCategory.NotAttempted:
                reasons.Add(ReasonCode.SmtpDisabled);
                return FinalizeResult(EmailValidationStatus.Unknown, 0.72, reasons, contributions);
        }

        var acceptanceWeight = providerResult.AcceptanceStrength switch
        {
            AcceptanceStrength.High => 0.25,
            AcceptanceStrength.Medium => 0.20,
            AcceptanceStrength.Low => 0.12,
            _ => 0.05
        };
        Add(contributions, "Recipient acceptance", acceptanceWeight, providerResult.Explanation);
        reasons.Add(ReasonCode.MailboxAccepted);

        var historicalCatchAll = evidence.History.LikelyCatchAllCount >= 2 ||
            (domain.Provider.Provider != MailProvider.GoogleWorkspace && evidence.History.RandomRecipientAcceptedCount >= 2);
        var score = Math.Clamp(contributions.Sum(item => item.Weight), 0, 1);
        if (catchAll.Status == CatchAllStatus.LikelyCatchAll || historicalCatchAll)
        {
            var catchAllConfidence = catchAll.Status == CatchAllStatus.LikelyCatchAll
                ? catchAll.Confidence
                : 0.80;
            return FinalizeResult(
                EmailValidationStatus.CatchAll,
                Math.Max(score, catchAllConfidence),
                reasons,
                contributions);
        }

        // Mailing reputation and suppression answer whether an address should be mailed,
        // not whether its mailbox appears technically deliverable. Keep those signals in
        // the independent risk result. Catch-all acceptance has already been classified
        // independently above. A domain typo remains relevant to deliverability.
        var intelligenceRisk = address?.Typo.TypoDetected == true;
        var risky = domain.Disposable || evidence.RoleAccount || intelligenceRisk;
        if (risky)
        {
            var riskConfidence = Math.Clamp(
                0.65 + (domain.Disposable ? 0.08 : 0) + (evidence.RoleAccount ? 0.08 : 0) +
                (intelligenceRisk ? 0.08 : 0),
                0.65,
                0.96);
            return FinalizeResult(EmailValidationStatus.Risky, riskConfidence, reasons, contributions);
        }

        var strongCatchAllNegative = (catchAll.Status is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll) &&
            catchAll.Confidence >= 0.75;
        var status = providerResult.AcceptanceStrength == AcceptanceStrength.High && strongCatchAllNegative
            ? EmailValidationStatus.Valid
            : EmailValidationStatus.LikelyValid;
        return FinalizeResult(status, score, reasons, contributions);
    }

    public ClassificationResult Classify(EmailValidationChecks checks, DnsStatus dnsStatus)
    {
        var catchAllConfidence = checks.CatchAll switch
        {
            CatchAllStatus.NotCatchAll => 0.90,
            CatchAllStatus.LikelyNotCatchAll => 0.75,
            CatchAllStatus.LikelyCatchAll => 0.85,
            _ => 0.20
        };
        var domain = new DomainIntelligence
        {
            Domain = "compatibility.local",
            DomainExists = checks.DomainExists,
            Dns = new DnsLookupResult(
                dnsStatus,
                checks.DomainExists,
                checks.MxPresent ? [new MxRecord(0, "mx.compatibility.local")] : [],
                checks.UsedImplicitMxFallback,
                TimeSpan.Zero),
            Provider = new ProviderDetectionResult(MailProvider.GenericSmtp, 0.55, "compatibility"),
            Disposable = checks.DisposableDomain,
            CatchAll = new CatchAllDetectionResult(checks.CatchAll, 0, 0, 0, 0, Confidence: catchAllConfidence),
            ObservedAt = DateTimeOffset.UtcNow
        };
        var category = MailProviderStrategyBase.ToCategory(checks.Mailbox);
        var providerResult = new ProviderValidationResult(
            MailProvider.GenericSmtp,
            0.55,
            category,
            checks.Mailbox == SmtpMailboxStatus.Accepted
                ? checks.CatchAll is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll
                    ? AcceptanceStrength.High : AcceptanceStrength.Medium
                : AcceptanceStrength.None,
            [],
            "Compatibility classification path.");
        SmtpEvidence? compatibilityEvidence = null;
        SmtpSessionEvidence? compatibilitySession = null;
        if (checks.Mailbox == SmtpMailboxStatus.Rejected)
        {
            compatibilityEvidence = new SmtpEvidence(
                SmtpCommand.RcptTo, 550, "5.1.1", SmtpResponseCategory.RecipientRejected,
                SmtpResponseTextClassification.RecipientDoesNotExist, 0, MailProvider.GenericSmtp,
                domain.MxRecords[0].Host, 1, DateTimeOffset.UtcNow);
            compatibilitySession = new SmtpSessionEvidence(
                SmtpCommand.RcptTo,
                [
                    new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted,
                        SmtpResponseTextClassification.Success, TimeSpan.Zero),
                    new(SmtpCommand.RcptTo, 550, "5.1.1", SmtpResponseCategory.RecipientRejected,
                        SmtpResponseTextClassification.RecipientDoesNotExist, TimeSpan.Zero)
                ],
                domain.MxRecords[0].Host, TimeSpan.Zero, "compatibility@local.test");
        }
        var probe = new SmtpProbeResult(
            checks.Mailbox, null, null, TimeSpan.Zero,
            Evidence: compatibilityEvidence, SessionEvidence: compatibilitySession);
        return Classify(new EmailClassificationEvidence(
            checks.SyntaxValid,
            dnsStatus,
            domain,
            checks.RoleAccount,
            new MailboxEvidence(domain.Domain, domain.MxRecords.Count > 0 ? domain.MxRecords[0].Host : string.Empty, probe, providerResult),
            HistoricalSignalSummary.Empty));
    }

    private static ClassificationResult Result(
        EmailValidationStatus status,
        double confidence,
        IEnumerable<ReasonCode> reasons,
        ICollection<ConfidenceContribution> contributions,
        string evidence,
        double weight)
    {
        Add(contributions, evidence, weight, $"Supports the {status} classification.");
        return FinalizeResult(status, confidence, reasons, contributions);
    }

    private static ClassificationResult FinalizeResult(
        EmailValidationStatus status,
        double confidence,
        IEnumerable<ReasonCode> reasons,
        IEnumerable<ConfidenceContribution> contributions) => new(
            status,
            Math.Round(Math.Clamp(confidence, 0, 1), 2),
            reasons.Distinct().ToArray(),
            contributions.ToArray());

    private static void Add(
        ICollection<ConfidenceContribution> contributions,
        string evidence,
        double weight,
        string explanation) => contributions.Add(new(evidence, weight, explanation));
}
