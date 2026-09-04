using System.Text.Json;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class ProjectionObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EventIdentity_IsDeterministicAcrossReplay_AndChangesByAttemptAndType()
    {
        var factory = Factory();
        var lifecycle = Lifecycle(1);

        var first = await factory.CreateLifecycleEventsAsync(lifecycle, null);
        var replay = await factory.CreateLifecycleEventsAsync(lifecycle, null);
        var secondAttempt = await factory.CreateLifecycleEventsAsync(Lifecycle(2), Lifecycle(1));

        Assert.Equal(first.Select(item => item.EventId), replay.Select(item => item.EventId));
        Assert.NotEqual(first.Single(item => item.EventType == EmailValidationObservationTypes.AttemptV1).EventId,
            secondAttempt.Single(item => item.EventType == EmailValidationObservationTypes.AttemptV1).EventId);
        Assert.NotEqual(first[0].EventId, first[1].EventId);
    }

    [Fact]
    public async Task HmacCorrelation_IsTenantScoped_AndRawEmailIsNeverSerialized()
    {
        var options = Options.Create(ConfiguredOptions());
        var service = new HmacEmailCorrelationService(options, NullLogger<HmacEmailCorrelationService>.Instance);

        var first = await service.TryCreateAsync("tenant-a", "person@example.com");
        var same = await service.TryCreateAsync("tenant-a", "PERSON@example.com");
        var otherTenant = await service.TryCreateAsync("tenant-b", "person@example.com");
        var events = await new ObservationEventFactory(service, options, new FixedTimeProvider(Now))
            .CreateLifecycleEventsAsync(Lifecycle(1), null);
        var json = JsonSerializer.Serialize(events);

        Assert.NotNull(first);
        Assert.Equal(first, same);
        Assert.NotEqual(first!.Id, otherTenant!.Id);
        Assert.DoesNotContain("person@example.com", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localPart", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawResponse", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingHmacKey_OmitsCorrelationWithoutThrowing()
    {
        var options = ConfiguredOptions();
        options.Projection.Privacy.EmailHashKey = string.Empty;
        var service = new HmacEmailCorrelationService(Options.Create(options),
            NullLogger<HmacEmailCorrelationService>.Instance);

        var result = await service.TryCreateAsync("tenant-a", "person@example.com");

        Assert.Null(result);
    }

    [Fact]
    public void OutboundHealthEvent_RequiresMaterialChangeAndIsDeterministic()
    {
        var factory = Factory();
        var healthy = new OutboundIdentityHealth("identity-1", MailProvider.Yahoo,
            OutboundIdentityHealthState.Healthy);
        var cooldown = healthy with
        {
            State = OutboundIdentityHealthState.Cooldown,
            CooldownUntil = Now.AddMinutes(30),
            AttributableFailureCount = 3,
            Reason = "PolicyBlock"
        };

        Assert.Null(factory.CreateOutboundHealthEvent(healthy, healthy, Now));
        var first = factory.CreateOutboundHealthEvent(healthy, cooldown, Now);
        var replay = factory.CreateOutboundHealthEvent(healthy, cooldown, Now);
        Assert.NotNull(first);
        Assert.Equal(first!.EventId, replay!.EventId);
    }

    [Fact]
    public void BulkResponse_HandlesSuccessDuplicateTransientAndMappingFailurePerItem()
    {
        var events = Enumerable.Range(0, 4).Select(index => Envelope($"event-{index}")).ToArray();
        using var response = JsonDocument.Parse("""
        {"items":[
          {"create":{"status":201}},
          {"create":{"status":409,"error":{"type":"version_conflict_engine_exception"}}},
          {"create":{"status":429,"error":{"type":"es_rejected_execution_exception"}}},
          {"create":{"status":400,"error":{"type":"strict_dynamic_mapping_exception"}}}
        ]}
        """);

        var results = ElasticsearchObservationSink.ParseBulkResponse(events, response.RootElement);

        Assert.Equal(ProjectionIndexDisposition.Indexed, results[0].Disposition);
        Assert.Equal(ProjectionIndexDisposition.Duplicate, results[1].Disposition);
        Assert.Equal(ProjectionIndexDisposition.Retryable, results[2].Disposition);
        Assert.Equal(ProjectionIndexDisposition.PermanentFailure, results[3].Disposition);
    }

    [Fact]
    public void ServiceBusMessage_UsesDeterministicMetadataWithoutRawEmail()
    {
        var observation = Envelope("event-1");

        var message = ProjectionOutboxDispatcher.ToMessage(observation);

        Assert.Equal(observation.EventId, message.MessageId);
        Assert.Equal(observation.ValidationId, message.CorrelationId);
        Assert.Equal(observation.EventType, message.Subject);
        Assert.Equal("application/json", message.ContentType);
        Assert.Equal("v1", message.ApplicationProperties["schemaVersion"]);
        Assert.DoesNotContain("person@example.com", message.Body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ObservationDeserializer_RejectsMalformedAndUnsupportedSchemas()
    {
        var unsupported = ObservationEventSerializer.Serialize(Envelope("event-1") with { SchemaVersion = "v2" });

        Assert.False(ObservationEventSerializer.TryDeserialize("not-json"u8.ToArray(), out _, out var malformed));
        Assert.Equal("malformed_json", malformed);
        Assert.False(ObservationEventSerializer.TryDeserialize(unsupported, out _, out var unsupportedReason));
        Assert.Equal("unsupported_event_schema", unsupportedReason);
    }

    [Fact]
    public void ElasticsearchDocument_UsesOccurrenceTimeAndCreateIdentity()
    {
        var observation = Envelope("event-1") with
        {
            OccurredAtUtc = Now.AddHours(-1),
            RecordedAtUtc = Now
        };

        var lines = ElasticsearchObservationSink.BuildBulkBody([observation])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var action = JsonDocument.Parse(lines[0]);
        using var document = JsonDocument.Parse(lines[1]);

        Assert.Equal("event-1", action.RootElement.GetProperty("create").GetProperty("_id").GetString());
        Assert.Equal(observation.OccurredAtUtc,
            document.RootElement.GetProperty("@timestamp").GetDateTimeOffset());
        Assert.Equal(observation.RecordedAtUtc,
            document.RootElement.GetProperty("recordedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public void StrictMapping_CoversEveryV1PayloadField_AndExcludesRawData()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../ops/elasticsearch/email-validation-observations/component-template-mappings.json"));
        using var mapping = JsonDocument.Parse(File.ReadAllText(path));
        var properties = mapping.RootElement.GetProperty("template").GetProperty("mappings")
            .GetProperty("properties");
        var emitted = new[]
            {
                typeof(ValidationAttemptObservationV1),
                typeof(ValidationLifecycleObservationV1),
                typeof(OutboundIdentityHealthObservationV1)
            }
            .SelectMany(type => type.GetProperties())
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Distinct(StringComparer.Ordinal);

        Assert.Equal("strict", mapping.RootElement.GetProperty("template").GetProperty("mappings")
            .GetProperty("dynamic").GetString());
        foreach (var field in emitted) Assert.True(properties.TryGetProperty(field, out _), field);
        Assert.True(properties.TryGetProperty("@timestamp", out _));
        Assert.False(properties.TryGetProperty("email", out _));
        Assert.False(properties.TryGetProperty("normalizedEmail", out _));
        Assert.False(properties.TryGetProperty("rawResponse", out _));
    }

    [Fact]
    public void ProjectionOptions_RejectRawEmailAndRequireDurableSecureConfiguration()
    {
        var options = ConfiguredOptions();
        options.Projection.Privacy.IncludeRawEmail = true;
        options.Projection.Privacy.EmailHashKey = "short";
        options.Projection.ServiceBus.ConnectionString = string.Empty;

        var result = new EmailValidationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("IncludeRawEmail", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("HMAC", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("Service Bus", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledProjection_DoesNotResolveMongoProjectionInfrastructure()
    {
        var services = new ServiceCollection();
        services.Configure<EmailValidationOptions>(configured =>
        {
            configured.ProbeSenderSource.Index = "authorized-senders";
            configured.ProbeSenderSource.QueryJson = "{\"match_all\":{}}";
        });
        services.AddLogging();
        services.AddEmailValidation();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DisabledProjectionReconciler>(provider.GetRequiredService<IProjectionReconciler>());
        Assert.IsType<DisabledProjectionOutbox>(provider.GetRequiredService<IProjectionOutbox>());
    }

    private static ObservationEventFactory Factory()
    {
        var options = Options.Create(ConfiguredOptions());
        return new(new HmacEmailCorrelationService(options, NullLogger<HmacEmailCorrelationService>.Instance),
            options, new FixedTimeProvider(Now));
    }

    private static EmailValidationOptions ConfiguredOptions() => new()
    {
        Persistence = new PersistenceOptions
        {
            Enabled = true,
            Provider = "MongoDB",
            ConnectionString = "mongodb://unit-test.invalid/email-validation",
            DatabaseName = "email-validation"
        },
        ProbeSenderSource = new ProbeSenderSourceOptions
        {
            Index = "authorized-senders",
            QueryJson = "{\"match_all\":{}}"
        },
        Projection = new EmailValidationProjectionOptions
        {
            Enabled = true,
            Environment = "test",
            ServiceBus = new ProjectionServiceBusOptions
            {
                ConnectionString = "Endpoint=sb://unit-test.invalid/;SharedAccessKeyName=test;SharedAccessKey=not-used"
            },
            Elasticsearch = new ProjectionElasticsearchOptions
            {
                Endpoint = "http://localhost:9200",
                DataStreamName = "email-validation-observations-test-v1"
            },
            Privacy = new ProjectionPrivacyOptions
            {
                EmailHashKey = "0123456789abcdef0123456789abcdef",
                EmailHashKeyVersion = "v1"
            }
        }
    };

    private static ValidationLifecycle Lifecycle(int attemptNumber)
    {
        var attemptAt = Now.AddMinutes(attemptNumber);
        var result = new EmailValidationResult
        {
            Email = "person@example.com",
            NormalizedEmail = "person@example.com",
            ValidationId = "validation-1",
            Status = EmailValidationStatus.Unknown,
            SubStatus = DetailedStatus.TemporaryFailure,
            Confidence = 0.4,
            Checks = new EmailValidationChecks { CatchAll = CatchAllStatus.Unknown },
            MailProvider = MailProvider.Microsoft365,
            ResultState = ValidationResultState.Provisional,
            AttemptNumber = attemptNumber,
            MaximumAttempts = 3,
            DurationMs = 125
        };
        var attempts = Enumerable.Range(1, attemptNumber).Select(index => new ValidationAttemptRecord(
            index, result.Status, result.SubStatus, result.Confidence, result.MailProvider,
            [ReasonCode.TemporarySmtpFailure], Now.AddMinutes(index), ValidationResultSource.LiveValidation,
            Now.AddMinutes(index + 5), NormalizedRecipientDomain: "example.com",
            SmtpResponseFingerprint: "smtp-fingerprint", SmtpReplyCode: 451,
            SmtpNormalizedReason: SmtpNormalizedReason.TemporaryFailure)).ToArray();
        return new ValidationLifecycle
        {
            ValidationId = "validation-1",
            NormalizedEmail = "person@example.com",
            Request = new EmailValidationRequest(true, ValidationId: "validation-1",
                TenantId: "tenant-a", ConsumerId: "consumer-a"),
            ResultState = ValidationResultState.Provisional,
            AttemptNumber = attemptNumber,
            MaximumAttempts = 3,
            CurrentResult = result,
            Attempts = attempts,
            FirstValidatedAt = Now.AddMinutes(1),
            LastValidatedAt = attemptAt,
            LastUpdatedAt = attemptAt,
            RetryScheduled = true,
            NextRetryAt = attemptAt.AddMinutes(5),
            LifecycleState = ValidationLifecycleState.Provisional,
            Sequence = attemptNumber,
            Version = attemptNumber
        };
    }

    private static EmailValidationObservationEnvelope Envelope(string id) => new(
        id, EmailValidationObservationTypes.AttemptV1, "v1", Now, Now, "test",
        null, null, "validation-1", null, 1, JsonSerializer.SerializeToElement(new { attemptNumber = 1 }));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
