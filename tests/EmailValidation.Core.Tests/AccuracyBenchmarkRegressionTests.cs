using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class AccuracyBenchmarkRegressionTests
{
    internal static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly CurrentConsumer Admin = new("admin", "tenant-a", new HashSet<string> { EmailValidationScopes.Admin });

    [Fact]
    public async Task AuthorizedImport_BindsTenantAndSnapshot_RejectsFalseTruth_AndIsIdempotent()
    {
        using var store = Store(); using var metrics = new ClassificationFoundationMetrics();
        var snapshot = Snapshot("s", "d", 0);
        await ((IEmailValidationFeatureSnapshotStore)store).AppendAsync(snapshot);
        var importer = new AuthorizedOutcomeImporter(store, new EmailDeliveryOutcomeIngestionService(store, metrics), TimeProvider.System);
        var input = new AuthorizedOutcomeImport("s", Start, "directory", "event-1", "consent-1",
            EvidenceCohort.AuthorizedReal, OutcomeTruthSource.ManagedDirectory, EmailDeliveryOutcome.MailboxConfirmed,
            Start.AddHours(1), Start.AddHours(2));
        Assert.Equal(AppendObservationResult.Inserted, (await importer.ImportAsync(input, Admin)).Status);
        Assert.Equal(AppendObservationResult.Duplicate, (await importer.ImportAsync(input, Admin)).Status);
        Assert.Equal(AppendObservationResult.Conflict, (await importer.ImportAsync(input with { Outcome = EmailDeliveryOutcome.MailboxAbsent }, Admin)).Status);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => importer.ImportAsync(input, Admin with { TenantId = "tenant-b" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => importer.ImportAsync(input, Admin with { Scopes = new HashSet<string>() }));
        await Assert.ThrowsAsync<ArgumentException>(() => importer.ImportAsync(input with { TruthSource = OutcomeTruthSource.DeliveryEvent }, Admin));
        await Assert.ThrowsAsync<ArgumentException>(() => importer.ImportAsync(input with { Cohort = EvidenceCohort.Synthetic }, Admin));
        var rows = await ((IEmailDeliveryOutcomeObservationStore)store).QueryAsync(Start, Start.AddDays(10), "tenant-a");
        Assert.Single(rows);
        Assert.Equal(snapshot.EmailCorrelationId, rows[0].EmailCorrelationId);
        Assert.Equal(snapshot.ValidationId, rows[0].ValidationId);
    }

    [Fact]
    public async Task V3Labels_DoNotTurnAcceptanceMissingPolicyBounceOrImmatureEvidenceIntoMailboxTruth()
    {
        using var store = Store(); using var metrics = new ClassificationFoundationMetrics();
        var snapshots = Enumerable.Range(0, 8).Select(i => Snapshot("s"+i, "d"+i, 0)).ToArray();
        foreach (var s in snapshots) await ((IEmailValidationFeatureSnapshotStore)store).AppendAsync(s);
        var outcomes = new[] {
            Outcome(snapshots[0], EmailDeliveryOutcome.Delivered),
            Outcome(snapshots[1], EmailDeliveryOutcome.MailboxConfirmed),
            Outcome(snapshots[2], EmailDeliveryOutcome.HardBounce) with { EnhancedStatusCode = "5.7.1" },
            Outcome(snapshots[3], EmailDeliveryOutcome.HardBounce) with { EnhancedStatusCode = "5.1.1" },
            Outcome(snapshots[4], EmailDeliveryOutcome.MailboxConfirmed) with { Cohort = EvidenceCohort.AuthorizedReal },
            Outcome(snapshots[5], EmailDeliveryOutcome.MailboxConfirmed),
            Outcome(snapshots[5], EmailDeliveryOutcome.MailboxAbsent) with { OutcomeEventId = "conflicting" },
            Outcome(snapshots[6], EmailDeliveryOutcome.MailboxConfirmed) with { SendAttemptAtUtc = Start.AddDays(12), ObservedAtUtc = Start.AddDays(12) }
        };
        foreach (var o in outcomes.Reverse()) await store.AppendAsync(o);
        var builder = new TrainingDatasetBuilder(store, store, new OutcomeDefinitionCatalog(), metrics, TimeProvider.System);
        var dataset = await builder.BuildAsync(Request() with { MaturationCutoffUtc = Start.AddDays(15) });
        Assert.Equal(2, dataset.Rows.Count);
        Assert.Single(dataset.Rows, r => r.Label == BinaryOutcomeLabel.Positive);
        Assert.Single(dataset.Rows, r => r.Label == BinaryOutcomeLabel.Negative);
        Assert.Equal(2, dataset.Manifest.ExcludedCount);
        Assert.Equal(1, dataset.Manifest.RightCensoredCount);
        Assert.Equal(3, dataset.Manifest.UnresolvedCount);
        Assert.Equal(dataset.Manifest.DatasetHash, (await builder.BuildAsync(Request() with { MaturationCutoffUtc = Start.AddDays(15) })).Manifest.DatasetHash);
    }

    [Fact]
    public async Task FrozenBenchmark_IsReproducible_HoldsOutDomains_AndDistinguishesUnestimableRates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ev10-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var snapshots = Enumerable.Range(0, 40).Select(i => Snapshot("s"+i, "domain-"+(i%10), i < 10 ? 0 : i < 20 ? 12 : 24)
                with { HeuristicStatus = i%2 == 0 ? EmailValidationStatus.Valid : EmailValidationStatus.Unknown }).ToArray();
            var outcomes = snapshots.Select((s,i) => Outcome(s, i%2 == 0 ? EmailDeliveryOutcome.MailboxConfirmed : EmailDeliveryOutcome.MailboxAbsent)).ToArray();
            var bundle = new BenchmarkEvidenceBundle(Request() with { EndUtc = Start.AddDays(30), MaturationCutoffUtc = Start.AddDays(40) },
                Start.AddDays(10), Start.AddDays(22), new("strict-v1", BootstrapSamples: 100), snapshots, outcomes);
            var input = Path.Combine(dir, "input.json"); var output = Path.Combine(dir, "report.json");
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(bundle, OfflineAccuracyBenchmark.JsonOptions));
            var report = await OfflineAccuracyBenchmark.RunAsync(input, output);
            var first = await File.ReadAllTextAsync(output);
            await OfflineAccuracyBenchmark.RunAsync(input, output);
            Assert.Equal(first, await File.ReadAllTextAsync(output));
            Assert.Equal(EvidenceCohort.Synthetic, report.Cohort);
            Assert.True(report.OutOfTime.Candidate.TruthPositive > 0);
            Assert.Equal(0, report.OutOfTime.Candidate.FalsePositiveRate);
            Assert.Null(report.OutOfTime.Candidate.FalseInvalidRate); // no negative predictions
            Assert.Equal(0, report.OutOfTime.FalsePositiveRateChange.Upper);
            Assert.True(report.UnseenDomain.Candidate.Total > 0);
            Assert.True(report.TrainingRows > 0); Assert.True(report.CalibrationRows > 0);
            var dataset = JsonSerializer.Deserialize<TrainingDataset>(await File.ReadAllTextAsync(output+".dataset.json"), OfflineAccuracyBenchmark.JsonOptions)!;
            var split = LeakageSafeDatasetSplitter.Split(dataset.Rows, bundle.CalibrationStartsUtc, bundle.TestStartsUtc);
            Assert.Empty(split.Training.Select(r=>r.DomainCorrelationId).Intersect(split.UnseenDomainTest.Select(r=>r.DomainCorrelationId)));
            Assert.Null(AccuracyBenchmark.Measure([], null, false).FalsePositiveRate);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ConfusionRates_UseTruthDenominators_AndCountAbstentions()
    {
        var rows = new[] { Row("tp", true, EmailValidationStatus.Valid), Row("fp", false, EmailValidationStatus.Valid),
            Row("fn", true, EmailValidationStatus.Invalid), Row("tn", false, EmailValidationStatus.Invalid),
            Row("ap", true, EmailValidationStatus.Unknown), Row("an", false, EmailValidationStatus.LikelyInvalid) };
        var metrics = AccuracyBenchmark.Measure(rows, null, false);
        Assert.Equal(1d/3, metrics.FalsePositiveRate); Assert.Equal(1d/3, metrics.FalseNegativeRate);
        Assert.Equal(0.5, metrics.Precision); Assert.Equal(1d/3, metrics.Recall); Assert.Equal(2d/3, metrics.Coverage);
        Assert.Equal(1, metrics.AbstainedPositive); Assert.Equal(1, metrics.AbstainedNegative);
    }

    [Theory]
    [InlineData(ModelRolloutMode.Advisory)]
    [InlineData(ModelRolloutMode.Enforced)]
    public void Runtime_RefusesChecksumOnlyPromotion(ModelRolloutMode mode)
    {
        var path = Path.GetTempFileName();
        try
        {
            var artifact = new LogisticRegressionArtifact
            {
                ModelName="synthetic", ModelVersion="v1", Target=PredictionTargetKind.MailboxExistence,
                FeatureSchemaVersion=EvidenceBackedClassificationVersions.FeatureSchemaV2, CalibrationVersion="platt-v1",
                OutcomeDefinitionVersion=EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV3,
                TrainingDatasetId="dataset-test", TrainingDataCutoffUtc=Start,
                Intercept=0, Coefficients=LogisticFeatureEncoder.SupportedFeatures.ToDictionary(k=>k,_=>0d),
                CalibrationSlope=1, CalibrationIntercept=0, L2Regularization=0.01, RandomSeed=17
            };
            File.WriteAllText(path, JsonSerializer.Serialize(artifact));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var options = Options.Create(new EmailValidationOptions { ClassificationModel = new() { Mode=mode, ArtifactPath=path, ArtifactChecksum=hash } });
            Assert.Throws<InvalidDataException>(()=>new LogisticRegressionArtifactProvider(options).Get());
        }
        finally { File.Delete(path); }
    }

    private static TrainingDatasetRequest Request() => new(PredictionTargetKind.MailboxExistence,
        EvidenceBackedClassificationVersions.MailboxExistenceOutcomeV3, EvidenceBackedClassificationVersions.FeatureSchemaV2,
        Start, Start.AddDays(1), Start.AddDays(15), TenantId:"tenant-a") { Cohort=EvidenceCohort.Synthetic };
    private static LocalClassificationEvidenceStore Store() => new(Options.Create(new EmailValidationOptions { Persistence = new() { Enabled = false } }));
    private static TrainingDatasetRow Row(string id, bool positive, EmailValidationStatus status) => new(id,id,"domain-"+id,Start,
        Snapshot(id,"domain-"+id,0) with { HeuristicStatus=status }, positive ? BinaryOutcomeLabel.Positive : BinaryOutcomeLabel.Negative,
        "outcome-"+id, Start.AddDays(1), OutcomeConfidence.Authoritative);
    internal static EmailDeliveryOutcomeObservation Outcome(EmailValidationFeatureSnapshot s, EmailDeliveryOutcome outcome) => new()
    {
        OutcomeEventId="outcome-"+s.SnapshotId, SnapshotId=s.SnapshotId, ValidationId=s.ValidationId,
        EmailCorrelationId=s.EmailCorrelationId, TenantId=s.TenantId, Outcome=outcome, Confidence=OutcomeConfidence.Authoritative,
        OutcomeSource="fixture", Provider=s.Domain.Provider, SendAttemptAtUtc=s.SnapshotAtUtc.AddHours(1),
        ObservedAtUtc=s.SnapshotAtUtc.AddHours(2), NormalizationVersion="authorized-outcome-v1", Cohort=EvidenceCohort.Synthetic,
        AuthorizationReference="synthetic-fixture", SubmittedBy="test"
    };
    internal static EmailValidationFeatureSnapshot Snapshot(string id,string domain,int day) => new()
    {
        SnapshotId=id, ValidationId="validation-"+id, EmailCorrelationId="mailbox-"+id, DomainCorrelationId=domain, TenantId="tenant-a",
        SnapshotAtUtc=Start.AddDays(day), FeatureSchemaVersion=EvidenceBackedClassificationVersions.FeatureSchemaV2,
        Syntax=new(true,true,false,false,false,false),
        Domain=new(true,DnsStatus.Success,true,false,1,false,MailProvider.GoogleWorkspace,0.8,DnsSecurityState.Unknown,
            AuthenticationRecordState.Unknown,AuthenticationRecordState.Unknown,CatchAllStatus.NotCatchAll,0.8,"mx")
            { RecipientBehavior=DomainRecipientBehavior.RecipientSpecific },
        Smtp=new(SmtpProbeDisposition.NotAttempted,null,null,null,SmtpResponseCategory.NotAttempted,null,false,false,false,false,false,false),
        History=new(0,0,0,0,VerificationReliabilityLevel.Unknown,0,0,0,0,0),
        Operational=new(ValidationResultSource.LiveValidation,1,EvidenceQuality.Partial,false,null,null),
        HeuristicEvidenceStrength=0.7, HeuristicStatus=EmailValidationStatus.Unknown,
        PolicyVersions=new("test-engine","test-rules","test-confidence","test-provider")
    };
}
