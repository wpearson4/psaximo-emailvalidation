using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

/// <summary>Retains decision inputs without persisting SMTP transcripts or sender identities.</summary>
internal static class PersistedControlEvidence
{
    public static SmtpProbeResult Sanitize(SmtpProbeResult probe) => new(
        probe.Status, probe.ResponseCode, null, probe.ConnectionDuration, probe.Attempts,
        probe.Evidence is { } evidence ? new SmtpEvidence(
            evidence.Command, evidence.ResponseCode, evidence.EnhancedStatusCode, evidence.Category,
            evidence.TextClassification, evidence.ElapsedMilliseconds, evidence.Provider,
            evidence.MxHost, evidence.Attempt, evidence.Timestamp) : null,
        probe.SessionEvidence is { } session ? new SmtpSessionEvidence(
            session.FailedStage,
            session.Stages.Where(stage => stage.Stage is SmtpCommand.MailFrom or SmtpCommand.RcptTo)
                .Select(stage => new SmtpStageResult(stage.Stage, stage.ResponseCode,
                    stage.EnhancedStatusCode, stage.Category, stage.TextClassification, stage.Duration)).ToArray(),
            session.MxHost, session.Duration, string.Empty) : null)
    {
        Disposition = probe.Disposition,
        RetryAfter = probe.RetryAfter,
        LocalBindFailure = probe.LocalBindFailure
    };

    public static CatchAllDetectionResult RequireProvenance(CatchAllDetectionResult controls)
    {
        // Historical writers removed the entire probe array. Counts and a contract label
        // cannot reconstruct the missing MAIL FROM / RCPT TO evidence. Refresh on use.
        var claimsRecipientEvidence = controls.EffectiveRecipientBehavior is
            DomainRecipientBehavior.RecipientSpecific or DomainRecipientBehavior.AcceptAll ||
            controls.ReasonCode == CatchAllReasonCode.AcceptAllCandidate;
        if (controls.RoutingAttestation is not null || controls.Probes == 0 ||
            controls.ProbeResults.Count == controls.Probes && (!claimsRecipientEvidence || controls.ProbeResults.All(probe =>
                probe.SessionEvidence is { MailFromSucceeded: true, RcptTo: not null } &&
                SmtpRecipientEvidencePolicy.RecipientObservedAt(probe) is not null &&
                SmtpRecipientEvidencePolicy.MxHost(probe) is not null)))
            return controls;

        return controls with
        {
            Status = CatchAllStatus.NotAttempted,
            RecipientBehavior = DomainRecipientBehavior.Unknown,
            ReasonCode = CatchAllReasonCode.MixedOrInconclusive,
            Probes = 0, Accepted = 0, Rejected = 0, Ambiguous = 0,
            ProbeResults = [], Confidence = 0, IndependentObservationCount = 0,
            EvidenceContractVersion = null, ControlScope = null,
            RefreshInconclusive = true,
            Detail = "Stored controls lack SMTP-stage provenance and require fresh observation."
        };
    }
}
