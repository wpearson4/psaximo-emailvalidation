using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

namespace EmailValidation.Worker;

public sealed class ProjectionOutboxPublisherWorker(
    IOptions<EmailValidationOptions> options,
    IProjectionOutboxDispatcher dispatcher,
    IProjectionOutbox outbox,
    TimeProvider timeProvider,
    ILogger<ProjectionOutboxPublisherWorker> logger) : BackgroundService
{
    private readonly EmailValidationProjectionOptions _options = options.Value.Projection;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.Outbox.DispatchIntervalSeconds));
        do
        {
            try
            {
                var count = await dispatcher.DispatchAsync(stoppingToken).ConfigureAwait(false);
                ProjectionTelemetry.ObserveBacklog(
                    await outbox.GetBacklogAsync(stoppingToken).ConfigureAwait(false), timeProvider.GetUtcNow());
                if (count > 0) logger.LogInformation("Published {Count} validation observation events", count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Validation observation outbox publication failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

public sealed class ElasticsearchProjectionWorker(
    IOptions<EmailValidationOptions> options,
    IElasticsearchObservationSink sink,
    TimeProvider timeProvider,
    ILogger<ElasticsearchProjectionWorker> logger) : BackgroundService
{
    private readonly EmailValidationProjectionOptions _options = options.Value.Projection;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        await using var client = new ServiceBusClient(_options.ServiceBus.ConnectionString);
        await using var receiver = client.CreateReceiver(
            _options.ServiceBus.TopicName,
            _options.ServiceBus.SubscriptionName,
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
                PrefetchCount = _options.ServiceBus.PrefetchCount
            });
        logger.LogInformation("Elasticsearch projector is consuming {Topic}/{Subscription}",
            _options.ServiceBus.TopicName, _options.ServiceBus.SubscriptionName);
        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<ServiceBusReceivedMessage> messages;
            try
            {
                messages = await receiver.ReceiveMessagesAsync(
                    _options.Elasticsearch.MaximumBatchSize,
                    TimeSpan.FromSeconds(_options.Elasticsearch.ReceiveWaitSeconds),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ServiceBusException exception)
            {
                logger.LogError(exception, "Observation subscription receive failed");
                continue;
            }
            if (messages.Count == 0) continue;
            var valid = new List<(ServiceBusReceivedMessage Message, EmailValidationObservationEnvelope Event)>();
            foreach (var message in messages)
            {
                if (!ObservationEventSerializer.TryDeserialize(message.Body.ToMemory(), out var observation, out var reason) ||
                    !string.Equals(message.MessageId, observation?.EventId, StringComparison.Ordinal))
                {
                    await receiver.DeadLetterMessageAsync(message, "invalid_observation",
                        SafeDescription(reason ?? "message_id_mismatch"), stoppingToken).ConfigureAwait(false);
                    ProjectionTelemetry.RecordDeadLetter(message.Subject ?? "unknown", reason);
                    continue;
                }
                valid.Add((message, observation!));
                ProjectionTelemetry.RecordReceived(observation!.EventType);
            }
            if (valid.Count == 0) continue;
            var stopwatch = Stopwatch.StartNew();
            IReadOnlyList<ProjectionIndexResult> results;
            try
            {
                results = await sink.IndexBatchAsync(valid.Select(item => item.Event).ToArray(), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Elasticsearch bulk projection failed before an item response was available");
                foreach (var item in valid)
                    await receiver.AbandonMessageAsync(item.Message, cancellationToken: stoppingToken)
                        .ConfigureAwait(false);
                continue;
            }
            finally
            {
                ProjectionTelemetry.RecordBulkDuration(stopwatch.Elapsed);
            }

            var byId = results.ToDictionary(item => item.EventId, StringComparer.Ordinal);
            var retryDelayApplied = false;
            foreach (var item in valid)
            {
                if (!byId.TryGetValue(item.Event.EventId, out var result))
                    result = new(item.Event.EventId, ProjectionIndexDisposition.Retryable,
                        FailureCategory: "missing_bulk_item_result");
                switch (result.Disposition)
                {
                    case ProjectionIndexDisposition.Indexed:
                        await receiver.CompleteMessageAsync(item.Message, stoppingToken).ConfigureAwait(false);
                        ProjectionTelemetry.RecordIndexed(item.Event.EventType);
                        ProjectionTelemetry.RecordLag(item.Event, timeProvider.GetUtcNow());
                        break;
                    case ProjectionIndexDisposition.Duplicate:
                        await receiver.CompleteMessageAsync(item.Message, stoppingToken).ConfigureAwait(false);
                        ProjectionTelemetry.RecordDuplicate(item.Event.EventType);
                        break;
                    case ProjectionIndexDisposition.Retryable when
                        item.Message.DeliveryCount < _options.Elasticsearch.RetryLimit:
                        if (!retryDelayApplied)
                        {
                            var delay = Math.Min(5_000, _options.Elasticsearch.RetryBackoffMilliseconds *
                                Math.Pow(2, Math.Min(4, Math.Max(0, item.Message.DeliveryCount - 1))));
                            await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken).ConfigureAwait(false);
                            retryDelayApplied = true;
                        }
                        await receiver.AbandonMessageAsync(item.Message, cancellationToken: stoppingToken)
                            .ConfigureAwait(false);
                        ProjectionTelemetry.RecordRetry(item.Event.EventType, result.FailureCategory);
                        break;
                    case ProjectionIndexDisposition.Retryable:
                        await receiver.DeadLetterMessageAsync(item.Message, "projection_retry_exhausted",
                            SafeDescription(result.FailureCategory), stoppingToken).ConfigureAwait(false);
                        ProjectionTelemetry.RecordDeadLetter(item.Event.EventType, "retry_exhausted");
                        break;
                    case ProjectionIndexDisposition.PermanentFailure:
                        await receiver.DeadLetterMessageAsync(item.Message, "projection_rejected",
                            SafeDescription(result.FailureCategory), stoppingToken).ConfigureAwait(false);
                        ProjectionTelemetry.RecordDeadLetter(item.Event.EventType, result.FailureCategory);
                        if (result.FailureCategory?.Contains("mapping", StringComparison.OrdinalIgnoreCase) == true)
                            ProjectionTelemetry.RecordMappingFailure(item.Event.EventType);
                        break;
                }
            }
        }
    }

    private static string SafeDescription(string? value) => string.IsNullOrWhiteSpace(value)
        ? "No additional detail."
        : value.Length <= 1024 ? value : value[..1024];
}

public sealed class ProjectionReconciliationWorker(
    IOptions<EmailValidationOptions> options,
    IProjectionReconciler reconciler,
    ILogger<ProjectionReconciliationWorker> logger) : BackgroundService
{
    private readonly EmailValidationProjectionOptions _options = options.Value.Projection;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.Reconciliation.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.Reconciliation.IntervalMinutes));
        do
        {
            try
            {
                var result = await reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                if (result.EventsEnqueued > 0)
                {
                    ProjectionTelemetry.RecordReconciled(result.EventsEnqueued);
                    logger.LogWarning("Projection reconciliation regenerated {Count} missing outbox events",
                        result.EventsEnqueued);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Projection reconciliation failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
