using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core;

public sealed class RevalidationPolicy(
    IProviderPolicyResolver providerPolicies,
    IOptions<EmailValidationOptions> options) : IRevalidationPolicy
{
    private static readonly HashSet<ReasonCode> RetryableReasons =
    [
        ReasonCode.SmtpTimeout,
        ReasonCode.DnsTimeout,
        ReasonCode.DnsFailure,
        ReasonCode.SmtpConnectionFailure,
        ReasonCode.TemporarySmtpFailure,
        ReasonCode.TemporaryFailure,
        ReasonCode.Timeout,
        ReasonCode.Greylisted,
        ReasonCode.RateLimited,
        ReasonCode.ProviderBlockedVerification,
        ReasonCode.ProviderVerificationBlocked,
        ReasonCode.PolicyBlock,
        ReasonCode.LocalCooldown,
        ReasonCode.RetryRecommended,
        ReasonCode.MailboxAcceptanceAmbiguous,
        ReasonCode.MxResultsConflicting,
        ReasonCode.MailboxFull
    ];

    private readonly RevalidationOptions _options = options.Value.Revalidation;

    private static readonly HashSet<ReasonCode> TerminalReasons =
    [
        ReasonCode.InvalidSyntax,
        ReasonCode.DomainNotFound,
        ReasonCode.NullMailExchanger,
        ReasonCode.NoMailExchanger,
        ReasonCode.NoMailRouting,
        ReasonCode.UnroutableMailInfrastructure,
        ReasonCode.MailboxRejected,
        ReasonCode.MicrosoftRecipientRejected,
        ReasonCode.SuppressionMatch
    ];

    public RevalidationDecision Evaluate(EmailValidationResult result, RevalidationContext context)
    {
        var providerMaximum = Math.Max(1, providerPolicies.Resolve(result.MailProvider).MaxRetries + 1);
        var configuredMaximum = Math.Max(1, _options.DefaultMaxAttempts);
        var maximum = context.ExistingMaximumAttempts ?? Math.Min(configuredMaximum, providerMaximum);
        maximum = Math.Max(context.AttemptNumber, maximum);

        var enforcedIntelligenceRetry = result.SmtpEvidence is
        {
            IntelligenceMode: SmtpResponseIntelligenceMode.Enforced,
            Decision.RetryDisposition: not SmtpRetryDisposition.None
        };
        if (!_options.Enabled || (result.Status != EmailValidationStatus.Unknown && !enforcedIntelligenceRetry) ||
            result.ReasonCodes.Any(TerminalReasons.Contains) ||
            result.MailingRisk?.RiskReasons.Contains(MailingRiskReason.KnownSuppression) == true)
            return new(false, null, maximum);

        var reason = result.ReasonCodes.FirstOrDefault(RetryableReasons.Contains);
        if (!RetryableReasons.Contains(reason))
            return new(false, null, maximum);

        return new(context.AttemptNumber < maximum, reason, maximum);
    }
}

public sealed class RevalidationSchedulePolicy(
    IProviderPolicyResolver providerPolicies,
    IDomainBackoffPolicy backoffPolicy) : IRevalidationSchedulePolicy
{
    public RevalidationSchedule CreateSchedule(RevalidationScheduleContext context)
    {
        var category = Category(context.Result, context.Reason);
        var backoff = backoffPolicy.Evaluate(
            context.Result.MailProvider,
            category,
            Math.Max(1, context.AttemptNumber),
            context.Now).NextAllowedAttemptAt;
        var scheduledAt = Max(context.Now, backoff, context.Result.RetryAfter, context.CurrentCooldownUntil);
        if (category == SmtpResponseCategory.VerificationBlocked)
        {
            var providerCooldown = context.Now.AddMinutes(
                Math.Max(0, providerPolicies.Resolve(context.Result.MailProvider).PolicyBlockCooldownMinutes));
            scheduledAt = Max(scheduledAt, providerCooldown);
        }

        return new(scheduledAt, context.Reason.ToString());
    }

    private static SmtpResponseCategory Category(EmailValidationResult result, ReasonCode reason)
    {
        if (result.ProviderValidation is { } provider &&
            provider.EffectiveCategory is not (SmtpResponseCategory.Unknown or SmtpResponseCategory.NotAttempted))
            return provider.EffectiveCategory;
        if (result.Diagnostics is { } diagnostics &&
            diagnostics.SmtpResponseCategory is not (SmtpResponseCategory.Unknown or SmtpResponseCategory.NotAttempted))
            return diagnostics.SmtpResponseCategory;
        return reason switch
        {
            ReasonCode.Greylisted => SmtpResponseCategory.Greylisted,
            ReasonCode.RateLimited => SmtpResponseCategory.RateLimited,
            ReasonCode.ProviderBlockedVerification or ReasonCode.ProviderVerificationBlocked or ReasonCode.PolicyBlock
                => SmtpResponseCategory.VerificationBlocked,
            ReasonCode.LocalCooldown => SmtpResponseCategory.LocalCooldown,
            ReasonCode.SmtpTimeout or ReasonCode.Timeout => SmtpResponseCategory.Timeout,
            ReasonCode.SmtpConnectionFailure => SmtpResponseCategory.ConnectionRejected,
            ReasonCode.MailboxFull => SmtpResponseCategory.MailboxFull,
            _ => SmtpResponseCategory.TemporaryFailure
        };
    }

    private static DateTimeOffset Max(params DateTimeOffset?[] values) =>
        values.Where(value => value.HasValue).Max(value => value!.Value);
}

