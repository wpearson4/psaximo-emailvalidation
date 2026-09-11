namespace EmailValidation.Core;

/// <summary>
/// Confirms stable public SMTP accept-all behavior only after independent,
/// recipient-accepted observations. A single randomized-recipient session is
/// retained as a candidate and cannot establish the domain behavior by itself.
/// </summary>
public static class DomainRecipientBehaviorPolicy
{
    public static bool RequiresProtectedObservationRetention(ValidationObservation observation) =>
        observation.Type == ValidationObservationType.CatchAllProbe ||
        observation.Type == ValidationObservationType.MailboxProbe &&
        observation.RecipientEvidenceQualified &&
        observation.ResponseCategory == SmtpResponseCategory.RecipientRejected;

    public static bool IsSemanticallyEquivalent(
        CatchAllDetectionResult left,
        CatchAllDetectionResult right) =>
        left.EffectiveRecipientBehavior == right.EffectiveRecipientBehavior &&
        IsStrongRecipientSpecificEvidence(left) == IsStrongRecipientSpecificEvidence(right) &&
        (left.ReasonCode == CatchAllReasonCode.AcceptAllCandidate) ==
            (right.ReasonCode == CatchAllReasonCode.AcceptAllCandidate) &&
        (left.ReasonCode == CatchAllReasonCode.TargetRecipientContradictedAcceptAll) ==
            (right.ReasonCode == CatchAllReasonCode.TargetRecipientContradictedAcceptAll) &&
        left.HasIndependentRoutingEvidence == right.HasIndependentRoutingEvidence;

    public static CatchAllDetectionResult NormalizePersisted(
        CatchAllDetectionResult evidence,
        int acceptAllMinimumIndependentObservations,
        int minimumAcceptedProbes)
    {
        var requiredObservations = Math.Max(2, acceptAllMinimumIndependentObservations);
        var requiredAccepted = Math.Clamp(minimumAcceptedProbes, 2, 3);
        if (evidence.HasIndependentRoutingEvidence)
            return evidence;
        if (evidence.RecipientBehavior == DomainRecipientBehavior.RecipientSpecific ||
            evidence.Status is CatchAllStatus.NotCatchAll or CatchAllStatus.LikelyNotCatchAll)
        {
            if (HasCurrentEvidenceContract(evidence))
                return evidence;
            return evidence with
            {
                Status = CatchAllStatus.NotAttempted,
                Probes = 0,
                Accepted = 0,
                Rejected = 0,
                Ambiguous = 0,
                Detail = "Legacy recipient-specific evidence lacks the current SMTP-stage provenance contract and must be refreshed.",
                Confidence = 0,
                RecipientBehavior = DomainRecipientBehavior.Unknown,
                ReasonCode = CatchAllReasonCode.MixedOrInconclusive,
                IndependentObservationCount = 0,
                ProbeResults = [],
                RefreshInconclusive = true
            };
        }

        if (evidence.HasConfirmedAcceptAllEvidence &&
            evidence.IndependentObservationCount >= requiredObservations &&
            HasAllAcceptedCounts(evidence, requiredAccepted))
            return evidence;

        if (HasAllAcceptedCounts(evidence, requiredAccepted) &&
            evidence.ReasonCode != CatchAllReasonCode.TargetRecipientContradictedAcceptAll)
            return evidence with
            {
                Status = CatchAllStatus.Unknown,
                RecipientBehavior = DomainRecipientBehavior.Unknown,
                ReasonCode = CatchAllReasonCode.AcceptAllCandidate,
                Detail = "Randomized-recipient acceptance is an accept-all candidate pending an independent confirmation session.",
                IndependentObservationCount = Math.Clamp(evidence.IndependentObservationCount, 0, 1),
                RefreshInconclusive = true
            };

        var invalidAcceptAll = evidence.RecipientBehavior == DomainRecipientBehavior.AcceptAll;
        var untrustedCatchAll = evidence.RecipientBehavior == DomainRecipientBehavior.CatchAll ||
            evidence.Status == CatchAllStatus.LikelyCatchAll;
        return evidence with
        {
            Status = untrustedCatchAll ? CatchAllStatus.Unknown : evidence.Status,
            RecipientBehavior = invalidAcceptAll || untrustedCatchAll
                ? DomainRecipientBehavior.Unknown
                : evidence.EffectiveRecipientBehavior,
            ReasonCode = invalidAcceptAll || untrustedCatchAll
                ? CatchAllReasonCode.MixedOrInconclusive
                : evidence.ReasonCode,
            IndependentObservationCount = evidence.ReasonCode == CatchAllReasonCode.AcceptAllCandidate ||
                invalidAcceptAll || untrustedCatchAll
                    ? 0
                    : evidence.IndependentObservationCount,
            RefreshInconclusive = invalidAcceptAll || untrustedCatchAll || evidence.RefreshInconclusive
        };
    }

