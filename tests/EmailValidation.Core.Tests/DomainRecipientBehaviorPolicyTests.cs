using EmailValidation.Core;
using EmailValidation.Infrastructure;

namespace EmailValidation.Core.Tests;

public sealed class DomainRecipientBehaviorPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstQualifyingSession_RemainsCandidate()
    {
        var result = Evaluate(Candidate(Now), AcceptedTarget(), []);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, result.ReasonCode);
        Assert.Equal(1, result.IndependentObservationCount);
        Assert.True(result.RefreshInconclusive);
    }

    [Fact]
    public void TwoAcceptedIndependentSessions_ConfirmAcceptAll()
    {
        var priorControlAt = Now.AddMinutes(-16);
        var observations = new[]
        {
            Control(priorControlAt),
            Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.Accepted)
        };

        var result = Evaluate(Candidate(Now), AcceptedTarget(), observations);

        Assert.Equal(DomainRecipientBehavior.AcceptAll, result.RecipientBehavior);
        Assert.Equal(CatchAllReasonCode.AcceptAllConfirmed, result.ReasonCode);
        Assert.Equal(2, result.IndependentObservationCount);
        Assert.False(result.RefreshInconclusive);
        Assert.InRange(result.Confidence, .90, .97);
    }

    [Fact]
    public void SessionsInsideMinimumSeparation_DoNotConfirm()
    {
        var priorControlAt = Now.AddMinutes(-10);
        var result = Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [Control(priorControlAt), Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.Accepted)]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public void PriorControlsWithoutAcceptedTarget_DoNotConfirm()
    {
        var priorControlAt = Now.AddMinutes(-16);
        var result = Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [Control(priorControlAt), Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.RecipientRejected)]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public void DifferentProviderObservation_DoesNotConfirm()
    {
        var priorControlAt = Now.AddMinutes(-16);
        var otherProviderControl = Control(priorControlAt) with { Provider = MailProvider.GoogleWorkspace };
        var otherProviderTarget = Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.Accepted) with
        {
            Provider = MailProvider.GoogleWorkspace
        };

        var result = Evaluate(Candidate(Now), AcceptedTarget(), [otherProviderControl, otherProviderTarget]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public void CurrentTargetMustBeAcceptedToConfirm()
    {
        var priorControlAt = Now.AddMinutes(-16);
        var result = Evaluate(
            Candidate(Now),
            RejectedTarget(),
            [Control(priorControlAt), Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.Accepted)]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(CatchAllReasonCode.TargetRecipientContradictedAcceptAll, result.ReasonCode);
        Assert.Equal(0, result.IndependentObservationCount);
    }

    [Fact]
    public void CachedCandidateWithoutCurrentControls_CannotConfirm()
    {
        var priorControlAt = Now.AddMinutes(-16);
        var result = DomainRecipientBehaviorPolicy.Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [Control(priorControlAt), Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.Accepted)],
            MailProvider.GenericSmtp,
            Options(),
            currentControlProbePerformed: false);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, result.ReasonCode);
    }

    [Fact]
    public void TargetRejection_DowngradesPreviouslyConfirmedAcceptAll()
    {
        var confirmed = Candidate(Now) with
        {
            RecipientBehavior = DomainRecipientBehavior.AcceptAll,
            ReasonCode = CatchAllReasonCode.AcceptAllConfirmed,
            IndependentObservationCount = 2,
            RefreshInconclusive = false
        };

        var result = Evaluate(
            confirmed,
            RejectedTarget(),
            []);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(CatchAllReasonCode.TargetRecipientContradictedAcceptAll, result.ReasonCode);
        Assert.Equal(0, result.IndependentObservationCount);
        Assert.True(result.RefreshInconclusive);

        var restored = DomainRecipientBehaviorPolicy.NormalizePersisted(result, 2, 2);
        Assert.Equal(CatchAllReasonCode.TargetRecipientContradictedAcceptAll, restored.ReasonCode);
        Assert.Equal(DomainRecipientBehavior.Unknown, restored.EffectiveRecipientBehavior);
    }

    [Fact]
    public void StrongTargetRejection_DowngradesAcceptAllEvenWhenMxPeersConflict()
    {
        var confirmed = Candidate(Now) with
        {
            RecipientBehavior = DomainRecipientBehavior.AcceptAll,
            ReasonCode = CatchAllReasonCode.AcceptAllConfirmed,
            IndependentObservationCount = 2,
            RefreshInconclusive = false
        };

        var result = DomainRecipientBehaviorPolicy.Evaluate(
            confirmed,
            RejectedTarget(),
            [],
            MailProvider.GenericSmtp,
            Options(),
            currentControlProbePerformed: false,
            MxConsensus.Conflicting);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.EffectiveRecipientBehavior);
        Assert.Equal(CatchAllReasonCode.TargetRecipientContradictedAcceptAll, result.ReasonCode);
    }

    [Fact]
    public void LegacyOneSessionAcceptAll_IsNormalizedToCandidate()
    {
        var legacy = Candidate(Now) with
        {
            RecipientBehavior = DomainRecipientBehavior.AcceptAll,
            ReasonCode = CatchAllReasonCode.AcceptAllObserved,
            IndependentObservationCount = 0
        };

        var result = DomainRecipientBehaviorPolicy.NormalizePersisted(legacy, 2, 2);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, result.ReasonCode);
        Assert.Equal(0, result.IndependentObservationCount);
    }

    [Fact]
    public void PreHardeningConfirmedAcceptAll_IsDemotedForFreshConfirmation()
    {
        var legacy = Candidate(Now) with
        {
            RecipientBehavior = DomainRecipientBehavior.AcceptAll,
            ReasonCode = CatchAllReasonCode.AcceptAllConfirmed,
            IndependentObservationCount = 2,
            EvidenceContractVersion = null,
            RefreshInconclusive = false
        };

        var result = DomainRecipientBehaviorPolicy.NormalizePersisted(legacy, 2, 2);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.EffectiveRecipientBehavior);
        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, result.ReasonCode);
        Assert.Equal(1, result.IndependentObservationCount);
        Assert.True(result.RefreshInconclusive);
    }

    [Fact]
    public void PreHardeningRecipientSpecificEvidence_RequiresFreshStageQualifiedControls()
    {
        var legacy = new CatchAllDetectionResult(
            CatchAllStatus.NotCatchAll, 2, 0, 2, 0,
            "Legacy randomized rejections.", .95)
        {
            RecipientBehavior = DomainRecipientBehavior.RecipientSpecific,
            ReasonCode = CatchAllReasonCode.RecipientSpecificObserved,
            StrategyVersion = "1.1.0"
        };

        var result = DomainRecipientBehaviorPolicy.NormalizePersisted(legacy, 2, 2);

        Assert.Equal(CatchAllStatus.NotAttempted, result.Status);
        Assert.Equal(DomainRecipientBehavior.Unknown, result.EffectiveRecipientBehavior);
        Assert.Empty(result.ProbeResults);
        Assert.True(result.RefreshInconclusive);
    }

    [Fact]
    public void LegacySmtpDerivedCatchAll_IsNotTrustedAsRoutingEvidence()
    {
        var legacy = new CatchAllDetectionResult(
            CatchAllStatus.LikelyCatchAll, 2, 2, 0, 0,
            "Random recipients accepted.", .90)
        {
            RecipientBehavior = DomainRecipientBehavior.CatchAll,
            ReasonCode = CatchAllReasonCode.RandomRecipientsAccepted
        };

        var result = DomainRecipientBehaviorPolicy.NormalizePersisted(legacy, 2, 2);

        Assert.Equal(CatchAllStatus.Unknown, result.Status);
        Assert.Equal(DomainRecipientBehavior.Unknown, result.EffectiveRecipientBehavior);
        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, result.ReasonCode);
    }

    [Fact]
    public void IndependentRoutingEvidence_RemainsCatchAll()
    {
        var independent = new CatchAllDetectionResult(
            CatchAllStatus.LikelyCatchAll, 2, 2, 0, 0,
            "Independent routing evidence.", .96)
        {
            RecipientBehavior = DomainRecipientBehavior.CatchAll,
            ReasonCode = CatchAllReasonCode.IndependentRoutingEvidence
        };

        var result = DomainRecipientBehaviorPolicy.NormalizePersisted(independent, 2, 2);

        Assert.Equal(DomainRecipientBehavior.CatchAll, result.EffectiveRecipientBehavior);
        Assert.True(result.HasIndependentRoutingEvidence);
    }

    [Fact]
    public void UncorrelatedAcceptedTarget_CannotConfirmPriorControl()
    {
        var priorControlAt = Now.AddMinutes(-16);
        var result = Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [
                Control(priorControlAt, "control-session"),
                Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.Accepted, "different-session")
            ]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public void PriorAcceptedCategoryWithoutRecipientProvenance_CannotConfirm()
    {
        var priorControlAt = Now.AddMinutes(-16);
        var unqualifiedTarget = Target(
            priorControlAt.AddSeconds(10),
            SmtpResponseCategory.Accepted) with
        {
            RecipientEvidenceQualified = false
        };

        var result = Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [Control(priorControlAt), unqualifiedTarget]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public void DifferentMxAcceptedTarget_CannotConfirmPriorControl()
    {
        var priorControlAt = Now.AddMinutes(-16);
        var result = Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [
                Control(priorControlAt),
                Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.Accepted) with
                {
                    MxHost = "mx2.example.test"
                }
            ]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public void CurrentTargetOutsideCorrelationWindow_DoesNotQualify()
    {
        var result = Evaluate(Candidate(Now), AcceptedTarget(Now.AddMinutes(6)), []);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(0, result.IndependentObservationCount);
    }

    [Fact]
    public void BareAcceptedStatusWithoutRecipientStageEvidence_DoesNotQualify()
    {
        var bare = new SmtpProbeResult(SmtpMailboxStatus.Accepted, 250, "2.1.5", TimeSpan.Zero);

        var result = Evaluate(Candidate(Now), bare, []);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(0, result.IndependentObservationCount);
    }

    [Fact]
    public void ContradictionCutsOffOlderAcceptedSessions()
    {
        var acceptedAt = Now.AddMinutes(-32);
        var contradictionAt = Now.AddMinutes(-20);
        var result = Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [
                Control(acceptedAt, "old-session"),
                Target(acceptedAt.AddSeconds(10), SmtpResponseCategory.Accepted, "old-session"),
                Target(contradictionAt, SmtpResponseCategory.RecipientRejected, "contradiction")
            ]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public void AcceptedSessionOutsideCatchAllFreshnessHorizon_DoesNotConfirm()
    {
        var priorControlAt = Now.AddMinutes(-1441);
        var result = Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [
                Control(priorControlAt),
                Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.Accepted)
            ]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public void StrongSameTopologyContradictionWithUnknownProvider_ResetsAcceptedHistory()
    {
        var acceptedAt = Now.AddMinutes(-32);
        var contradictionAt = Now.AddMinutes(-20);
        var contradiction = Target(
            contradictionAt,
            SmtpResponseCategory.RecipientRejected,
            "contradiction") with
        {
            Provider = MailProvider.Unknown
        };
        var result = Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [
                Control(acceptedAt, "old-session"),
                Target(acceptedAt.AddSeconds(10), SmtpResponseCategory.Accepted, "old-session"),
                contradiction
            ]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public async Task HighVolumeMailboxTraffic_DoesNotEvictQualifyingControlSession()
    {
        var store = new InMemoryValidationObservationStore();
        var firstAt = Now.AddMinutes(-16);
        var firstControl = Control(firstAt, "protected-session") with
        {
            CorrelatedTargetResponseCategory = SmtpResponseCategory.Accepted,
            CorrelatedTargetObservedAt = firstAt.AddSeconds(10),
            CorrelatedTargetMxHost = "mx.example.test",
            CorrelatedTargetRecipientEvidenceQualified = true
        };
        await store.RecordAsync(firstControl);
        for (var index = 0; index < 250; index++)
        {
            await store.RecordAsync(Target(
                firstAt.AddSeconds(20 + index),
                SmtpResponseCategory.Accepted,
                $"noise-{index}"));
        }

        var observations = await store.GetDomainObservationsAsync("example.test");
        var result = Evaluate(Candidate(Now), AcceptedTarget(), observations);

        Assert.Contains(observations, observation =>
            observation.ObservationSessionId == "protected-session");
        Assert.Equal(DomainRecipientBehavior.AcceptAll, result.EffectiveRecipientBehavior);
        Assert.Equal(2, result.IndependentObservationCount);
    }

    [Fact]
    public async Task HighVolumeMailboxTraffic_DoesNotEvictRecipientContradiction()
    {
        var store = new InMemoryValidationObservationStore();
        var firstAt = Now.AddMinutes(-32);
        var contradictionAt = Now.AddMinutes(-20);
        var firstControl = Control(firstAt, "accepted-before-contradiction") with
        {
            CorrelatedTargetResponseCategory = SmtpResponseCategory.Accepted,
            CorrelatedTargetObservedAt = firstAt.AddSeconds(10),
            CorrelatedTargetMxHost = "mx.example.test",
            CorrelatedTargetRecipientEvidenceQualified = true
        };
        var contradiction = Target(
            contradictionAt,
            SmtpResponseCategory.RecipientRejected,
            "protected-contradiction");
        await store.RecordAsync(firstControl);
        await store.RecordAsync(contradiction);
        for (var index = 0; index < 250; index++)
        {
            await store.RecordAsync(Target(
                contradictionAt.AddSeconds(index + 1),
                SmtpResponseCategory.Accepted,
                $"noise-after-contradiction-{index}"));
        }

        var observations = await store.GetDomainObservationsAsync("example.test");
        var result = Evaluate(Candidate(Now), AcceptedTarget(), observations);

        Assert.Contains(observations, observation =>
            observation.ObservationSessionId == "protected-contradiction");
        Assert.Equal(DomainRecipientBehavior.Unknown, result.EffectiveRecipientBehavior);
        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, result.ReasonCode);
        Assert.Equal(1, result.IndependentObservationCount);
    }

    [Fact]
    public void AcceptedTargetWithAnotherAmbiguousMxAttempt_DoesNotQualify()
    {
        var result = DomainRecipientBehaviorPolicy.Evaluate(
            Candidate(Now),
            AcceptedTarget(),
            [],
            MailProvider.GenericSmtp,
            Options(),
            currentControlProbePerformed: true,
            MxConsensus.ConsistentAmbiguous,
            currentTargetAcceptanceUncontested: false);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(0, result.IndependentObservationCount);
    }

    [Fact]
    public void BareOrPreRecipientRejection_DoesNotDowngradeConfirmedAcceptAll()
    {
        var confirmed = Candidate(Now) with
        {
            RecipientBehavior = DomainRecipientBehavior.AcceptAll,
            ReasonCode = CatchAllReasonCode.AcceptAllConfirmed,
            IndependentObservationCount = 2,
            RefreshInconclusive = false
        };
        var bare = new SmtpProbeResult(
            SmtpMailboxStatus.Rejected, 550, "rejected", TimeSpan.Zero);
        var mailFromEvidence = new SmtpEvidence(
            SmtpCommand.MailFrom, 550, "5.7.1", SmtpResponseCategory.VerificationBlocked,
            SmtpResponseTextClassification.PolicyRejection, 1, MailProvider.GenericSmtp,
            "mx.example.test", 1, Now);
        var mailFromRejected = new SmtpProbeResult(
            SmtpMailboxStatus.Blocked, 550, "5.7.1", TimeSpan.Zero,
            Evidence: mailFromEvidence,
            SessionEvidence: new SmtpSessionEvidence(
                SmtpCommand.MailFrom,
                [new(SmtpCommand.MailFrom, 550, "5.7.1", SmtpResponseCategory.VerificationBlocked,
                    SmtpResponseTextClassification.PolicyRejection, TimeSpan.Zero)],
                "mx.example.test",
                TimeSpan.Zero,
                "probe@validator.example"));

        var bareResult = Evaluate(confirmed, bare, []);
        var preRecipientResult = Evaluate(confirmed, mailFromRejected, []);

        Assert.Equal(DomainRecipientBehavior.AcceptAll, bareResult.EffectiveRecipientBehavior);
        Assert.Equal(DomainRecipientBehavior.AcceptAll, preRecipientResult.EffectiveRecipientBehavior);
    }

    [Fact]
    public void StrongRandomizedRecipientRejection_DowngradesConfirmedAcceptAll()
    {
        var confirmed = Candidate(Now) with
        {
            RecipientBehavior = DomainRecipientBehavior.AcceptAll,
            ReasonCode = CatchAllReasonCode.AcceptAllConfirmed,
            IndependentObservationCount = 2,
            RefreshInconclusive = false,
            ProbeResults = [RejectedTarget()]
        };

        var result = Evaluate(confirmed, AcceptedTarget(), []);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.EffectiveRecipientBehavior);
        Assert.Equal(CatchAllReasonCode.TargetRecipientContradictedAcceptAll, result.ReasonCode);
        Assert.Contains("randomized-recipient rejection", result.Detail, StringComparison.Ordinal);
    }

    private static CatchAllDetectionResult Evaluate(
        CatchAllDetectionResult current,
        SmtpProbeResult target,
        IReadOnlyList<ValidationObservation> prior) =>
        DomainRecipientBehaviorPolicy.Evaluate(
            current,
            target,
            prior,
            MailProvider.GenericSmtp,
            Options(),
            currentControlProbePerformed: true);

    private static CatchAllDetectionResult Candidate(DateTimeOffset observedAt) => new(
        CatchAllStatus.Unknown,
        2,
        2,
        0,
        0,
        "Candidate",
        .75)
    {
        RecipientBehavior = DomainRecipientBehavior.Unknown,
        ReasonCode = CatchAllReasonCode.AcceptAllCandidate,
        IndependentObservationCount = 0,
        EvidenceContractVersion = CatchAllDetectionResult.CurrentRecipientBehaviorEvidenceContractVersion,
        ObservedAt = observedAt,
        RefreshInconclusive = true,
        ProbeResults =
        [
            AcceptedProbe("mx.example.test", observedAt.AddSeconds(-1)),
            AcceptedProbe("mx.example.test", observedAt)
        ]
    };

    private static SmtpProbeResult AcceptedTarget(DateTimeOffset? observedAt = null) =>
        AcceptedProbe("mx.example.test", observedAt ?? Now.AddSeconds(10));

    private static SmtpProbeResult RejectedTarget()
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 550, "5.1.1", SmtpResponseCategory.RecipientRejected,
            SmtpResponseTextClassification.RecipientDoesNotExist, 1, MailProvider.GenericSmtp,
            "mx.example.test", 1, Now.AddSeconds(10));
        return new SmtpProbeResult(
            SmtpMailboxStatus.Rejected, 550, "5.1.1", TimeSpan.Zero,
            Evidence: evidence,
            SessionEvidence: RecipientSession(
                "mx.example.test", SmtpResponseCategory.RecipientRejected, 550, "5.1.1"));
    }

    private static SmtpProbeResult AcceptedProbe(string host, DateTimeOffset observedAt)
    {
        var evidence = new SmtpEvidence(
            SmtpCommand.RcptTo, 250, "2.1.5", SmtpResponseCategory.Accepted,
            SmtpResponseTextClassification.Success, 1, MailProvider.GenericSmtp,
            host, 1, observedAt);
        return new SmtpProbeResult(
            SmtpMailboxStatus.Accepted, 250, "2.1.5", TimeSpan.Zero,
            Evidence: evidence,
            SessionEvidence: RecipientSession(host, SmtpResponseCategory.Accepted, 250, "2.1.5"));
    }

    private static SmtpSessionEvidence RecipientSession(
        string host,
        SmtpResponseCategory category,
        int code,
        string enhanced) => new(
        category == SmtpResponseCategory.Accepted ? null : SmtpCommand.RcptTo,
        [
            new(SmtpCommand.MailFrom, 250, "2.1.0", SmtpResponseCategory.Accepted,
                SmtpResponseTextClassification.Success, TimeSpan.Zero),
            new(SmtpCommand.RcptTo, code, enhanced, category,
                category == SmtpResponseCategory.Accepted
                    ? SmtpResponseTextClassification.Success
                    : SmtpResponseTextClassification.RecipientDoesNotExist,
                TimeSpan.Zero)
        ],
        host,
        TimeSpan.Zero,
        "probe@validator.example");

    private static ValidationObservation Control(
        DateTimeOffset observedAt,
        string sessionId = "prior-session") => new(
        "example.test",
        ValidationObservationType.CatchAllProbe,
        MailProvider.GenericSmtp,
        "mx.example.test",
        CatchAllStatus.Unknown,
        .75,
        SmtpResponseCategory.GatewayAccepted,
        observedAt,
        10,
        RandomRecipientAcceptedCount: 2,
        RandomRecipientProbeCount: 2,
        RandomRecipientRejectedCount: 0,
        ObservationSessionId: sessionId);

    private static ValidationObservation Target(
        DateTimeOffset observedAt,
        SmtpResponseCategory category,
        string sessionId = "prior-session") => new(
        "example.test",
        ValidationObservationType.MailboxProbe,
        MailProvider.GenericSmtp,
        "mx.example.test",
        CatchAllStatus.Unknown,
        .75,
        category,
        observedAt,
        10,
        ObservationSessionId: sessionId,
        RecipientEvidenceQualified: true);

    private static CatchAllOptions Options() => new()
    {
        MinimumAcceptedProbes = 2,
        AcceptAllMinimumIndependentObservations = 2,
        AcceptAllMinimumObservationSeparationMinutes = 15,
        AcceptAllSessionCorrelationMinutes = 5
    };
}
