using Microsoft.Extensions.Options;

namespace EmailValidation.Core;

/// <summary>
/// Centralizes decisions about when domain catch-all evidence can replace live SMTP work.
/// Persistence only supplies evidence; it never decides the validation plan.
/// </summary>
public sealed class ValidationPlanBuilder(IOptions<EmailValidationOptions> options) : IValidationPlanBuilder
{
    private readonly EmailValidationOptions _options = options.Value;

    public ValidationPlan Build(
        DomainIntelligence? intelligence,
        bool smtpEnabled,
        bool domainIntelligenceReused,
        ValidationPolicyVersions currentPolicy,
        DateTimeOffset now)
    {
        if (intelligence is null)
            return new(true, false, smtpEnabled, false, "Domain intelligence is missing.");

        var strategyCompatible = string.Equals(
            intelligence.StrategyVersion,
            currentPolicy.ProviderStrategyVersion,
            StringComparison.Ordinal);
        var domainFresh = intelligence.EvidenceExpiresAt is { } expiresAt && expiresAt > now;
        if (!domainFresh || !strategyCompatible)
            return new(
                true,
                false,
                smtpEnabled,
                false,
                !domainFresh
                    ? "Domain intelligence is stale."
                    : "The provider strategy version changed.");

        var catchAll = intelligence.CatchAll;
        var observedAt = catchAll.ObservedAt ?? intelligence.ObservedAt;
        var catchAllFresh = observedAt != default &&
            observedAt.AddMinutes(Math.Max(0, _options.CatchAll.CacheMinutes)) > now;
        var refreshBackoffActive = catchAll.RefreshInconclusive &&
            catchAll.RefreshAttemptedAt is { } attemptedAt &&
            attemptedAt.AddMinutes(Math.Max(0, _options.ResultReuse.TransientMinutes)) > now;
        var reusableCatchAll = _options.CatchAll.Enabled &&
            catchAll.Status == CatchAllStatus.LikelyCatchAll &&
            catchAll.Confidence >= _options.CatchAll.MinimumReusableConfidence &&
            catchAllFresh;
        var performCatchAllProbe = smtpEnabled && _options.CatchAll.Enabled && !refreshBackoffActive &&
            (catchAll.Status == CatchAllStatus.NotAttempted ||
             !catchAllFresh ||
             (catchAll.Status == CatchAllStatus.LikelyCatchAll &&
              catchAll.Confidence < _options.CatchAll.MinimumReusableConfidence));
        var usePersistedCatchAll = domainIntelligenceReused && reusableCatchAll && !performCatchAllProbe;

        return new(
            false,
            performCatchAllProbe,
            smtpEnabled && !usePersistedCatchAll,
            usePersistedCatchAll,
            usePersistedCatchAll
                ? "Fresh, high-confidence domain catch-all evidence makes recipient SMTP acceptance non-discriminating."
                : performCatchAllProbe
                    ? "Catch-all evidence requires live evaluation."
                    : "The normal mailbox validation policy applies.");
    }
}
