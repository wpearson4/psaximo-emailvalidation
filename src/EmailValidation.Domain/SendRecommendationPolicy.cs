namespace EmailValidation.Core;

public static class SendRecommendationPolicy
{
    public static SendRecommendation Evaluate(
        EmailValidationStatus status,
        EmailValidationChecks checks,
        DomainIntelligence? domain,
        EmailAddressIntelligence? address)
    {
        var reasons = new List<string>();
        if (checks.DisposableDomain) reasons.Add("Disposable");
        if (checks.RoleAccount && checks.CatchAll == CatchAllStatus.LikelyCatchAll) reasons.Add("RoleBasedCatchAll");
        if (domain?.ToxicDomain.Status is ToxicDomainStatus.KnownToxic or ToxicDomainStatus.LikelyToxic) reasons.Add("ToxicDomain");
        if (address?.SpamTrapRisk.Status is SpamTrapRiskStatus.PossibleSpamTrap or SpamTrapRiskStatus.LikelySpamTrap or SpamTrapRiskStatus.KnownSpamTrap) reasons.Add("SpamTrapRisk");
        if (address?.AbuseRisk.Status == AbuseRiskStatus.KnownRisk) reasons.Add("AbuseRisk");
        if (address?.Suppression.Status == SuppressionStatus.Suppressed) reasons.Add("SuppressionMatch");

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

}
