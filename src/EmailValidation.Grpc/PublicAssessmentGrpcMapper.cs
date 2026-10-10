using EmailValidation.Core;
using Google.Protobuf.WellKnownTypes;
namespace EmailValidation.Grpc;

public static class PublicAssessmentGrpcMapper
{
    public static Assessment.V1.PublicAssessment? Map(PublicValidationAssessment? assessment)
    {
        if (assessment is null) return null;
        var response = new Assessment.V1.PublicAssessment
        {
            SummaryStatus = assessment.SummaryStatus, ConfidenceType = assessment.ConfidenceType,
            HeuristicEvidenceStrength = assessment.HeuristicEvidenceStrength,
            EvidenceLevel = assessment.EvidenceLevel, RecipientBehavior = assessment.RecipientBehavior
        };
        if (assessment.MailboxEvidenceObservedAtUtc is { } mailbox) response.MailboxEvidenceObservedAtUtc = Timestamp.FromDateTimeOffset(mailbox);
        if (assessment.RoutingEvidenceObservedAtUtc is { } routing) response.RoutingEvidenceObservedAtUtc = Timestamp.FromDateTimeOffset(routing);
        if (assessment.MailboxEvidenceAgeSeconds is { } mailboxAge) response.MailboxEvidenceAgeSeconds = mailboxAge;
        if (assessment.RoutingEvidenceAgeSeconds is { } routingAge) response.RoutingEvidenceAgeSeconds = routingAge;
        if (assessment.Probability is { } p) response.Probability = new()
        {
            Value = p.Value, Target = p.Target, OutcomeWindowSeconds = p.OutcomeWindowSeconds,
            ModelVersion = p.ModelVersion, CalibrationVersion = p.CalibrationVersion,
            OutcomeDefinitionVersion = p.OutcomeDefinitionVersion, DecisionPolicyVersion = p.DecisionPolicyVersion,
            TrainingDatasetId = p.TrainingDatasetId, ArtifactChecksum = p.ArtifactChecksum,
            RolloutMode = p.RolloutMode, ScoredAtUtc = Timestamp.FromDateTimeOffset(p.ScoredAtUtc)
        };
        return response;
    }
}
