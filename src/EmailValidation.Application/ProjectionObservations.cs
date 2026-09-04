using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Application;

public static class EmailValidationObservationTypes
{
    public const string AttemptV1 = "validation.attempt.observed.v1";
    public const string LifecycleV1 = "validation.lifecycle.changed.v1";
    public const string OutboundIdentityHealthV1 = "outbound-identity.health.changed.v1";
    public const string SchemaVersionV1 = "v1";
    public const string MappingVersionV1 = "v1";
}

public sealed record EmailValidationObservationEnvelope(
    string EventId,
    string EventType,
    string SchemaVersion,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string Environment,
    string? TenantId,
    string? ConsumerId,
    string? ValidationId,
    string? JobId,
    long? Sequence,
    JsonElement Payload);

public sealed record ValidationAttemptObservationV1(
    string ValidationId,
    int AttemptNumber,
    long LifecycleSequence,
    string? EmailCorrelationId,
    string? EmailHashKeyVersion,
    string? RecipientDomain,
    string Provider,
    string? ProviderClassificationSource,
    double? ProviderConfidence,
    string? MxFingerprint,
    string? MxHost,
    string Status,
    string SubStatus,
    string ResultState,
    string ResultSource,
    double Confidence,
    double? VerificationReliability,
    string? ResultStability,
    string CatchAllState,
    string RoleAddressState,
    string? OutboundIdentityId,
    string? SourceIp,
    string? EhloHostName,
    string? FcrDnsState,
    string? SmtpStage,
    int? ReplyCode,
    string? EnhancedStatusCode,
    string? NormalizedReason,
    string? ResponseFingerprint,
    bool RetryScheduled,
    DateTimeOffset? RetryAtUtc,
    int MaxAttempts,
    long DurationMilliseconds,
    DateTimeOffset OccurredAtUtc,
    string? StrategyVersion,
    string? ClassificationVersion,
    string? PolicyVersion);

public sealed record ValidationLifecycleObservationV1(
    string ValidationId,
    long Sequence,
    string? PreviousLifecycleState,
    string LifecycleState,
    string ResultState,
    string Status,
    string SubStatus,
    int AttemptNumber,
    int MaxAttempts,
    bool RetryScheduled,
    DateTimeOffset? RetryAtUtc,
    string Provider,
    DateTimeOffset OccurredAtUtc);

public sealed record OutboundIdentityHealthObservationV1(
    string OutboundIdentityId,
    string Provider,
    string PreviousHealthState,
    string HealthState,
    DateTimeOffset? CooldownUntilUtc,
    string? NormalizedReason,
    int FailureCount,
    DateTimeOffset OccurredAtUtc,
    string HealthPolicyVersion);

public enum ProjectionOutboxState { Pending, Publishing, Published, Failed }

public sealed record ProjectionOutboxEntry(
    EmailValidationObservationEnvelope Event,
    ProjectionOutboxState State,
    int PublishAttemptCount,
    DateTimeOffset NextPublishAttemptAtUtc,
    string? LockedBy = null,
    DateTimeOffset? LockExpiresAtUtc = null,
    DateTimeOffset? PublishedAtUtc = null,
    string? LastErrorCode = null,
    DateTimeOffset? CreatedAtUtc = null);

public interface IProjectionOutbox
{
    Task<bool> EnqueueAsync(EmailValidationObservationEnvelope observation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectionOutboxEntry>> ClaimAsync(
        int maximumCount,
        string lockOwner,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default);
    Task MarkPublishedAsync(string eventId, string lockOwner, CancellationToken cancellationToken = default);
    Task ReleaseAsync(
        string eventId,
        string lockOwner,
        DateTimeOffset nextAttemptAtUtc,
        string errorCode,
        bool terminal,
        CancellationToken cancellationToken = default);
    Task<ProjectionOutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken = default);
}

public sealed record ProjectionOutboxBacklog(long PendingCount, DateTimeOffset? OldestCreatedAtUtc);

public interface IProjectionPersistenceInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IEmailCorrelationService
{
    ValueTask<EmailCorrelation?> TryCreateAsync(
        string? tenantId,
        string normalizedEmail,
        CancellationToken cancellationToken = default);
}

