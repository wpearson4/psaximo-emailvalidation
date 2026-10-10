using System.Text.Json.Nodes;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EmailValidation.Core.Tests;

public sealed class MongoControlEvidenceRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const string Raw = "private-recipient@example.test raw SMTP text";

    [Fact]
    public void InconclusiveControls_PreserveTheirActualFailureAndRefreshBackoff()
    {
        var settings = Settings(false);
        var domain = Domain(settings, false) with
        {
            CatchAll = new(CatchAllStatus.Unknown, 1, 0, 0, 1)
            {
                ProbeResults = [new(SmtpMailboxStatus.TemporaryFailure, 451, Raw, TimeSpan.Zero)],
                ObservedAt = Now.AddMinutes(-1), RefreshAttemptedAt = Now, RefreshInconclusive = true
            }
        };
        var restored = MongoValidationIntelligenceStore.DomainIntelligenceDocument.FromModel(domain).ToModel()!;
        Assert.Equal(CatchAllStatus.Unknown, restored.CatchAll.Status);
        Assert.Equal(SmtpMailboxStatus.TemporaryFailure, Assert.Single(restored.CatchAll.ProbeResults).Status);
        Assert.Equal(Now, restored.CatchAll.RefreshAttemptedAt);
        Assert.Equal(domain.CatchAll.ObservedAt, restored.CatchAll.ObservedAt);
        Assert.Null(restored.CatchAll.ProbeResults[0].Response);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BsonRoundTrip_PreservesValidationDecisionsWithoutTranscripts(bool recipientSpecific, bool shadow)
    {
        var settings = Settings(recipientSpecific, shadow);
        var original = Domain(settings, recipientSpecific);
        var document = MongoValidationIntelligenceStore.DomainIntelligenceDocument.FromModel(original);
        var restored = BsonSerializer.Deserialize<MongoValidationIntelligenceStore.DomainIntelligenceDocument>(document.ToBson()).ToModel()!;
        Assert.DoesNotContain(Raw, document.PayloadJson);
        Assert.DoesNotContain("sender@validator.test", document.PayloadJson);
        Assert.DoesNotContain("private-identity", document.PayloadJson);
        Assert.All(restored.CatchAll.ProbeResults, probe =>
        {
            Assert.Null(probe.Response);
            Assert.Empty(probe.SessionHistory);
            Assert.Null(probe.Evidence!.SanitizedResponse);
            Assert.Null(probe.SessionEvidence!.ServerBanner);
            Assert.Empty(probe.SessionEvidence.ProbeSender);
            Assert.All(probe.SessionEvidence.Stages, stage => Assert.Null(stage.SanitizedResponse));
        });
        Assert.Equal(original.CatchAll.ControlScope, restored.CatchAll.ControlScope);
        Assert.Equal(original.CatchAll.ObservedAt, restored.CatchAll.ObservedAt);
        Assert.Equal(original.CatchAll.EvidenceExpiresAt, restored.CatchAll.EvidenceExpiresAt);
        Assert.Equal(original.CatchAll.ProbeResults[0].Evidence!.Timestamp, restored.CatchAll.ProbeResults[0].Evidence!.Timestamp);
        var before = await Validate(settings, new Cache(original), recipientSpecific);
        var after = await Validate(settings, new Cache(restored), recipientSpecific);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.MailboxEvidenceObservedAt, after.MailboxEvidenceObservedAt);
        Assert.Equal(before.ProviderCapabilities!.WouldSkipMailbox, after.ProviderCapabilities!.WouldSkipMailbox);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("session")]
    [InlineData("mail_from")]
    [InlineData("timestamp")]
    [InlineData("structured_only")]
    public async Task OldOrIncompleteDocuments_RefreshInsteadOfInferringRecipientEvidence(string fault)
    {
        var settings = Settings(false);
        var domain = Domain(settings, false);
        var document = MongoValidationIntelligenceStore.DomainIntelligenceDocument.FromModel(domain);
        var payload = JsonNode.Parse(document.PayloadJson!)!;
        var probes = payload["catchAll"]!["probeResults"]!.AsArray();
        switch (fault)
        {
            case "missing": probes.Clear(); break;
            case "partial": probes.RemoveAt(1); break;
            case "session": probes[0]!["sessionEvidence"] = null; break;
            case "mail_from": probes[0]!["sessionEvidence"]!["stages"]![0]!["responseCode"] = 550; break;
            case "timestamp": probes[0]!["evidence"] = null; break;
        }
        document.PayloadJson = fault == "structured_only" ? null : payload.ToJsonString();
        var restored = document.ToModel()!;
        Assert.False(restored.CatchAll.HasConfirmedAcceptAllEvidence);
        Assert.Empty(restored.CatchAll.ProbeResults);
        Assert.Equal(domain.CatchAll.ObservedAt, restored.CatchAll.ObservedAt);
        var smtp = new Smtp();
        var controls = new Controls();
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: smtp, catchAll: controls, cache: new Cache(restored), clock: new Clock())
            .ValidateAsync("target@example.test", new(true));
        Assert.Equal(1, controls.Calls);
        Assert.Equal(1, smtp.Calls);
        Assert.DoesNotContain(ReasonCode.NonDiscriminationEvidenceReused, result.ReasonCodes);
        Assert.Equal(Now, result.MailboxEvidenceObservedAt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "MongoIntegration")]
    public async Task MongoStore_NewInstancesAndRepeatedValidationPreserveControlEvidence(bool recipientSpecific, bool shadow)
    {
        var connection = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var settings = Settings(recipientSpecific, shadow);
        settings.Persistence.Enabled = true;
        settings.Persistence.Provider = "MongoDB";
        settings.Persistence.DatabaseName = "ev_control_roundtrip_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(connection);
        MongoValidationIntelligenceStore Store() => new(client, Options.Create(settings),
            new ValidationPersistenceMetrics(), NullLogger<MongoValidationIntelligenceStore>.Instance);
        try
        {
            var writer = Store();
            await writer.InitializeAsync();
            await writer.SaveDomainAsync(Domain(settings, recipientSpecific));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // A new store/cache/validator on each pass prevents an in-memory copy
                // from hiding Mongo serialization loss, including writes by validation.
                var reader = Store();
                await Validate(settings, new Cache(await reader.GetDomainAsync("example.test"), reader), recipientSpecific);
                var saved = await Store().GetDomainAsync("example.test");
                Assert.Equal(2, saved!.CatchAll.ProbeResults.Count);
                Assert.All(saved.CatchAll.ProbeResults, probe => Assert.NotNull(probe.Evidence));
            }
            var raw = await client.GetDatabase(settings.Persistence.DatabaseName)
                .GetCollection<BsonDocument>(settings.Persistence.DomainCollection).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
            Assert.DoesNotContain(Raw, raw.ToJson());
            Assert.DoesNotContain("sender@validator.test", raw.ToJson());
        }
        finally { await client.DropDatabaseAsync(settings.Persistence.DatabaseName); }
    }

    private static async Task<EmailValidationResult> Validate(EmailValidationOptions settings, Cache cache, bool recipientSpecific)
    {
        var smtp = new Smtp();
        var controls = new Controls();
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: smtp, catchAll: controls, cache: cache, clock: new Clock())
            .ValidateAsync("target@example.test", new(true));
        Assert.Equal(recipientSpecific ? EmailValidationStatus.Valid : EmailValidationStatus.Unknown, result.Status);
        var shadow = settings.ProviderCapabilities.Mode == ProviderCapabilityMode.Shadow;
        Assert.Equal(recipientSpecific || shadow ? 1 : 0, smtp.Calls);
        Assert.Equal(0, controls.Calls);
        if (recipientSpecific)
            Assert.True(result.RecipientEvidence!.Qualified);
        else if (shadow)
        {
            Assert.True(result.ProviderCapabilities!.WouldSkipMailbox);
            Assert.False(result.ProviderCapabilities.Applied);
            Assert.False(result.ProviderCapabilities.ShadowStatusDisagrees);
            Assert.DoesNotContain(ReasonCode.NonDiscriminationEvidenceReused, result.ReasonCodes);
        }
        else
        {
            Assert.Contains(ReasonCode.NonDiscriminationEvidenceReused, result.ReasonCodes);
            Assert.Null(result.MailboxEvidenceObservedAt);
            Assert.Equal(UnknownCause.NonDiscriminatingSmtpEndpoint, result.UnknownContext!.Cause);
        }
        return result;
    }

    private static EmailValidationOptions Settings(bool recipientSpecific, bool shadow = false)
    {
        var settings = new EmailValidationOptions();
        settings.Smtp.Enabled = true;
        settings.CatchAll.Enabled = true;
        settings.ProviderCapabilities.Mode = recipientSpecific || shadow ? ProviderCapabilityMode.Shadow : ProviderCapabilityMode.Enforced;
        settings.ProviderCapabilities.CanaryProviders = ["Microsoft365"];
        settings.ProviderCapabilities.ApprovalReference = "synthetic-only";
        settings.ProviderCapabilities.ApprovedPolicyHash = ProviderCapabilityPolicy.Fingerprint(settings.ProviderCapabilities);
        return settings;
    }

    private static DomainIntelligence Domain(EmailValidationOptions settings, bool recipientSpecific)
    {
        var version = ProviderCapabilityPolicy.StrategyVersion(settings);
        var observed = Now.AddMinutes(-1);
        var domain = new DomainIntelligence
        {
            Domain = "example.test", DomainExists = true,
            Dns = new(DnsStatus.Success, true, [new(10, "mx.example.test")], false, TimeSpan.Zero),
            Provider = new(MailProvider.Microsoft365, .99, TopologyFingerprint: "topology"),
            ObservedAt = observed, EvidenceExpiresAt = Now.AddHours(1),
            RoutingEvidence = new(observed, Now.AddHours(1)),
            StrategyVersion = version, IntelligencePolicyVersion = settings.DomainIntelligence.PolicyVersion
        };
        return domain with { CatchAll = new(recipientSpecific ? CatchAllStatus.NotCatchAll : CatchAllStatus.Unknown,
            2, recipientSpecific ? 0 : 2, recipientSpecific ? 2 : 0, 0, Confidence: .96)
        {
            RecipientBehavior = recipientSpecific ? DomainRecipientBehavior.RecipientSpecific : DomainRecipientBehavior.AcceptAll,
            ReasonCode = recipientSpecific ? CatchAllReasonCode.RandomRecipientsRejected : CatchAllReasonCode.AcceptAllConfirmed,
            IndependentObservationCount = 2, ObservedAt = observed, EvidenceExpiresAt = Now.AddHours(1),
            StrategyVersion = version, ProbeResults = [Probe(recipientSpecific, observed), Probe(recipientSpecific, observed)],
            EvidenceContractVersion = CatchAllDetectionResult.CurrentRecipientBehaviorEvidenceContractVersion,
            ControlScope = new("mx.example.test", 10, MailProvider.Microsoft365, domain.Provider.GatewayProvider,
                EndpointControlEvidencePolicy.TopologyFingerprint(domain), version, "synthetic-acquisition")
        } };
    }

    private static SmtpProbeResult Probe(bool rejected, DateTimeOffset at)
    {
        var category = rejected ? SmtpResponseCategory.RecipientRejected : SmtpResponseCategory.Accepted;
        var code = rejected ? 550 : 250;
        var session = new SmtpSessionEvidence(null,
            [new(SmtpCommand.MailFrom, 250, null, SmtpResponseCategory.Accepted, SmtpResponseTextClassification.Success, TimeSpan.Zero, Raw),
             new(SmtpCommand.RcptTo, code, rejected ? "5.1.1" : "2.1.5", category, SmtpResponseTextClassification.Success, TimeSpan.Zero, Raw)],
            "mx.example.test", TimeSpan.Zero, "sender@validator.test", Raw, Raw, OutboundIdentityId: "private-identity");
        return new(rejected ? SmtpMailboxStatus.Rejected : SmtpMailboxStatus.Accepted, code, Raw, TimeSpan.Zero,
            Evidence: new(SmtpCommand.RcptTo, code, rejected ? "5.1.1" : "2.1.5", category, SmtpResponseTextClassification.Success,
                1, MailProvider.Microsoft365, "mx.example.test", 1, at, Raw), SessionEvidence: session)
        { SessionHistory = [session] };
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Routing : IDnsMailResolver
    {
        public Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnsLookupResult(DnsStatus.Success, true, [new(10, "mx.example.test")], false, TimeSpan.Zero));
    }
    private sealed class Smtp : ISmtpMailboxProbe
    {
        public int Calls { get; private set; }
        public Task<SmtpProbeResult> ProbeAsync(string mxHost, string recipient, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Probe(false, Now)); }
    }
    private sealed class Controls : ICatchAllDetector
    {
        public int Calls { get; private set; }
        public Task<CatchAllDetectionResult> DetectAsync(string domain, string mxHost, MailProvider provider = MailProvider.Unknown,
            CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new CatchAllDetectionResult(CatchAllStatus.Unknown, 0, 0, 0, 0)); }
    }
    private sealed class Cache(DomainIntelligence? initial, MongoValidationIntelligenceStore? store = null) : IDomainValidationCache
    {
        private DomainIntelligence? _domain = initial;
        public int Count => _domain is null ? 0 : 1;
        public bool TryGet(string domain, out DomainIntelligence? data) { data = _domain; return data is not null; }
        public void Store(DomainIntelligence data, TimeSpan lifetime) => _domain = data;
        public Task<DomainIntelligence?> GetAsync(string domain, CancellationToken cancellationToken = default) => Task.FromResult(_domain);
        public async Task StoreAsync(DomainIntelligence data, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            _domain = data;
            if (store is not null) await store.SaveDomainAsync(data, cancellationToken);
        }
    }
}
