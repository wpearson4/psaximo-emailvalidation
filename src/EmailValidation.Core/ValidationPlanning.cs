using Microsoft.Extensions.Options;

namespace EmailValidation.Core;

/// <summary>
/// Centralizes decisions about when domain catch-all evidence can replace live SMTP work.
/// Persistence only supplies evidence; it never decides the validation plan.
/// </summary>
public sealed class ValidationPlanBuilder(IOptions<EmailValidationOptions> options) : IValidationPlanBuilder
{
    private readonly EmailValidationOptions _options = options.Value;

    public ValidationPlan Build(DomainIntelligence? intelligence, bool smtpEnabled,
        bool domainIntelligenceReused, ValidationPolicyVersions currentPolicy, DateTimeOffset now)
    {
        var baseline = BuildBaseline(intelligence, smtpEnabled, domainIntelligenceReused, currentPolicy, now);
        if (intelligence is null || _options.ProviderCapabilities.Mode == ProviderCapabilityMode.Disabled)
            return baseline;
        var provider = intelligence.Provider.Provider;
        var key = ProviderCapabilityPolicy.Key(provider, intelligence.Domain);
        var candidateProfile = ProviderCapabilityPolicy.Resolve(_options, provider, intelligence.Domain, candidate: true);
        var candidate = Apply(baseline, intelligence, candidateProfile, smtpEnabled, now);
        var active = Apply(baseline, intelligence,
            ProviderCapabilityPolicy.Resolve(_options, provider, intelligence.Domain), smtpEnabled, now);
        var applied = _options.ProviderCapabilities.Mode == ProviderCapabilityMode.Enforced &&
            ProviderCapabilityPolicy.Approved(_options.ProviderCapabilities) &&
            _options.ProviderCapabilities.CanaryProviders.Contains(key, StringComparer.OrdinalIgnoreCase);
        return active with
        {
            Capabilities = new(key, _options.ProviderCapabilities.PolicyVersion,
                ProviderCapabilityPolicy.Fingerprint(_options.ProviderCapabilities), _options.ProviderCapabilities.Mode,
                !candidate.PerformMailboxProbe && baseline.PerformMailboxProbe,
                !candidate.PerformCatchAllProbe && baseline.PerformCatchAllProbe, applied,
                candidate.UsePersistedNonDiscrimination ? NonDiscriminationExpiresAt(intelligence, candidateProfile) : null)
            { WouldReuseNonDiscrimination = candidate.UsePersistedNonDiscrimination }
        };
    }

    private ValidationPlan Apply(ValidationPlan baseline, DomainIntelligence domain,
        ProviderCapabilityProfile profile, bool smtpEnabled, DateTimeOffset now)
    {
        var plan = baseline;
        if (!baseline.RefreshDomainIntelligence && smtpEnabled && _options.CatchAll.Enabled &&
            profile.ReuseConfirmedNonDiscrimination && domain.CatchAll.HasConfirmedAcceptAllEvidence)
        {
            if (QualifiedNonDiscrimination(domain, now) && NonDiscriminationExpiresAt(domain, profile) > now)
                plan = plan with
                {
                    PerformMailboxProbe = false, PerformCatchAllProbe = false,
                    UsePersistedCatchAll = false, UsePersistedNonDiscrimination = true,
                    Reason = "Fresh confirmed public-endpoint non-discrimination cannot establish mailbox existence."
                };
            else
            {
                var backoff = domain.CatchAll.RefreshInconclusive && domain.CatchAll.RefreshAttemptedAt is { } at &&
                    at.AddMinutes(Math.Max(0, _options.ResultReuse.TransientMinutes)) > now;
                plan = plan with { PerformCatchAllProbe = !backoff, PerformMailboxProbe = true };
            }
        }
        return plan with
        {
            PerformMailboxProbe = plan.PerformMailboxProbe && profile.ProbeMailbox,
            PerformCatchAllProbe = plan.PerformCatchAllProbe && profile.ProbeControls,
            ProviderRestricted = smtpEnabled && !profile.ProbeMailbox,
            Reason = !profile.ProbeMailbox ? "The reviewed provider capability policy disables mailbox probes." : plan.Reason
        };
    }