public sealed record EmailCorrelation(string Id, string KeyVersion);

public interface IObservationEventFactory
{
    Task<IReadOnlyList<EmailValidationObservationEnvelope>> CreateLifecycleEventsAsync(
        ValidationLifecycle lifecycle,
        ValidationLifecycle? previous,
        CancellationToken cancellationToken = default);
    EmailValidationObservationEnvelope? CreateOutboundHealthEvent(
        OutboundIdentityHealth previous,
        OutboundIdentityHealth current,
        DateTimeOffset occurredAtUtc);
}

public interface IProjectionReconciler
{
    Task<ProjectionReplayResult> ReconcileAsync(CancellationToken cancellationToken = default);
    Task<ProjectionReplayResult> BackfillAsync(ProjectionReplayRequest request, CancellationToken cancellationToken = default);
}

public sealed record ProjectionReplayRequest(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int BatchSize = 500,
    int MaximumEvents = 10_000,
    bool DryRun = false,
    string? EventType = null,
    string? TenantId = null);

public sealed record ProjectionReplayResult(
    int CanonicalRecordsRead,
    int EventsConsidered,
    int EventsEnqueued,
    DateTimeOffset? LastObservedAtUtc,
    string? LastStableIdentifier,
    bool DryRun);

public sealed class ObservationEventFactory(
    IEmailCorrelationService correlation,
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider) : IObservationEventFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EmailValidationProjectionOptions _options = options.Value.Projection;

    public async Task<IReadOnlyList<EmailValidationObservationEnvelope>> CreateLifecycleEventsAsync(
        ValidationLifecycle lifecycle,
        ValidationLifecycle? previous,
        CancellationToken cancellationToken = default)
    {
        var events = new List<EmailValidationObservationEnvelope>(2);
        var occurredAt = EnsureUtc(lifecycle.LastUpdatedAt ?? lifecycle.LastValidatedAt);
        if (previous is null || previous.AttemptNumber != lifecycle.AttemptNumber)
        {
            var attempt = lifecycle.Attempts.LastOrDefault(item => item.AttemptNumber == lifecycle.AttemptNumber);
            if (attempt is not null)
            {
                var emailCorrelation = await correlation.TryCreateAsync(
                    lifecycle.Request.TenantId, lifecycle.NormalizedEmail, cancellationToken).ConfigureAwait(false);
                var result = lifecycle.CurrentResult;
                var payload = new ValidationAttemptObservationV1(
                    lifecycle.ValidationId,
                    attempt.AttemptNumber,
                    lifecycle.Sequence,
                    emailCorrelation?.Id,
                    emailCorrelation?.KeyVersion,
                    _options.Privacy.IncludeRecipientDomain ? attempt.NormalizedRecipientDomain : null,
                    attempt.Provider.ToString(),
                    NormalizeCode(attempt.ProviderClassificationSource),
                    attempt.ProviderClassificationConfidence,
                    attempt.MxTopologyFingerprint,
                    attempt.MxHost,
                    attempt.Status.ToString(),
                    attempt.SubStatus.ToString(),
                    lifecycle.ResultState.ToString(),
                    attempt.ResultSource.ToString(),
                    attempt.Confidence,
                    result.DomainIntelligence?.Behavior?.VerificationReliability,
                    lifecycle.ResultState == ValidationResultState.Final ? "Final" : "Provisional",
                    result.Checks.CatchAll.ToString(),
                    result.Checks.RoleAccount ? "Role" : "NonRole",
                    attempt.OutboundIdentityId,
                    attempt.SourceAddress,
                    attempt.EhloHostName,
                    attempt.FcrDnsState?.ToString(),
                    attempt.SmtpStage?.ToString(),
                    attempt.SmtpReplyCode,
                    attempt.SmtpEnhancedStatusCode,
                    attempt.SmtpNormalizedReason?.ToString(),
                    attempt.SmtpResponseFingerprint,
                    lifecycle.RetryScheduled || lifecycle.PendingRevalidation is not null,
                    lifecycle.NextRetryAt?.ToUniversalTime(),
                    lifecycle.MaximumAttempts,
                    result.DurationMs,
                    EnsureUtc(attempt.AttemptedAt),
                    attempt.SmtpStrategyVersion ?? result.Metadata?.Policy.ProviderStrategyVersion,
                    attempt.SmtpClassificationVersion,
                    attempt.SmtpDecisionPolicyVersion ?? result.Metadata?.Policy.ClassificationPolicyVersion);
                events.Add(CreateEnvelope(
                    EmailValidationObservationTypes.AttemptV1,
                    lifecycle.ValidationId,
                    attempt.AttemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    lifecycle.ValidationId,
                    lifecycle.Sequence,
                    payload,
                    payload.OccurredAtUtc,
                    lifecycle.Request.TenantId,
                    lifecycle.Request.ConsumerId,
                    lifecycle.Request.JobId));
            }
        }

        if (previous is null || previous.Sequence != lifecycle.Sequence ||
            previous.LifecycleState != lifecycle.LifecycleState)
        {
            var payload = new ValidationLifecycleObservationV1(
                lifecycle.ValidationId,
                lifecycle.Sequence,
                previous?.LifecycleState.ToString(),
                lifecycle.LifecycleState.ToString(),
                lifecycle.ResultState.ToString(),
                lifecycle.CurrentResult.Status.ToString(),
                lifecycle.CurrentResult.SubStatus.ToString(),
                lifecycle.AttemptNumber,
                lifecycle.MaximumAttempts,
                lifecycle.RetryScheduled || lifecycle.PendingRevalidation is not null,
                lifecycle.NextRetryAt?.ToUniversalTime(),
                lifecycle.CurrentResult.MailProvider.ToString(),
                occurredAt);
            events.Add(CreateEnvelope(
                EmailValidationObservationTypes.LifecycleV1,
                lifecycle.ValidationId,
                lifecycle.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                lifecycle.ValidationId,
                lifecycle.Sequence,
                payload,
                occurredAt,
                lifecycle.Request.TenantId,
                lifecycle.Request.ConsumerId,
                lifecycle.Request.JobId));
        }
        return events;
    }

    public EmailValidationObservationEnvelope? CreateOutboundHealthEvent(
        OutboundIdentityHealth previous,
        OutboundIdentityHealth current,
        DateTimeOffset occurredAtUtc)
    {
        if (previous.State == current.State && previous.CooldownUntil == current.CooldownUntil &&
            previous.AttributableFailureCount == current.AttributableFailureCount)
            return null;
        var occurred = EnsureUtc(occurredAtUtc);
        var payload = new OutboundIdentityHealthObservationV1(
            current.IdentityId,
            current.Provider.ToString(),
            previous.State.ToString(),
            current.State.ToString(),
            current.CooldownUntil?.ToUniversalTime(),
            NormalizeCode(current.Reason),
            current.AttributableFailureCount,
            occurred,
            "outbound-health-v1");
        var sequence = $"{occurred.UtcTicks}|{current.State}|{current.AttributableFailureCount}";
        return CreateEnvelope(
            EmailValidationObservationTypes.OutboundIdentityHealthV1,
            current.IdentityId,
            sequence,
            null,
            null,
            payload,
            occurred);
    }

    private EmailValidationObservationEnvelope CreateEnvelope<TPayload>(
        string eventType,
        string aggregateId,
        string sequenceKey,
        string? validationId,
        long? sequence,
        TPayload payload,
        DateTimeOffset occurredAtUtc,
        string? tenantId = null,
        string? consumerId = null,
        string? jobId = null)
    {
        var canonical = string.Join('|', EmailValidationObservationTypes.SchemaVersionV1,
            eventType, aggregateId, sequenceKey);
        var eventId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(eventId, eventType, EmailValidationObservationTypes.SchemaVersionV1,
            EnsureUtc(occurredAtUtc), EnsureUtc(timeProvider.GetUtcNow()), _options.Environment,
            tenantId, consumerId, validationId, jobId, sequence,
            JsonSerializer.SerializeToElement(payload, JsonOptions));
    }

    private static DateTimeOffset EnsureUtc(DateTimeOffset value) => value.ToUniversalTime();

    private static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new string(value.Trim().Take(128)
            .Where(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':')
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? null : safe;
    }
}
