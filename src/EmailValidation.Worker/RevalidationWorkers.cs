using Azure.Messaging.ServiceBus;
using System.Diagnostics;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Worker;

public sealed class ServiceBusRevalidationWorker(
    IOptions<EmailValidationOptions> options,
    IRevalidationMessageSerializer serializer,
    IEmailRevalidationProcessor processor,
    IRevalidationMetrics metrics,
    ILogger<ServiceBusRevalidationWorker> logger) : BackgroundService
{
    private readonly RevalidationOptions _options = options.Value.Revalidation;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Email revalidation worker is disabled");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        await using var client = new ServiceBusClient(_options.ServiceBus.ConnectionString);
        await using var receiver = client.CreateProcessor(_options.ServiceBus.QueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            MaxConcurrentCalls = _options.ServiceBus.MaxConcurrentCalls,
            PrefetchCount = _options.ServiceBus.PrefetchCount,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(_options.ServiceBus.MaxAutoLockRenewalMinutes)
        });
        receiver.ProcessMessageAsync += ProcessMessageAsync;
        receiver.ProcessErrorAsync += ProcessErrorAsync;
        await receiver.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
        logger.LogInformation("Listening for email revalidation on queue {QueueName}", _options.ServiceBus.QueueName);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await receiver.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);

        async Task ProcessMessageAsync(ProcessMessageEventArgs args)
        {
            var stopwatch = Stopwatch.StartNew();
            if (!serializer.TryDeserialize(args.Message.Body.ToMemory(), out var message, out var failureReason))
            {
                metrics.RecordDeadLettered();
                metrics.RecordProcessingLatency(stopwatch.Elapsed);
                await args.DeadLetterMessageAsync(
                    args.Message, "invalid_payload", Truncate(failureReason), args.CancellationToken).ConfigureAwait(false);
                return;
            }
            metrics.RecordQueueReceived(Enum.TryParse<MailProvider>(message!.Provider, true, out var provider)
                ? provider : MailProvider.Unknown);
            try
            {
                var result = await processor.ProcessAsync(message, args.CancellationToken).ConfigureAwait(false);
                switch (result.Disposition)
                {
                    case RevalidationProcessingDisposition.Completed:
                    case RevalidationProcessingDisposition.Rescheduled:
                    case RevalidationProcessingDisposition.Stale:
                    case RevalidationProcessingDisposition.AlreadyFinal:
                        await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
                        break;
                    case RevalidationProcessingDisposition.RetryInfrastructureFailure:
                        await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case RevalidationProcessingDisposition.DeadLetter:
                        metrics.RecordDeadLettered();
                        await args.DeadLetterMessageAsync(args.Message,
                            result.DeadLetterReason ?? "invalid_message",
                            Truncate(result.DeadLetterDescription), args.CancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                metrics.RecordWorkerFailure();
                logger.LogError(exception, "Email revalidation failed for message {MessageId}", args.Message.MessageId);
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                metrics.RecordProcessingLatency(stopwatch.Elapsed);
            }
        }

        Task ProcessErrorAsync(ProcessErrorEventArgs args)
        {
            logger.LogError(args.Exception, "Service Bus receiver failure from {ErrorSource}", args.ErrorSource);
            return Task.CompletedTask;
        }
    }

    private static string Truncate(string? value) => string.IsNullOrWhiteSpace(value)
        ? "No additional detail."
        : value.Length <= 1024 ? value : value[..1024];
}

public sealed class RevalidationOutboxPublisherService(
    IOptions<EmailValidationOptions> options,
    IRevalidationOutboxDispatcher dispatcher,
    ILogger<RevalidationOutboxPublisherService> logger) : BackgroundService
{
    private readonly RevalidationOptions _options = options.Value.Revalidation;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.OutboxDispatchIntervalSeconds));
        do
        {
            try
            {
                var count = await dispatcher.DispatchPendingAsync(
                    _options.OutboxBatchSize, stoppingToken).ConfigureAwait(false);
                if (count > 0) logger.LogInformation("Dispatched {Count} pending email revalidations", count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Email revalidation outbox dispatch failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