    public static CatchAllDetectionResult Evaluate(
        CatchAllDetectionResult current,
        SmtpProbeResult targetProbe,
        IReadOnlyList<ValidationObservation> priorObservations,
        MailProvider provider,
        CatchAllOptions options,
        bool currentControlProbePerformed,
        MxConsensus mxConsensus = MxConsensus.Unknown,
        bool currentTargetAcceptanceUncontested = true)
    {
        current = NormalizePersisted(
            current,
            options.AcceptAllMinimumIndependentObservations,
            options.MinimumAcceptedProbes);
        if (current.EffectiveRecipientBehavior == DomainRecipientBehavior.AcceptAll)
        {
            var controlContradicted =
                current.ProbeResults.Any(SmtpRecipientEvidencePolicy.HasStrongRecipientRejection);
            var targetContradicted = SmtpRecipientEvidencePolicy.HasStrongRecipientRejection(targetProbe);
            if (!controlContradicted && !targetContradicted)
                return current;
            return current with
            {
                Status = CatchAllStatus.Unknown,
                RecipientBehavior = DomainRecipientBehavior.Unknown,
                ReasonCode = CatchAllReasonCode.TargetRecipientContradictedAcceptAll,
                Detail = controlContradicted
                    ? "Previously confirmed accept-all behavior was contradicted by an explicit randomized-recipient rejection."
                    : "Previously confirmed accept-all behavior was contradicted by an explicit target-recipient rejection.",
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
        var currentControlMx = SingleControlMx(current);
        var targetMx = SmtpRecipientEvidencePolicy.MxHost(targetProbe);
        var correlationWindow = TimeSpan.FromMinutes(
            Math.Max(1, options.AcceptAllSessionCorrelationMinutes));
        var currentObservedAt = current.ObservedAt;
        var targetObservedAt = SmtpRecipientEvidencePolicy.RecipientObservedAt(targetProbe);
        if (SmtpRecipientEvidencePolicy.HasStrongRecipientRejection(targetProbe) &&
            currentControlMx is not null && targetMx is not null &&
            string.Equals(currentControlMx, targetMx, StringComparison.OrdinalIgnoreCase) &&
            currentObservedAt is { } rejectionControlAt &&
            targetObservedAt is { } rejectionTargetAt &&
            rejectionTargetAt >= rejectionControlAt &&
            rejectionTargetAt - rejectionControlAt <= correlationWindow)
            return current with
            {
                Status = CatchAllStatus.Unknown,
                RecipientBehavior = DomainRecipientBehavior.Unknown,
                ReasonCode = CatchAllReasonCode.TargetRecipientContradictedAcceptAll,
                Detail = "Randomized controls were accepted but the target recipient was explicitly rejected; accept-all behavior was not established.",
                Confidence = 0.20,
                IndependentObservationCount = 0,
                RefreshInconclusive = true
            };

        if (!IsQualifyingControlObservation(current, minimumAccepted) ||
            !SmtpRecipientEvidencePolicy.HasRecipientAcceptance(targetProbe) ||
            currentObservedAt is not { } qualifyingControlAt ||
            targetObservedAt is not { } currentTargetObservedAt ||
            provider == MailProvider.Unknown ||
            mxConsensus == MxConsensus.Conflicting ||
            !currentTargetAcceptanceUncontested ||
            currentControlMx is null || targetMx is null ||
            !string.Equals(currentControlMx, targetMx, StringComparison.OrdinalIgnoreCase) ||
            currentTargetObservedAt < qualifyingControlAt ||
            currentTargetObservedAt - qualifyingControlAt > correlationWindow)
            return current with { IndependentObservationCount = 0 };

        var minimumSeparation = TimeSpan.FromMinutes(
            Math.Max(1, options.AcceptAllMinimumObservationSeparationMinutes));
        var required = Math.Max(2, options.AcceptAllMinimumIndependentObservations);
        var confirmationHorizon = TimeSpan.FromMinutes(Math.Max(
            Math.Max(1, options.CacheMinutes),
            minimumSeparation.TotalMinutes * (required - 1)));
        var oldestQualifyingAt = qualifyingControlAt - confirmationHorizon;
        var latestContradictionAt = priorObservations
            .Where(observation => observation.ObservedAt >= oldestQualifyingAt &&
                observation.ObservedAt < qualifyingControlAt &&
                IsContradiction(observation))
            .Select(observation => (DateTimeOffset?)observation.ObservedAt)
            .Max();
        var qualifyingPriorSessions = priorObservations
            .Where(observation => observation.Type == ValidationObservationType.CatchAllProbe &&
                observation.Provider == provider &&
                !string.IsNullOrWhiteSpace(observation.ObservationSessionId) &&
                observation.ObservedAt >= oldestQualifyingAt &&
                observation.ObservedAt < qualifyingControlAt &&
                (latestContradictionAt is null || observation.ObservedAt > latestContradictionAt) &&
                observation.RandomRecipientProbeCount >= minimumAccepted &&
                observation.RandomRecipientAcceptedCount == observation.RandomRecipientProbeCount &&
                observation.RandomRecipientRejectedCount == 0 &&
                HasAcceptedTargetInSession(observation, priorObservations, provider, correlationWindow))
            .GroupBy(observation => observation.ObservationSessionId!, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(observation => observation.ObservedAt).First())
            .OrderByDescending(observation => observation.ObservedAt)
            .ToArray();

        var independentObservationCount = 1;
        var previousAcceptedAt = qualifyingControlAt;
        foreach (var prior in qualifyingPriorSessions)
        {
            if (previousAcceptedAt - prior.ObservedAt < minimumSeparation) continue;
            independentObservationCount++;
            previousAcceptedAt = prior.ObservedAt;
        }

        if (independentObservationCount < required)
        {
            var nextEligibleAt = qualifyingControlAt.Add(minimumSeparation);
            return current with
            {
                IndependentObservationCount = independentObservationCount,
                EvidenceContractVersion = CatchAllDetectionResult.CurrentRecipientBehaviorEvidenceContractVersion,
                Detail = $"Accept-all candidate observed {independentObservationCount} of {required} required independent times. Confirmation can occur after {nextEligibleAt:O} if the target and randomized controls are accepted again.",
                RefreshInconclusive = true
            };
        }

        return current with
        {
            Status = CatchAllStatus.Unknown,
            RecipientBehavior = DomainRecipientBehavior.AcceptAll,
            ReasonCode = CatchAllReasonCode.AcceptAllConfirmed,
            EvidenceContractVersion = CatchAllDetectionResult.CurrentRecipientBehaviorEvidenceContractVersion,
            Detail = $"Public SMTP accept-all behavior was confirmed across {independentObservationCount} independent observations separated by at least {minimumSeparation.TotalMinutes:0} minutes; this does not prove catch-all routing.",
            Confidence = Math.Min(0.97, 0.90 + (0.02 * (independentObservationCount - required))),
            IndependentObservationCount = independentObservationCount,
            RefreshInconclusive = false
        };
    }

    private static bool IsQualifyingControlObservation(
        CatchAllDetectionResult evidence,
        int minimumAccepted)
    {
        if (evidence.Probes < minimumAccepted ||
            evidence.ProbeResults.Count != evidence.Probes ||
            evidence.ProbeResults.Any(result => !SmtpRecipientEvidencePolicy.HasRecipientAcceptance(result)))
            return false;

        return evidence.Accepted == evidence.Probes &&
            evidence.Rejected == 0 &&
            evidence.Ambiguous == 0;
    }

    private static bool HasAllAcceptedCounts(CatchAllDetectionResult evidence, int minimumAccepted) =>
        evidence.Probes >= minimumAccepted &&
        evidence.Accepted == evidence.Probes &&
        evidence.Rejected == 0 &&
        evidence.Ambiguous == 0;

    private static bool IsStrongRecipientSpecificEvidence(CatchAllDetectionResult evidence) =>
        evidence.Status == CatchAllStatus.NotCatchAll && evidence.Confidence >= 0.75;

    private static bool HasCurrentEvidenceContract(CatchAllDetectionResult evidence) =>
        string.Equals(
            evidence.EvidenceContractVersion,
            CatchAllDetectionResult.CurrentRecipientBehaviorEvidenceContractVersion,
            StringComparison.Ordinal);

    private static string? SingleControlMx(CatchAllDetectionResult evidence)
    {
        var hosts = evidence.ProbeResults
            .Select(SmtpRecipientEvidencePolicy.MxHost)
            .Where(host => host is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return hosts.Length == 1 ? hosts[0] : null;
    }

    private static bool IsContradiction(ValidationObservation observation) =>
        observation.Type == ValidationObservationType.CatchAllProbe
            ? observation.RandomRecipientRejectedCount > 0 ||
              observation.CorrelatedTargetRecipientEvidenceQualified &&
              observation.CorrelatedTargetResponseCategory == SmtpResponseCategory.RecipientRejected
            : observation.Type == ValidationObservationType.MailboxProbe &&
              observation.RecipientEvidenceQualified &&
              observation.ResponseCategory == SmtpResponseCategory.RecipientRejected;

    private static bool HasAcceptedTargetInSession(
        ValidationObservation control,
        IReadOnlyList<ValidationObservation> observations,
        MailProvider provider,
        TimeSpan correlationWindow)
    {
        if (string.IsNullOrWhiteSpace(control.ObservationSessionId) ||
            string.IsNullOrWhiteSpace(control.MxHost))
            return false;

        if (control.CorrelatedTargetRecipientEvidenceQualified &&
            (control.CorrelatedTargetResponseCategory is
                SmtpResponseCategory.Accepted or SmtpResponseCategory.GatewayAccepted) &&
            control.CorrelatedTargetObservedAt is { } embeddedObservedAt &&
            embeddedObservedAt >= control.ObservedAt &&
            embeddedObservedAt - control.ObservedAt <= correlationWindow &&
            string.Equals(
                control.CorrelatedTargetMxHost,
                control.MxHost,
                StringComparison.OrdinalIgnoreCase))
            return true;

        return observations.Any(observation =>
            observation.Type == ValidationObservationType.MailboxProbe &&
            observation.Provider == provider &&
            observation.RecipientEvidenceQualified &&
            string.Equals(observation.ObservationSessionId, control.ObservationSessionId, StringComparison.Ordinal) &&
            string.Equals(observation.MxHost, control.MxHost, StringComparison.OrdinalIgnoreCase) &&
            observation.ObservedAt >= control.ObservedAt &&
            observation.ObservedAt - control.ObservedAt <= correlationWindow &&
            observation.ResponseCategory is SmtpResponseCategory.Accepted or SmtpResponseCategory.GatewayAccepted);
    }
}