    private DateTimeOffset NonDiscriminationExpiresAt(DomainIntelligence domain, ProviderCapabilityProfile profile) =>
        new[] { DomainEvidenceFreshness.ExpiresAt(domain, _options.DomainIntelligence),
            domain.CatchAll.EvidenceExpiresAt ?? DateTimeOffset.MinValue,
            (domain.CatchAll.ObservedAt ?? DateTimeOffset.MinValue).AddMinutes(
                Math.Min(Math.Max(0, _options.CatchAll.CacheMinutes), profile.NonDiscriminationRefreshMinutes)) }.Min();

    private bool QualifiedNonDiscrimination(DomainIntelligence domain, DateTimeOffset now)
    {
        var controls = domain.CatchAll;
        var scope = controls.ControlScope;
        // Current persisted controls cover one endpoint. Multiple published endpoints require live work.
        var endpoints = domain.MxRecords.Select(mx =>
            (mx.Preference, Host: SmtpRecipientEvidencePolicy.NormalizeHost(mx.Host))).Distinct().ToArray();
        return domain.Dns.Status == DnsStatus.Success && !domain.Dns.HasDefinitiveNoRoute &&
            domain.MailInfrastructure.Status != MailInfrastructureStatus.Unroutable &&
            domain.Provider.Provider != MailProvider.Unknown && endpoints.Length == 1 &&
            !controls.RefreshInconclusive && controls.Confidence >= _options.CatchAll.MinimumReusableConfidence &&
            controls.IndependentObservationCount >= Math.Max(2, _options.CatchAll.AcceptAllMinimumIndependentObservations) &&
            controls.Accepted >= Math.Clamp(_options.CatchAll.MinimumAcceptedProbes, 2, 3) &&
            controls.ObservedAt is { } observedAt && observedAt <= now &&
            controls.StrategyVersion == domain.StrategyVersion && scope is not null &&
            !string.IsNullOrWhiteSpace(scope.AcquisitionId) &&
            scope.MxHost == endpoints[0].Host && scope.Preference == endpoints[0].Preference &&
            scope.Provider == domain.Provider.Provider && scope.GatewayProvider == domain.Provider.GatewayProvider &&
            scope.StrategyVersion == domain.StrategyVersion &&
            scope.TopologyFingerprint == EndpointControlEvidencePolicy.TopologyFingerprint(domain) &&
            controls.ProbeResults.Count == controls.Probes && controls.ProbeResults.All(probe =>
                SmtpRecipientEvidencePolicy.HasRecipientAcceptance(probe) &&
                SmtpRecipientEvidencePolicy.MxHost(probe) == scope.MxHost &&
                SmtpRecipientEvidencePolicy.RecipientObservedAt(probe) is { } at && at <= observedAt &&
                observedAt - at <= TimeSpan.FromMinutes(Math.Max(1, _options.CatchAll.AcceptAllSessionCorrelationMinutes)));
    }

    private ValidationPlan BuildBaseline(
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
        var domainFresh = DomainEvidenceFreshness.ExpiresAt(intelligence, _options.DomainIntelligence) > now;
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
            (catchAll.EvidenceExpiresAt is null || catchAll.EvidenceExpiresAt > now) &&
            observedAt.AddMinutes(Math.Max(0, _options.CatchAll.CacheMinutes)) > now;
        var refreshBackoffActive = catchAll.RefreshInconclusive &&
            catchAll.RefreshAttemptedAt is { } attemptedAt &&
            attemptedAt.AddMinutes(Math.Max(0, _options.ResultReuse.TransientMinutes)) > now;
        var acceptAllConfirmationDue = catchAll.ReasonCode == CatchAllReasonCode.AcceptAllCandidate &&
            observedAt.AddMinutes(Math.Max(1,
                _options.CatchAll.AcceptAllMinimumObservationSeparationMinutes)) <= now;
        var reusableCatchAll = _options.CatchAll.Enabled &&
            catchAll.HasIndependentRoutingEvidence &&
            catchAll.Confidence >= _options.CatchAll.MinimumReusableConfidence &&
            catchAllFresh;
        var performCatchAllProbe = smtpEnabled && _options.CatchAll.Enabled && !refreshBackoffActive &&
            (catchAll.Status == CatchAllStatus.NotAttempted ||
             !catchAllFresh ||
             acceptAllConfirmationDue ||
             (catchAll.HasIndependentRoutingEvidence &&
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
