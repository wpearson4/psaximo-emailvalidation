using EmailValidation.Core;

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
            new SmtpProbeResult(SmtpMailboxStatus.Rejected, 550, "5.1.1", TimeSpan.Zero),
            [Control(priorControlAt), Target(priorControlAt.AddSeconds(10), SmtpResponseCategory.Accepted)]);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(CatchAllReasonCode.AcceptAllCandidate, result.ReasonCode);
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
            new SmtpProbeResult(SmtpMailboxStatus.Rejected, 550, "5.1.1", TimeSpan.Zero),
            []);

        Assert.Equal(DomainRecipientBehavior.Unknown, result.RecipientBehavior);
        Assert.Equal(CatchAllReasonCode.MixedOrInconclusive, result.ReasonCode);
        Assert.Equal(0, result.IndependentObservationCount);
        Assert.True(result.RefreshInconclusive);
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
        Assert.Equal(1, result.IndependentObservationCount);
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
        IndependentObservationCount = 1,
        ObservedAt = observedAt,
        RefreshInconclusive = true
    };

    private static SmtpProbeResult AcceptedTarget() =>
        new(SmtpMailboxStatus.Accepted, 250, "2.1.5", TimeSpan.Zero);

    private static ValidationObservation Control(DateTimeOffset observedAt) => new(
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
        RandomRecipientRejectedCount: 0);

    private static ValidationObservation Target(DateTimeOffset observedAt, SmtpResponseCategory category) => new(
        "example.test",
        ValidationObservationType.MailboxProbe,
        MailProvider.GenericSmtp,
        "mx.example.test",
        CatchAllStatus.Unknown,
        .75,
        category,
        observedAt,
        10);

    private static CatchAllOptions Options() => new()
    {
        MinimumAcceptedProbes = 2,
        AcceptAllMinimumIndependentObservations = 2,
        AcceptAllMinimumObservationSeparationMinutes = 15,
        AcceptAllSessionCorrelationMinutes = 5
    };
}
