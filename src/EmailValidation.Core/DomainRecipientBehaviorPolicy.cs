namespace EmailValidation.Core;

/// <summary>
/// Confirms stable public SMTP accept-all behavior only after independent,
/// recipient-accepted observations. A single randomized-recipient session is
/// retained as a candidate and cannot establish the domain behavior by itself.
/// </summary>
public static class DomainRecipientBehaviorPolicy
{
    public static CatchAllDetectionResult NormalizePersisted(
        CatchAllDetectionResult evidence,
        int acceptAllMinimumIndependentObservations,
        int minimumAcceptedProbes)
    {
        var requiredObservations = Math.Max(2, acceptAllMinimumIndependentObservations);
        var requiredAccepted = Math.Clamp(minimumAcceptedProbes, 2, 3);
        if (evidence.RecipientBehavior is DomainRecipientBehavior.RecipientSpecific or
            DomainRecipientBehavior.CatchAll)
            return evidence;

        if (evidence.RecipientBehavior == DomainRecipientBehavior.AcceptAll &&
            evidence.ReasonCode == CatchAllReasonCode.AcceptAllConfirmed &&
            evidence.IndependentObservationCount >= requiredObservations)
            return evidence;

        if (evidence.Accepted >= requiredAccepted && evidence.Accepted == evidence.Probes &&
            evidence.Rejected == 0 && evidence.Ambiguous == 0)
            return evidence with
            {
                Status = CatchAllStatus.Unknown,
                RecipientBehavior = DomainRecipientBehavior.Unknown,
                ReasonCode = CatchAllReasonCode.AcceptAllCandidate,
                Detail = "Persisted randomized-recipient acceptance is an accept-all candidate pending an independent confirmation session.",
                IndependentObservationCount = Math.Max(1, evidence.IndependentObservationCount),
                RefreshInconclusive = true
            };

        var invalidAcceptAll = evidence.RecipientBehavior == DomainRecipientBehavior.AcceptAll;
        return evidence with
        {
            RecipientBehavior = invalidAcceptAll
                ? DomainRecipientBehavior.Unknown
                : evidence.EffectiveRecipientBehavior,
            ReasonCode = invalidAcceptAll
                ? CatchAllReasonCode.MixedOrInconclusive
                : evidence.ReasonCode,
            RefreshInconclusive = invalidAcceptAll || evidence.RefreshInconclusive
        };
    }

    public static CatchAllDetectionResult Evaluate(
        CatchAllDetectionResult current,
        SmtpProbeResult targetProbe,
        IReadOnlyList<ValidationObservation> priorObservations,
        MailProvider provider,
        CatchAllOptions options,
        bool currentControlProbePerformed)
    {
        if (current.EffectiveRecipientBehavior == DomainRecipientBehavior.AcceptAll)
        {
            if (targetProbe.Status != SmtpMailboxStatus.Rejected) return current;
            return current with
            {
                Status = CatchAllStatus.Unknown,
                RecipientBehavior = DomainRecipientBehavior.Unknown,
                ReasonCode = CatchAllReasonCode.MixedOrInconclusive,
                Detail = "Previously confirmed accept-all behavior was contradicted by an explicit target-recipient rejection.",
                Confidence = 0.20,
                IndependentObservationCount = 0,
                RefreshInconclusive = true
            };
        }

        if (current.EffectiveRecipientBehavior != DomainRecipientBehavior.Unknown ||
            current.ReasonCode != CatchAllReasonCode.AcceptAllCandidate ||
            !currentControlProbePerformed)
            return current;

        var minimumAccepted = Math.Clamp(options.MinimumAcceptedProbes, 2, 3);
        if (!IsQualifyingControlObservation(current, minimumAccepted) ||
            targetProbe.Status != SmtpMailboxStatus.Accepted ||
            current.ObservedAt is not { } currentObservedAt)
            return current;

        var correlationWindow = TimeSpan.FromMinutes(
            Math.Max(1, options.AcceptAllSessionCorrelationMinutes));
        var minimumSeparation = TimeSpan.FromMinutes(
            Math.Max(1, options.AcceptAllMinimumObservationSeparationMinutes));
        var qualifyingPriorSessions = priorObservations
            .Where(observation => observation.Type == ValidationObservationType.CatchAllProbe &&
                observation.Provider == provider &&
                observation.RandomRecipientProbeCount >= minimumAccepted &&
                observation.RandomRecipientAcceptedCount == observation.RandomRecipientProbeCount &&
                observation.RandomRecipientRejectedCount == 0 &&
                HasAcceptedTargetInSession(observation, priorObservations, provider, correlationWindow))
            .OrderByDescending(observation => observation.ObservedAt)
            .ToArray();

        var independentObservationCount = 1;
        var previousAcceptedAt = currentObservedAt;
        foreach (var prior in qualifyingPriorSessions)
        {
            if (previousAcceptedAt - prior.ObservedAt < minimumSeparation) continue;
            independentObservationCount++;
            previousAcceptedAt = prior.ObservedAt;
        }

        var required = Math.Max(2, options.AcceptAllMinimumIndependentObservations);
        if (independentObservationCount < required)
        {
            var nextEligibleAt = currentObservedAt.Add(minimumSeparation);
            return current with
            {
                IndependentObservationCount = independentObservationCount,
                Detail = $"Accept-all candidate observed {independentObservationCount} of {required} required independent times. Confirmation can occur after {nextEligibleAt:O} if the target and randomized controls are accepted again.",
                RefreshInconclusive = true
            };
        }

        return current with
        {
            Status = CatchAllStatus.Unknown,
            RecipientBehavior = DomainRecipientBehavior.AcceptAll,
            ReasonCode = CatchAllReasonCode.AcceptAllConfirmed,
            Detail = $"Public SMTP accept-all behavior was confirmed across {independentObservationCount} independent observations separated by at least {minimumSeparation.TotalMinutes:0} minutes; this does not prove catch-all routing.",
            Confidence = Math.Min(0.97, 0.90 + (0.02 * (independentObservationCount - required))),
            IndependentObservationCount = independentObservationCount,
            RefreshInconclusive = false
        };
    }

    private static bool IsQualifyingControlObservation(
        CatchAllDetectionResult evidence,
        int minimumAccepted) =>
        evidence.Probes >= minimumAccepted &&
        evidence.Accepted == evidence.Probes &&
        evidence.Rejected == 0 &&
        evidence.Ambiguous == 0;

    private static bool HasAcceptedTargetInSession(
        ValidationObservation control,
        IReadOnlyList<ValidationObservation> observations,
        MailProvider provider,
        TimeSpan correlationWindow) =>
        observations.Any(observation =>
            observation.Type == ValidationObservationType.MailboxProbe &&
            observation.Provider == provider &&
            observation.ObservedAt >= control.ObservedAt &&
            observation.ObservedAt - control.ObservedAt <= correlationWindow &&
            observation.ResponseCategory is SmtpResponseCategory.Accepted or SmtpResponseCategory.GatewayAccepted);
}
