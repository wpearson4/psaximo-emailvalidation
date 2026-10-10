using System.Security.Cryptography;
using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;
using static EmailValidation.Core.Tests.AccuracyBenchmarkRegressionTests;
namespace EmailValidation.Core.Tests;

public sealed class ModelReleaseGateTests
{
    [Fact]
    public async Task Release_RequiresMatchingRealHoldoutsCalibrationApprovalAndProviderSupport()
    {
        var dir=Path.Combine(Path.GetTempPath(),"release-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            using var store=new LocalClassificationEvidenceStore(Options.Create(new EmailValidationOptions { Persistence=new() { Enabled=false } }));
            using var metrics=new ClassificationFoundationMetrics();
            var snapshots=Enumerable.Range(0,60).Select(i=>Snapshot("s"+i,"d"+(i%10),i<20?0:i<40?12:24)
                with { HeuristicStatus=i%2==0?EmailValidationStatus.Valid:EmailValidationStatus.Invalid }).ToArray();
            foreach(var s in snapshots)
            {
                await ((IEmailValidationFeatureSnapshotStore)store).AppendAsync(s);
                await store.AppendAsync(Outcome(s,s.HeuristicStatus==EmailValidationStatus.Valid?EmailDeliveryOutcome.MailboxConfirmed:EmailDeliveryOutcome.MailboxAbsent)
                    with { Cohort=EvidenceCohort.AuthorizedReal });
            }
            var dataset=await new TrainingDatasetBuilder(store,store,new OutcomeDefinitionCatalog(),metrics,TimeProvider.System).BuildAsync(
                new(PredictionTargetKind.MailboxExistence,EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV3,
                    EvidenceBackedClassificationVersions.FeatureSchemaV2,Start,Start.AddDays(30),Start.AddDays(40),TenantId:"tenant-a") { Cohort=EvidenceCohort.AuthorizedReal });
            var artifact=new LogisticRegressionArtifact
            {
                ModelName="test-only",ModelVersion="1",Target=PredictionTargetKind.MailboxExistence,
                FeatureSchemaVersion=EvidenceBackedClassificationVersions.FeatureSchemaV2,CalibrationVersion="cal-v1",
                OutcomeDefinitionVersion=EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV3,TrainingDataCutoffUtc=Start.AddDays(8),
                TrainingDatasetId=dataset.Manifest.DatasetId,Intercept=0,Coefficients=LogisticFeatureEncoder.SupportedFeatures.ToDictionary(k=>k,_=>0d),
                CalibrationSlope=1,CalibrationIntercept=0,L2Regularization=0.01,RandomSeed=17,SupportedProviders=[MailProvider.GoogleWorkspace]
            };
            var modelHash=Write(dir,"model.json",artifact);
            var options=new ClassificationModelOptions { Mode=ModelRolloutMode.Enforced,ArtifactPath=Path.Combine(dir,"model.json"),ArtifactChecksum=modelHash };
            var predictions=dataset.Rows.Select(r=>new BenchmarkPrediction(r.SnapshotId,r.Snapshot.HeuristicStatus,r.Label==BinaryOutcomeLabel.Positive?0.9:0.1)).ToArray();
            var report=AccuracyBenchmark.Evaluate(dataset,predictions,new("strict-v1",BootstrapSamples:100),Start.AddDays(10),Start.AddDays(22),modelHash,new string('c',64))
                with { ModelPolicyHash=ModelReleaseGate.PolicyHash(options) };
            var split=LeakageSafeDatasetSplitter.Split(dataset.Rows,Start.AddDays(10),Start.AddDays(22));
            var calibration=new CalibrationEvidence(dataset.Manifest.DatasetId,"cal-v1",split.Calibration.Select(r=>r.SnapshotId).ToArray(),split.Training.Select(r=>r.SnapshotId).ToArray());
            var approval=new ModelReleaseApproval("test-approval","test-reviewer",DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1),
                modelHash,"dataset.json",Write(dir,"dataset.json",dataset),"evaluation.json",Write(dir,"evaluation.json",report),
                "calibration.json",Write(dir,"calibration.json",calibration),options.DecisionPolicyVersion,[MailProvider.GoogleWorkspace],1,1,0,0,0,new string('b',64));
            options.ReleaseApprovalPath=Path.Combine(dir,"approval.json");
            options.ReleaseApprovalChecksum=Write(dir,"approval.json",approval);
            Assert.Equal("test-approval",ModelReleaseGate.Validate(options,artifact,modelHash).ApprovalId);
            var runtime=new LogisticRegressionArtifactProvider(Options.Create(new EmailValidationOptions { ClassificationModel=options }));
            Assert.Equal(MailProvider.GoogleWorkspace,Assert.Single(runtime.Get().Artifact.SupportedProviders));

            void Rejected(ModelReleaseApproval changed)
            {
                options.ReleaseApprovalChecksum=Write(dir,"approval.json",changed);
                Assert.Throws<InvalidDataException>(()=>ModelReleaseGate.Validate(options,artifact,modelHash));
            }
            Rejected(approval with { ExpiresAtUtc=DateTimeOffset.UtcNow.AddMinutes(-1) });
            Rejected(approval with { SupportedProviders=[MailProvider.Yahoo] });
            Rejected(approval with { MinimumPositivePerProvider=10000 });
            Rejected(approval with { CalibrationChecksum=Write(dir,"calibration.json",calibration with { SnapshotIds=[split.OutOfTimeTest[0].SnapshotId] }) });
            Write(dir,"calibration.json",calibration);
            Rejected(approval with { DatasetChecksum=Write(dir,"dataset.json",dataset with { Manifest=dataset.Manifest with { Cohort=EvidenceCohort.Synthetic } }) });
            Write(dir,"dataset.json",dataset);
            options.ReleaseApprovalChecksum=Write(dir,"approval.json",approval);
            options.LikelyValidThreshold=0.95;
            Assert.Throws<InvalidDataException>(()=>ModelReleaseGate.Validate(options,artifact,modelHash));
        }
        finally { Directory.Delete(dir,true); }
    }

    [Fact]
    public void UnapprovedProvider_AbstainsEvenWithExtremeProbability()
    {
        var options=Options.Create(new EmailValidationOptions());
        var model=new PredictionModelMetadata("test","1",EvidenceBackedClassificationVersions.FeatureSchemaV2,"cal-v1",
            EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV3,"policy",Start,"data","hash",Start,ModelRolloutMode.Enforced)
            { SupportedProviders=[MailProvider.Yahoo] };
        var uncertainty=new TransparentPredictionUncertaintyPolicy(options).Evaluate(new(PredictionTargetKind.MailboxExistence,0.999,model),Snapshot("s","d",0));
        Assert.Equal(PredictionDisposition.InsufficientSupport,uncertainty.Disposition);
    }
    private static string Write<T>(string directory,string name,T value)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value,OfflineAccuracyBenchmark.JsonOptions);
        File.WriteAllBytes(Path.Combine(directory,name),bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