public sealed class RevalidationOutboxDispatcher(
    IRevalidationOutbox outbox,
    IRevalidationScheduler scheduler,
    IRevalidationMetrics metrics,
    IOptions<EmailValidationOptions> options,
    ILogger<RevalidationOutboxDispatcher> logger,
    IValidationLifecycleStore? lifecycleStore = null,
    IValidationStatusPublisher? statusPublisher = null,
    TimeProvider? timeProvider = null) : IRevalidationOutboxDispatcher
{
    private readonly TimeSpan _lease = TimeSpan.FromSeconds(
        Math.Max(1, options.Value.Revalidation.OutboxLeaseSeconds));

    public async Task<RevalidationScheduleResult?> DispatchAsync(
        string validationId,
        CancellationToken cancellationToken = default)
    {
        var pending = await outbox.TryClaimAsync(validationId, _lease, cancellationToken).ConfigureAwait(false);
        if (pending is null) return null;
        try
        {
            await ReportRetrySchedulingAsync(validationId, cancellationToken).ConfigureAwait(false);
            var result = await scheduler.ScheduleAsync(
                new(pending.Message, pending.ScheduledAt), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await outbox.ReleaseAsync(validationId, pending.Message.MessageId, result.ErrorCode, cancellationToken)
                    .ConfigureAwait(false);
                await ReportSchedulingFailureAsync(validationId).ConfigureAwait(false);
                return result;
            }

            if (await outbox.MarkScheduledAsync(
                validationId, pending.Message.MessageId, result, cancellationToken).ConfigureAwait(false))
            {
                metrics.RecordScheduled(ParseProvider(pending.Message.Provider));
                if (lifecycleStore is not null && statusPublisher is not null)
                {
                    var lifecycle = await lifecycleStore.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
                    if (lifecycle is not null)
                    {
                        try
                        {
                            await statusPublisher.PublishAsync(
                                ValidationStatusMapper.ToEvent(
                                    lifecycle, (timeProvider ?? TimeProvider.System).GetUtcNow()),
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            logger.LogWarning(exception,
                                "Lifecycle {ValidationId} was persisted but its retry-waiting status could not be published",
                                validationId);
                        }
                    }
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not dispatch revalidation outbox item {MessageId}", pending.Message.MessageId);
            await outbox.ReleaseAsync(validationId, pending.Message.MessageId, "dispatch_failed", cancellationToken)
                .ConfigureAwait(false);
            await ReportSchedulingFailureAsync(validationId).ConfigureAwait(false);
            return new(false, pending.Message.MessageId, pending.ScheduledAt, ErrorCode: "dispatch_failed");
        }
    }

    public async Task<int> DispatchPendingAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        var ids = await outbox.GetPendingValidationIdsAsync(maximumCount, cancellationToken).ConfigureAwait(false);
        var dispatched = 0;
        foreach (var id in ids)
        {
            var result = await DispatchAsync(id, cancellationToken).ConfigureAwait(false);
            if (result?.Succeeded == true) dispatched++;
        }
        return dispatched;
    }

    private static MailProvider ParseProvider(string? value) =>
        Enum.TryParse<MailProvider>(value, true, out var provider) ? provider : MailProvider.Unknown;

    private async Task ReportRetrySchedulingAsync(string validationId, CancellationToken cancellationToken)
    {
        if (lifecycleStore is null) return;
        var current = await lifecycleStore.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.ResultState == ValidationResultState.Final || current.PendingRevalidation is null)
            return;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var scheduling = current with
        {
            LifecycleState = ValidationLifecycleState.RetryScheduled,
            CurrentStage = ValidationProgressStage.RetryScheduled,
            LastUpdatedAt = now,
            StatusMessage = "Automatic revalidation is being scheduled.",
            Sequence = current.Sequence + 1,
            Version = current.Version + 1
        };
        var saved = await lifecycleStore.TrySaveAsync(scheduling, current.Version, cancellationToken)
            .ConfigureAwait(false);
        if (!saved.Applied || statusPublisher is null) return;
        try
        {
            await statusPublisher.PublishAsync(
                ValidationStatusMapper.ToEvent(saved.Lifecycle!, now), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Lifecycle {ValidationId} was persisted but its retry-scheduled status could not be published",
                validationId);
        }
    }

    private async Task ReportSchedulingFailureAsync(string validationId)
    {
        if (lifecycleStore is null) return;
        try
        {
            var current = await lifecycleStore.GetAsync(validationId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || current.ResultState == ValidationResultState.Final) return;
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var failed = current with
            {
                LifecycleState = ValidationLifecycleState.Provisional,
                CurrentStage = ValidationProgressStage.Provisional,
                RetryScheduled = false,
                CurrentResult = current.CurrentResult with { RetryScheduled = false },
                LastUpdatedAt = now,
                StatusMessage = "Automatic retry could not be scheduled; validation remains provisional.",
                Sequence = current.Sequence + 1,
                Version = current.Version + 1
            };
            var saved = await lifecycleStore.TrySaveAsync(failed, current.Version, CancellationToken.None)
                .ConfigureAwait(false);
            if (saved.Applied && statusPublisher is not null)
            {
                try
                {
                    await statusPublisher.PublishAsync(
                        ValidationStatusMapper.ToEvent(saved.Lifecycle!, now), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Lifecycle {ValidationId} was persisted but its scheduling-failure status could not be published",
                        validationId);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not persist the scheduling-failure status for lifecycle {ValidationId}", validationId);
        }
    }
}

public sealed class ValidationLifecycleCoordinator(
    IValidationLifecycleStore store,
    IRevalidationPolicy retryPolicy,
    IRevalidationSchedulePolicy schedulePolicy,
    IRevalidationOutboxDispatcher dispatcher,
    IRevalidationMetrics metrics,
    TimeProvider timeProvider,
    IOptions<EmailValidationOptions> options,
    ILogger<ValidationLifecycleCoordinator> logger,
    IValidationStatusPublisher? statusPublisher = null) : IValidationLifecycleCoordinator
{
    private readonly RevalidationOptions _options = options.Value.Revalidation;

    public async Task<ValidationLifecycleStartResult> BeginAsync(
        string email,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestedId = string.IsNullOrWhiteSpace(request.ValidationId)
            ? Guid.NewGuid().ToString("N")
            : request.ValidationId.Trim();
        if (requestedId.Length > 128)
            throw new ArgumentException("ValidationId must not exceed 128 characters.", nameof(request));
        var normalizedEmail = LifecycleKey(email, requestedId);
        var existingById = await store.GetAsync(requestedId, cancellationToken).ConfigureAwait(false);
        if (existingById is not null &&
            !string.Equals(existingById.NormalizedEmail, normalizedEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ValidationId is already associated with a different validation.");
        var existing = existingById ??
            await store.GetActiveByEmailAsync(normalizedEmail, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return new(existing.ValidationId, existing, false);

        var now = timeProvider.GetUtcNow();
        var placeholder = Placeholder(email, normalizedEmail, requestedId);
        var requested = new ValidationLifecycle
        {
            ValidationId = requestedId,
            NormalizedEmail = normalizedEmail,
            Request = request with { ValidationId = requestedId },
            ResultState = ValidationResultState.Provisional,
            AttemptNumber = 0,
            MaximumAttempts = Math.Max(1, _options.DefaultMaxAttempts),
            CurrentResult = placeholder,
            RequestedAt = now,
            LastUpdatedAt = now,
            LifecycleState = ValidationLifecycleState.Requested,
            CurrentStage = ValidationProgressStage.Requested,
            StatusMessage = "Validation requested.",
            Sequence = 1,
            Version = 1
        };
        var saved = await store.TrySaveAsync(requested, 0, cancellationToken).ConfigureAwait(false);
        if (!saved.Applied)
        {
            existing = await store.GetAsync(requestedId, cancellationToken).ConfigureAwait(false) ??
                await store.GetActiveByEmailAsync(normalizedEmail, cancellationToken).ConfigureAwait(false);
            return new(existing?.ValidationId ?? requestedId, existing, false);
        }

        await PublishBestEffortAsync(saved.Lifecycle!).ConfigureAwait(false);
        var validating = saved.Lifecycle! with
        {
            LifecycleState = ValidationLifecycleState.Validating,
            CurrentStage = ValidationProgressStage.Started,
            StartedAt = now,
            LastUpdatedAt = now,
            StatusMessage = "Validation started.",
            Sequence = saved.Lifecycle!.Sequence + 1,
            Version = saved.Lifecycle!.Version + 1
        };
        var started = await store.TrySaveAsync(validating, saved.Lifecycle!.Version, cancellationToken)
            .ConfigureAwait(false);
        var canonical = started.Applied ? started.Lifecycle! :
            await store.GetAsync(requestedId, cancellationToken).ConfigureAwait(false) ?? saved.Lifecycle!;
        if (started.Applied)
            await PublishBestEffortAsync(canonical).ConfigureAwait(false);
        return new(canonical.ValidationId, canonical, true);
    }

    public async Task FailAsync(string validationId, CancellationToken cancellationToken = default)
    {
        var existing = await store.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.LifecycleState is ValidationLifecycleState.Final or ValidationLifecycleState.Failed)
            return;
        var now = timeProvider.GetUtcNow();
        var failed = existing with
        {
            LifecycleState = ValidationLifecycleState.Failed,
            CurrentStage = ValidationProgressStage.Failed,
            ResultState = ValidationResultState.Final,
            RetryScheduled = false,
            PendingRevalidation = null,
            FinalizedAt = now,
            LastUpdatedAt = now,
            StatusMessage = "Validation failed.",
            Sequence = existing.Sequence + 1,
            Version = existing.Version + 1,
            CurrentResult = existing.CurrentResult with
            {
                ResultState = ValidationResultState.Final,
                RetryScheduled = false,
                FinalizedAt = now
            }
        };
        var saved = await store.TrySaveAsync(failed, existing.Version, cancellationToken).ConfigureAwait(false);
        if (saved.Applied)
            await PublishBestEffortAsync(saved.Lifecycle!).ConfigureAwait(false);
    }

    public async Task<ValidationLifecycleResult> ProcessInitialResultAsync(
        EmailValidationResult result,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = string.IsNullOrWhiteSpace(result.NormalizedEmail)
            ? result.Email.Trim().ToLowerInvariant()
            : result.NormalizedEmail;

        for (var collision = 0; collision < 3; collision++)
        {
            var existing = string.IsNullOrWhiteSpace(request.ValidationId)
                ? await store.GetActiveByEmailAsync(normalizedEmail, cancellationToken).ConfigureAwait(false)
                : await store.GetAsync(request.ValidationId, cancellationToken).ConfigureAwait(false) ??
                    await store.GetActiveByEmailAsync(normalizedEmail, cancellationToken).ConfigureAwait(false);
            var now = result.Metadata?.ValidatedAt ?? timeProvider.GetUtcNow();
            if (existing?.LastValidatedAt == now)
                return existing.PendingRevalidation is null
                    ? new(existing.CurrentResult, existing, false, existing.RetryScheduled)
                    : await DispatchIfPendingAsync(existing, cancellationToken).ConfigureAwait(false);
            var attempt = existing is null ? 1 : Math.Max(1, existing.AttemptNumber + 1);
            var decision = retryPolicy.Evaluate(result, new(attempt, existing?.MaximumAttempts));
            var lifecycle = BuildLifecycle(existing, result, request, attempt, decision, now);
            var saved = await store.TrySaveAsync(lifecycle, existing?.Version ?? 0, cancellationToken)
                .ConfigureAwait(false);
            if (!saved.Applied) continue;
            await PublishBestEffortAsync(saved.Lifecycle!).ConfigureAwait(false);
            return await DispatchIfPendingAsync(saved.Lifecycle!, cancellationToken).ConfigureAwait(false);
        }

        var latest = await store.GetActiveByEmailAsync(normalizedEmail, cancellationToken).ConfigureAwait(false);
        if (latest is not null)
        {
            logger.LogWarning(
                "Validation lifecycle compare-and-set was contended for {ValidationId}; returning the current canonical result",
                latest.ValidationId);
            return new(latest.CurrentResult, latest, false, latest.RetryScheduled);
        }
        throw new InvalidOperationException("Validation lifecycle could not be persisted after repeated concurrency conflicts.");
    }

    public async Task<ValidationLifecycleResult> ProcessRetryResultAsync(
        string validationId,
        long expectedVersion,
        int expectedAttemptNumber,
        EmailValidationResult result,
        CancellationToken cancellationToken = default)
    {
        var existing = await store.GetAsync(validationId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.Version != expectedVersion ||
            existing.ResultState == ValidationResultState.Final ||
            expectedAttemptNumber != (existing.LifecycleState == ValidationLifecycleState.Revalidating
                ? existing.AttemptNumber
                : existing.AttemptNumber + 1))
            return new(existing?.CurrentResult ?? result, existing, false, false);

        var now = result.Metadata?.ValidatedAt ?? timeProvider.GetUtcNow();
        var decision = retryPolicy.Evaluate(result, new(expectedAttemptNumber, existing.MaximumAttempts));
        var lifecycle = BuildLifecycle(existing, result, existing.Request, expectedAttemptNumber, decision, now);
        var saved = await store.TrySaveAsync(lifecycle, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (!saved.Applied)
            return new(existing.CurrentResult, existing, false, false);
        await PublishBestEffortAsync(saved.Lifecycle!).ConfigureAwait(false);
        return await DispatchIfPendingAsync(saved.Lifecycle!, cancellationToken).ConfigureAwait(false);
    }

    private ValidationLifecycle BuildLifecycle(
        ValidationLifecycle? existing,
        EmailValidationResult raw,
        EmailValidationRequest request,
        int attempt,
        RevalidationDecision decision,
        DateTimeOffset attemptedAt)
    {
        var validationId = existing?.ValidationId ?? request.ValidationId ?? Guid.NewGuid().ToString("N");
        var shouldRetry = decision.ShouldRetry && decision.Reason.HasValue;
        var first = existing is null || existing.AttemptNumber == 0 ? attemptedAt : existing.FirstValidatedAt;
        PendingRevalidation? pending = null;
        DateTimeOffset? nextRetry = null;
        if (shouldRetry)
        {
            var schedule = schedulePolicy.CreateSchedule(new(raw, decision.Reason!.Value, attempt, timeProvider.GetUtcNow()));
            nextRetry = schedule.ScheduledAt;
            var message = new EmailRevalidationMessageV1(
                validationId,
                attempt + 1,
                decision.MaximumAttempts,
                first,
                attemptedAt,
                schedule.ScheduledAt,
                raw.MailProvider.ToString(),
                raw.Status,
                raw.SubStatus,
                raw.Metadata?.Policy.ClassificationPolicyVersion);
            pending = new(message, timeProvider.GetUtcNow(), schedule.ScheduledAt);
        }

        var state = shouldRetry ? ValidationResultState.Provisional : ValidationResultState.Final;
        var enriched = raw with
        {
            ValidationId = validationId,
            ResultState = state,
            AttemptNumber = attempt,
            MaximumAttempts = decision.MaximumAttempts,
            RetryScheduled = false,
            RetryAfter = nextRetry ?? raw.RetryAfter,
            UnknownContext = raw.UnknownContext is null
                ? null
                : raw.UnknownContext with { RetryAfter = nextRetry ?? raw.RetryAfter },
            FirstValidatedAt = first,
            LastValidatedAt = attemptedAt,
            FinalizedAt = state == ValidationResultState.Final ? attemptedAt : null
        };
        var attempts = (existing?.Attempts ?? []).Append(ToAttempt(enriched, attemptedAt)).ToArray();
        if (state == ValidationResultState.Provisional) metrics.RecordProvisional(raw.MailProvider);
        else
        {
            if (existing is not null)
                metrics.RecordFinalized(raw.MailProvider, existing.CurrentResult.Status, raw.Status,
                    attemptedAt - first, attempt);
            if (raw.Status == EmailValidationStatus.Unknown && decision.Reason.HasValue &&
                attempt >= decision.MaximumAttempts)
                metrics.RecordExhausted(raw.MailProvider);
        }

        logger.LogInformation(
            "Validation {ValidationId} transitioned to {ResultState} at attempt {AttemptNumber}; status {Status}; next retry {NextRetryAt}",
            validationId, state, attempt, raw.Status, nextRetry);

        return new ValidationLifecycle
        {
            ValidationId = validationId,
            NormalizedEmail = raw.NormalizedEmail ?? existing?.NormalizedEmail ?? raw.Email.Trim().ToLowerInvariant(),
            Request = request,
            ResultState = state,
            AttemptNumber = attempt,
            MaximumAttempts = decision.MaximumAttempts,
            CurrentResult = enriched,
            Attempts = attempts,
            FirstValidatedAt = first,
            LastValidatedAt = attemptedAt,
            FinalizedAt = state == ValidationResultState.Final ? attemptedAt : null,
            NextRetryAt = nextRetry,
            RetryScheduled = false,
            PendingRevalidation = pending,
            RequestedAt = existing?.RequestedAt ?? attemptedAt,
            StartedAt = existing?.StartedAt ?? attemptedAt,
            LastUpdatedAt = attemptedAt,
            LifecycleState = shouldRetry
                ? ValidationLifecycleState.Provisional
                : ValidationLifecycleState.Final,
            CurrentStage = shouldRetry ? ValidationProgressStage.Provisional : ValidationProgressStage.Final,
            Sequence = (existing?.Sequence ?? 0) + 1,
            ResultReused = raw.Metadata?.ResultSource is ValidationResultSource.MemoryCache or
                ValidationResultSource.PersistentReuse or ValidationResultSource.JoinedInFlightValidation,
            RetryReason = shouldRetry ? decision.Reason?.ToString() : null,
            StatusMessage = shouldRetry
                ? "Validation is provisional and eligible for automatic revalidation."
                : "Validation completed.",
            Version = (existing?.Version ?? 0) + 1
        };
    }

    private async Task<ValidationLifecycleResult> DispatchIfPendingAsync(
        ValidationLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        if (lifecycle.PendingRevalidation is null)
            return new(lifecycle.CurrentResult, lifecycle, true, false);
        var scheduled = await dispatcher.DispatchAsync(lifecycle.ValidationId, cancellationToken).ConfigureAwait(false);
        var current = await store.GetAsync(lifecycle.ValidationId, cancellationToken).ConfigureAwait(false) ?? lifecycle;
        return new(current.CurrentResult, current, true, scheduled?.Succeeded == true);
    }

    private static ValidationAttemptRecord ToAttempt(EmailValidationResult result, DateTimeOffset attemptedAt)
    {
        var evidence = result.SmtpEvidence;
        var intelligence = evidence?.Intelligence;
        var decision = evidence?.Decision;
        return new(result.AttemptNumber, result.Status, result.SubStatus, result.Confidence, result.MailProvider,
            result.ReasonCodes.ToArray(), attemptedAt,
            result.Metadata?.ResultSource ?? ValidationResultSource.LiveValidation, result.RetryAfter,
            intelligence?.Stage ?? evidence?.Command,
            intelligence?.ReplyCode ?? evidence?.ResponseCode,
            intelligence?.EnhancedStatusCode ?? evidence?.EnhancedStatusCode,
            intelligence?.Reason,
            intelligence?.ResponseFingerprint,
            decision?.CooldownScope,
            decision?.HealthImpact,
            intelligence?.ClassificationVersion,
            decision?.PolicyVersion,
            evidence?.IntelligenceMode,
            result.Provider?.Family,
            result.Provider?.GatewayProvider,
            result.Provider?.MailboxProvider,
            result.Provider?.TopologyFingerprint,
            intelligence?.OutboundIdentityId,
            intelligence?.SenderIdentityId,
            decision?.ResultState,
            intelligence?.StrategyVersion);
    }

    private async Task PublishBestEffortAsync(ValidationLifecycle lifecycle)
    {
        if (statusPublisher is null) return;
        try
        {
            await statusPublisher.PublishAsync(
                ValidationStatusMapper.ToEvent(lifecycle, timeProvider.GetUtcNow()), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Lifecycle {ValidationId} sequence {Sequence} was persisted but its status event could not be published",
                lifecycle.ValidationId, lifecycle.Sequence);
        }
    }

    private static EmailValidationResult Placeholder(
        string email,
        string normalizedEmail,
        string validationId) => new()
        {
            Email = email,
            NormalizedEmail = normalizedEmail,
            Status = EmailValidationStatus.Unknown,
            Confidence = 0,
            Checks = new EmailValidationChecks(),
            ValidationId = validationId,
            ResultState = ValidationResultState.Provisional,
            AttemptNumber = 0,
            MaximumAttempts = 1,
            Metadata = null
        };

    private static string LifecycleKey(string email, string validationId)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var separator = normalized.LastIndexOf('@');
        return separator > 0 && separator < normalized.Length - 1
            ? normalized
            : $"$invalid:{validationId}";
    }
}

public sealed class LifecycleEmailValidator(
    IEmailValidationService service,
    IValidationLifecycleCoordinator coordinator) : IEmailValidator
{
    public async Task<EmailValidationResult> ValidateAsync(
        string email,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var started = await coordinator.BeginAsync(email, request, cancellationToken).ConfigureAwait(false);
        if (!started.Applied && started.Lifecycle?.LifecycleState == ValidationLifecycleState.Final)
            return started.Lifecycle.CurrentResult;
        var correlatedRequest = request with { ValidationId = started.ValidationId };
        try
        {
            var result = await service.ValidateAsync(email, correlatedRequest, cancellationToken).ConfigureAwait(false);
            return (await coordinator.ProcessInitialResultAsync(result, correlatedRequest, cancellationToken)
                .ConfigureAwait(false)).Result;
        }
        catch
        {
            try
            {
                await coordinator.FailAsync(started.ValidationId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the validation failure; lifecycle failure reporting is best effort.
            }
            throw;
        }
    }
}

public sealed class EmailRevalidationProcessor(
    IValidationLifecycleStore store,
    IEmailValidationService validationService,
    IValidationLifecycleCoordinator coordinator,
    IRevalidationOutboxDispatcher dispatcher,
    ISmtpProbeThrottle throttle,
    IRevalidationSchedulePolicy schedulePolicy,
    IRevalidationMetrics metrics,
    TimeProvider timeProvider,
    IValidationStatusPublisher? statusPublisher = null) : IEmailRevalidationProcessor
{
    public async Task<RevalidationProcessingResult> ProcessAsync(
        EmailRevalidationMessageV1 message,
        CancellationToken cancellationToken = default)
    {
        if (message.MessageVersion != 1 || string.IsNullOrWhiteSpace(message.ValidationId) ||
            message.AttemptNumber < 2 || message.MaximumAttempts < message.AttemptNumber ||
            message.OriginalValidatedAt == default || message.PreviousAttemptAt == default ||
            message.ScheduledRetryAt < message.PreviousAttemptAt)
            return new(RevalidationProcessingDisposition.DeadLetter, "invalid_message", "Unsupported or invalid revalidation message.");

        var lifecycle = await store.GetAsync(message.ValidationId, cancellationToken).ConfigureAwait(false);
        if (lifecycle is null)
            return new(RevalidationProcessingDisposition.DeadLetter, "lifecycle_not_found", "Validation lifecycle was not found.");
        if (lifecycle.ResultState == ValidationResultState.Final)
        {
            metrics.RecordAlreadyFinal();
            return new(RevalidationProcessingDisposition.AlreadyFinal);
        }
        if (message.AttemptNumber < lifecycle.AttemptNumber ||
            (message.AttemptNumber == lifecycle.AttemptNumber &&
             lifecycle.LifecycleState != ValidationLifecycleState.Revalidating))
        {
            metrics.RecordDuplicate();
            return new(RevalidationProcessingDisposition.Stale);
        }
        var expectedAttempt = lifecycle.LifecycleState == ValidationLifecycleState.Revalidating
            ? lifecycle.AttemptNumber
            : lifecycle.AttemptNumber + 1;
        if (message.AttemptNumber != expectedAttempt ||
            message.MaximumAttempts != lifecycle.MaximumAttempts ||
            message.ScheduledRetryAt != lifecycle.NextRetryAt)
        {
            metrics.RecordStale();
            return new(RevalidationProcessingDisposition.Stale);
        }

        var mxHost = lifecycle.CurrentResult.SelectedMx ??
            (lifecycle.CurrentResult.MxRecords.Count > 0 ? lifecycle.CurrentResult.MxRecords[0].Host : string.Empty);
        var availability = throttle.GetAvailability(new(
            Domain(lifecycle.NormalizedEmail), mxHost, lifecycle.CurrentResult.MailProvider));
        if (!availability.CanProbe && availability.RetryAfter is { } retryAfter && retryAfter > timeProvider.GetUtcNow())
        {
            var schedule = schedulePolicy.CreateSchedule(new(
                lifecycle.CurrentResult, ReasonCode.LocalCooldown, lifecycle.AttemptNumber,
                timeProvider.GetUtcNow(), retryAfter));
            var rescheduled = lifecycle with
            {
                NextRetryAt = schedule.ScheduledAt,
                RetryScheduled = false,
                PendingRevalidation = new(message with { ScheduledRetryAt = schedule.ScheduledAt },
                    timeProvider.GetUtcNow(), schedule.ScheduledAt),
                CurrentResult = lifecycle.CurrentResult with { RetryAfter = schedule.ScheduledAt, RetryScheduled = false },
                LifecycleState = ValidationLifecycleState.Provisional,
                CurrentStage = ValidationProgressStage.Provisional,
                RetryReason = ReasonCode.LocalCooldown.ToString(),
                StatusMessage = "Provider or domain cooldown remains active; automatic revalidation will be rescheduled.",
                LastUpdatedAt = timeProvider.GetUtcNow(),
                Sequence = lifecycle.Sequence + 1,
                Version = lifecycle.Version + 1
            };
            var saved = await store.TrySaveAsync(rescheduled, lifecycle.Version, cancellationToken).ConfigureAwait(false);
            if (!saved.Applied) return new(RevalidationProcessingDisposition.Stale);
            var dispatch = await dispatcher.DispatchAsync(lifecycle.ValidationId, cancellationToken).ConfigureAwait(false);
            if (dispatch?.Succeeded != true) return new(RevalidationProcessingDisposition.RetryInfrastructureFailure);
            metrics.RecordRescheduled(lifecycle.CurrentResult.MailProvider);
            return new(RevalidationProcessingDisposition.Rescheduled);
        }

        if (lifecycle.LifecycleState != ValidationLifecycleState.Revalidating)
        {
            var revalidating = lifecycle with
            {
                LifecycleState = ValidationLifecycleState.Revalidating,
                CurrentStage = ValidationProgressStage.Revalidating,
                AttemptNumber = message.AttemptNumber,
                RetryScheduled = false,
                CurrentResult = lifecycle.CurrentResult with
                {
                    AttemptNumber = message.AttemptNumber,
                    RetryScheduled = false
                },
                StatusMessage = "Automatic revalidation started.",
                LastUpdatedAt = timeProvider.GetUtcNow(),
                Sequence = lifecycle.Sequence + 1,
                Version = lifecycle.Version + 1
            };
            var started = await store.TrySaveAsync(revalidating, lifecycle.Version, cancellationToken)
                .ConfigureAwait(false);
            if (!started.Applied) return new(RevalidationProcessingDisposition.Stale);
            lifecycle = started.Lifecycle!;
            if (statusPublisher is not null)
            {
                try
                {
                    await statusPublisher.PublishAsync(
                        ValidationStatusMapper.ToEvent(lifecycle, timeProvider.GetUtcNow()), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Canonical lifecycle persistence is authoritative; live delivery is best effort.
                }
            }
        }

        var result = await validationService.ValidateAsync(
            lifecycle.NormalizedEmail, lifecycle.Request, cancellationToken).ConfigureAwait(false);
        var reused = result.Metadata?.ResultSource is ValidationResultSource.MemoryCache or
            ValidationResultSource.PersistentReuse or ValidationResultSource.JoinedInFlightValidation;
        metrics.RecordExecuted(lifecycle.CurrentResult.MailProvider, reused);
        var canonical = await store.GetAsync(lifecycle.ValidationId, cancellationToken).ConfigureAwait(false);
        if (canonical is null || canonical.LifecycleState != ValidationLifecycleState.Revalidating ||
            canonical.AttemptNumber != message.AttemptNumber)
            return new(RevalidationProcessingDisposition.Stale);
        var coordinated = await coordinator.ProcessRetryResultAsync(
            canonical.ValidationId, canonical.Version, message.AttemptNumber, result, cancellationToken)
            .ConfigureAwait(false);
        return coordinated.Applied
            ? new(coordinated.Lifecycle?.ResultState == ValidationResultState.Provisional
                ? RevalidationProcessingDisposition.Rescheduled
                : RevalidationProcessingDisposition.Completed)
            : new(RevalidationProcessingDisposition.Stale);
    }

    private static string Domain(string email)
    {
        var separator = email.LastIndexOf('@');
        return separator >= 0 && separator < email.Length - 1 ? email[(separator + 1)..] : string.Empty;
    }
}

public sealed class RevalidationMetrics : IRevalidationMetrics, IDisposable
{
    private readonly Meter _meter = new("EmailValidation.Revalidation");
    private readonly Counter<long> _events;
    private readonly Histogram<double> _timeToFinal;
    private readonly Histogram<long> _attemptsToFinal;
    private readonly Histogram<double> _processingLatency;
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public RevalidationMetrics()
    {
        _events = _meter.CreateCounter<long>("email_validation.revalidation.events");
        _timeToFinal = _meter.CreateHistogram<double>("email_validation.revalidation.time_to_final", "s");
        _attemptsToFinal = _meter.CreateHistogram<long>("email_validation.revalidation.attempts_to_final");
        _processingLatency = _meter.CreateHistogram<double>("email_validation.revalidation.processing_latency", "ms");
    }

    public void RecordQueueReceived(MailProvider provider) => Add("queue_received", provider);
    public void RecordWorkerFailure() => Add("worker_failure", MailProvider.Unknown);
    public void RecordProcessingLatency(TimeSpan latency) => _processingLatency.Record(latency.TotalMilliseconds);
    public void RecordScheduled(MailProvider provider) => Add("scheduled", provider);
    public void RecordExecuted(MailProvider provider, bool reusedFreshResult)
    {
        Add("executed", provider);
        if (reusedFreshResult) Add("skipped_fresh_result", provider);
    }
    public void RecordAlreadyFinal() => Add("skipped_already_final", MailProvider.Unknown);
    public void RecordRescheduled(MailProvider provider) => Add("rescheduled", provider);
    public void RecordFinalized(MailProvider provider, EmailValidationStatus previous, EmailValidationStatus current, TimeSpan timeToFinal, int attempts)
    {
        Add("finalized", provider);
        if (previous == EmailValidationStatus.Unknown && current != EmailValidationStatus.Unknown) Add("resolved", provider);
        if (current == EmailValidationStatus.Unknown) Add("final_unknown", provider);
        _events.Add(1,
            new KeyValuePair<string, object?>("event", "outcome_transition"),
            new KeyValuePair<string, object?>("provider", provider.ToString()),
            new KeyValuePair<string, object?>("from", previous.ToString()),
            new KeyValuePair<string, object?>("to", current.ToString()));
        _timeToFinal.Record(timeToFinal.TotalSeconds, Tags(provider));
        _attemptsToFinal.Record(attempts, Tags(provider));
    }
    public void RecordExhausted(MailProvider provider) => Add("exhausted", provider);
    public void RecordDuplicate() => Add("duplicate", MailProvider.Unknown);
    public void RecordStale() => Add("stale", MailProvider.Unknown);
    public void RecordDeadLettered() => Add("dead_lettered", MailProvider.Unknown);
    public void RecordProvisional(MailProvider provider) => Add("provisional", provider);

    public RevalidationMetricsSnapshot GetSnapshot() => new(
        Get("scheduled"), Get("executed"), Get("skipped_fresh_result"), Get("skipped_already_final"),
        Get("rescheduled"), Get("finalized"), Get("exhausted"), Get("duplicate"), Get("stale"), Get("dead_lettered"),
        Get("provisional", true), Get("scheduled", true), Get("executed", true), Get("resolved", true), Get("final_unknown", true));

    public void Dispose() => _meter.Dispose();

    private void Add(string name, MailProvider provider)
    {
        _counts.AddOrUpdate(name, 1, (_, value) => value + 1);
        _counts.AddOrUpdate($"{name}:{provider}", 1, (_, value) => value + 1);
        _events.Add(1, new KeyValuePair<string, object?>("event", name),
            new KeyValuePair<string, object?>("provider", provider.ToString()));
    }
    private long Get(string name, bool microsoft = false) => microsoft
        ? Get($"{name}:{MailProvider.Microsoft365}") + Get($"{name}:{MailProvider.MicrosoftConsumer}")
        : _counts.TryGetValue(name, out var value) ? value : 0;
    private static TagList Tags(MailProvider provider) => new() { { "provider", provider.ToString() } };
}
