using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmailValidation.Api;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Grpc;
using Microsoft.Extensions.DependencyInjection;

namespace EmailValidation.Api.Tests;

public sealed class PublicAssessmentContractTests : IClassFixture<EmailValidationApiFactory>
{
    private readonly EmailValidationApiFactory _factory;
    public PublicAssessmentContractTests(EmailValidationApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData(EmailValidationStatus.Unknown, "Inconclusive", ReasonCode.SmtpTimeout)]
    [InlineData(EmailValidationStatus.Unknown, "Inconclusive", ReasonCode.PolicyBlock)]
    [InlineData(EmailValidationStatus.CatchAll, "Risky", ReasonCode.CatchAllDetected)]
    [InlineData(EmailValidationStatus.LikelyInvalid, "Risky", ReasonCode.MailboxAcceptanceAmbiguous)]
    [InlineData(EmailValidationStatus.Invalid, "Invalid", ReasonCode.MailboxRejected)]
    [InlineData(EmailValidationStatus.Valid, "Valid", ReasonCode.MailboxAccepted)]
    public void RestGrpcCsv_AgreeOnSummaryAndHeuristicSemantics(EmailValidationStatus status, string summary, ReasonCode reason)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new EmailValidationResult
        {
            ValidationId="contract", Email="person@example.test", Status=status, Checks=new(), Confidence=0.8,
            ResultState=ValidationResultState.Final, MailboxEvidenceObservedAt=now.AddMinutes(-3),
            ReasonCodes=[reason], ConfidenceLevel=ConfidenceLevel.Low
        };
        var rest = ApiContractMapper.Map(result);
        var grpc = PublicAssessmentGrpcMapper.Map(rest.Assessment)!;
        var csv = PublicAssessmentCsv.Headers.Zip(PublicAssessmentCsv.Values(rest.Assessment)).ToDictionary(p=>p.First,p=>p.Second);
        Assert.Equal(summary, rest.Assessment!.SummaryStatus);
        Assert.Equal(summary, grpc.SummaryStatus); Assert.Equal(summary, csv["Summary Status"]);
        Assert.Equal("Heuristic", grpc.ConfidenceType); Assert.Equal("Heuristic", csv["Score Type"]);
        Assert.Null(rest.Assessment.Probability); Assert.Null(grpc.Probability); Assert.Equal("", csv["Probability"]);
        Assert.Equal(0.8, rest.Confidence); Assert.True(rest.Assessment.MailboxEvidenceAgeSeconds >= 180);
        Assert.Null(rest.Assessment.RoutingEvidenceAgeSeconds);
        var lifecycle = new ValidationLifecycle
        {
            ValidationId="contract", NormalizedEmail=result.Email, Request=new(), CurrentResult=result,
            ResultState=ValidationResultState.Final, AttemptNumber=1, MaximumAttempts=1, FirstValidatedAt=now,
            LastValidatedAt=now, LifecycleState=ValidationLifecycleState.Final, Version=1
        };
        var snapshot = ValidationStatusMapper.ToSnapshot(lifecycle);
        Assert.Equal(summary, ApiContractMapper.Map(snapshot).Assessment!.SummaryStatus);
        Assert.Equal(summary, ValidationStatusGrpcMapper.Map(snapshot,now).Assessment.SummaryStatus);
    }

    [Theory]
    [InlineData(ModelRolloutMode.Enforced, PredictionDisposition.AcceptedPrediction, true)]
    [InlineData(ModelRolloutMode.Advisory, PredictionDisposition.AcceptedPrediction, true)]
    [InlineData(ModelRolloutMode.Shadow, PredictionDisposition.AcceptedPrediction, false)]
    [InlineData(ModelRolloutMode.Enforced, PredictionDisposition.InsufficientSupport, false)]
    public void ModelProbability_HasTargetProvenanceAndSurvivesPersistence(ModelRolloutMode mode, PredictionDisposition disposition, bool published)
    {
        var now = DateTimeOffset.UtcNow;
        var prediction = new EmailValidationPrediction
        {
            HeuristicEvidenceStrength=0.6, MailboxExistenceProbability=0.1,
            Decision=new(EmailValidationStatus.LikelyInvalid,"model"), Uncertainty=new(disposition,"test"),
            Model=new("baseline","model-v1",EvidenceBackedClassificationVersions.FeatureSchemaV2,"platt-v1",
                EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV3,"policy-v1",now.AddDays(-10),"dataset-1",new string('a',64),now,mode)
        };
        var result = new EmailValidationResult
        {
            ValidationId="model", Email="person@example.test", Status=EmailValidationStatus.LikelyInvalid, Checks=new(),
            Confidence=0.9, OriginalHeuristicEvidenceStrength=0.6, ConfidenceType=ConfidenceType.CalibratedProbability,
            Prediction=prediction, ProbabilityAssessment=PublicProbabilityAssessment.From(prediction)
        };
        var persisted = JsonSerializer.Deserialize<EmailValidationResult>(JsonSerializer.Serialize(result))!;
        var rest = ApiContractMapper.Map(persisted).Assessment!;
        var grpc = PublicAssessmentGrpcMapper.Map(rest)!;
        Assert.Equal(0.6, rest.HeuristicEvidenceStrength);
        Assert.Equal("CalibratedProbability", rest.ConfidenceType);
        Assert.Equal(published, rest.Probability is not null);
        Assert.Equal(published, grpc.Probability is not null);
        if (published)
        {
            Assert.Equal(0.1, rest.Probability!.Value); Assert.Equal("MailboxExistence", rest.Probability.Target);
            Assert.Equal(0, rest.Probability.OutcomeWindowSeconds); Assert.Equal("platt-v1", grpc.Probability!.CalibrationVersion);
            Assert.Equal("dataset-1", grpc.Probability!.TrainingDatasetId);
        }
    }

    [Fact]
    public async Task OutcomeEndpoint_RequiresAdminAndRejectsUnknownTenantSnapshot()
    {
        var request = new AuthorizedOutcomeImport("missing",DateTimeOffset.UtcNow.AddDays(-10),"directory","event","consent",
            EvidenceCohort.AuthorizedReal,OutcomeTruthSource.ManagedDirectory,EmailDeliveryOutcome.MailboxConfirmed,
            DateTimeOffset.UtcNow.AddDays(-9),DateTimeOffset.UtcNow.AddDays(-8));
        using var anonymous=_factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PostAsJsonAsync("/v1/validation-outcomes",request)).StatusCode);
        using var reader=_factory.CreateAuthenticatedClient([EmailValidationScopes.Read]);
        Assert.Equal(HttpStatusCode.Forbidden,(await reader.PostAsJsonAsync("/v1/validation-outcomes",request)).StatusCode);
        using var admin=_factory.CreateAuthenticatedClient([EmailValidationScopes.Admin]);
        Assert.Equal(HttpStatusCode.NotFound,(await admin.PostAsJsonAsync("/v1/validation-outcomes",request)).StatusCode);
        await _factory.Services.GetRequiredService<IEmailValidationFeatureSnapshotStore>().AppendAsync(new()
        {
            SnapshotId=request.SnapshotId, SnapshotAtUtc=request.SnapshotAtUtc, TenantId="tenant-a",
            ValidationId="api-import",EmailCorrelationId="api-mailbox",DomainCorrelationId="api-domain",
            FeatureSchemaVersion=EvidenceBackedClassificationVersions.FeatureSchemaV2,
            Syntax=new(true,true,false,false,false,false),
            Domain=new(true,DnsStatus.Success,true,false,1,false,MailProvider.GoogleWorkspace,0.8,DnsSecurityState.Unknown,
                AuthenticationRecordState.Unknown,AuthenticationRecordState.Unknown,CatchAllStatus.Unknown,0,"mx"),
            Smtp=new(SmtpProbeDisposition.NotAttempted,null,null,null,SmtpResponseCategory.NotAttempted,null,false,false,false,false,false,false),
            History=new(0,0,0,0,VerificationReliabilityLevel.Unknown,0,0,0,0,0),
            Operational=new(ValidationResultSource.LiveValidation,1,EvidenceQuality.Partial,false,null,null),
            HeuristicEvidenceStrength=0.5,HeuristicStatus=EmailValidationStatus.Unknown
        });
        Assert.Equal(HttpStatusCode.OK,(await admin.PostAsJsonAsync("/v1/validation-outcomes",request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await admin.PostAsJsonAsync("/v1/validation-outcomes",request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await admin.PostAsJsonAsync("/v1/validation-outcomes",request with { Outcome=EmailDeliveryOutcome.MailboxAbsent })).StatusCode);
        using var other=_factory.CreateAuthenticatedClient([EmailValidationScopes.Admin],tenant:"tenant-b");
        Assert.Equal(HttpStatusCode.NotFound,(await other.PostAsJsonAsync("/v1/validation-outcomes",request)).StatusCode);
    }
}
