using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
namespace EmailValidation.IntegrationTests;

public sealed class MongoClassificationEvidenceTests
{
    [Fact]
    [Trait("Category", "MongoIntegration")]
    public async Task Outcomes_ConcurrentImmutableEventsAndTenantIsolationSurviveReload()
    {
        var connection=Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var database="ev10_classification_"+Guid.NewGuid().ToString("N");
        var client=new MongoClient(connection);
        var options=Options.Create(new EmailValidationOptions { Persistence=new() { Enabled=true,Provider="MongoDB",DatabaseName=database } });
        try
        {
            var first=new MongoClassificationEvidenceStore(client,options); var second=new MongoClassificationEvidenceStore(client,options);
            await first.InitializeAsync();
            var at=DateTimeOffset.UtcNow.AddDays(-1);
            var observation=new EmailDeliveryOutcomeObservation
            {
                OutcomeEventId="immutable",EmailCorrelationId="mailbox",ValidationId="validation",SnapshotId="snapshot",TenantId="tenant-a",
                Outcome=EmailDeliveryOutcome.MailboxConfirmed,Confidence=OutcomeConfidence.Authoritative,OutcomeSource="directory",
                SourceEventId="event",Provider=MailProvider.GoogleWorkspace,SendAttemptAtUtc=at,ObservedAtUtc=at,
                NormalizationVersion="authorized-outcome-v1",Cohort=EvidenceCohort.AuthorizedReal,AuthorizationReference="consent",SubmittedBy="admin"
            };
            var results=await Task.WhenAll(first.AppendAsync(observation),second.AppendAsync(observation with { Outcome=EmailDeliveryOutcome.MailboxAbsent }));
            Assert.Contains(AppendObservationResult.Inserted,results); Assert.Contains(AppendObservationResult.Conflict,results);
            var saved=Assert.Single(await first.QueryAsync(at.AddMinutes(-1),at.AddMinutes(1),tenantId:"tenant-a"));
            Assert.Equal(AppendObservationResult.Duplicate,await second.AppendAsync(saved));
            Assert.Equal(AppendObservationResult.Inserted,await second.AppendAsync(observation with { OutcomeEventId="tenant-b-event",TenantId="tenant-b" }));
            Assert.Single(await first.QueryAsync(at.AddMinutes(-1),at.AddMinutes(1),tenantId:"tenant-a"));
            Assert.Single(await second.QueryAsync(at.AddMinutes(-1),at.AddMinutes(1),tenantId:"tenant-b"));
        }
        finally { await client.DropDatabaseAsync(database); }
    }
}
