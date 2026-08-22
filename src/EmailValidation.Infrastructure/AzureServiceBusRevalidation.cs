using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class JsonRevalidationMessageSerializer : IRevalidationMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public byte[] Serialize(EmailRevalidationMessageV1 message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public bool TryDeserialize(
        ReadOnlyMemory<byte> payload,
        out EmailRevalidationMessageV1? message,
        out string? failureReason)
    {
        try
        {
            message = JsonSerializer.Deserialize<EmailRevalidationMessageV1>(payload.Span, Options);
            if (message is null)
            {
                failureReason = "Payload was empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(message.ValidationId) || message.AttemptNumber < 2 ||
                message.MaximumAttempts < message.AttemptNumber || message.OriginalValidatedAt == default ||
                message.PreviousAttemptAt == default || message.ScheduledRetryAt < message.PreviousAttemptAt ||
                !Enum.IsDefined(message.PreviousStatus) || !Enum.IsDefined(message.PreviousSubStatus))
            {
                message = null;
                failureReason = "Payload is missing required revalidation fields.";
                return false;
            }
            failureReason = null;
            return true;
        }
        catch (JsonException exception)
        {
            message = null;
            failureReason = exception.Message;
            return false;
        }
    }
}

public sealed class AzureServiceBusRevalidationScheduler : IRevalidationScheduler, IAsyncDisposable
{
    private readonly ServiceBusRevalidationOptions _options;
    private readonly IRevalidationMessageSerializer _serializer;
    private readonly ILogger<AzureServiceBusRevalidationScheduler> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private ServiceBusClient? _client;
    private ServiceBusSender? _sender;

    public AzureServiceBusRevalidationScheduler(
        IOptions<EmailValidationOptions> options,
        IRevalidationMessageSerializer serializer,
        ILogger<AzureServiceBusRevalidationScheduler> logger)
    {
        _options = options.Value.Revalidation.ServiceBus;
        _serializer = serializer;
        _logger = logger;
    }

    public async Task<RevalidationScheduleResult> ScheduleAsync(
        RevalidationRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sender = await GetSenderAsync(cancellationToken).ConfigureAwait(false);
            var message = new ServiceBusMessage(BinaryData.FromBytes(_serializer.Serialize(request.Message)))
            {
                MessageId = request.Message.MessageId,
                CorrelationId = request.Message.ValidationId,
                Subject = "email-validation-retry",
                ContentType = "application/json"
            };
            message.ApplicationProperties["messageVersion"] = request.Message.MessageVersion;
            message.ApplicationProperties["attemptNumber"] = request.Message.AttemptNumber;
            var sequence = await sender.ScheduleMessageAsync(
                message, request.ScheduledAt, cancellationToken).ConfigureAwait(false);
            return new(true, request.Message.MessageId, request.ScheduledAt, sequence);
        }
        catch (ServiceBusException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception,
                "Service Bus scheduling failed for message {MessageId}; the durable outbox will retry",
                request.Message.MessageId);
            return new(false, request.Message.MessageId, request.ScheduledAt, ErrorCode: exception.Reason.ToString());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync().ConfigureAwait(false);
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _sync.Dispose();
    }

    private async Task<ServiceBusSender> GetSenderAsync(CancellationToken cancellationToken)
    {
        if (_sender is not null) return _sender;
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sender is not null) return _sender;
            _client = new ServiceBusClient(_options.ConnectionString);
            _sender = _client.CreateSender(_options.QueueName);
            return _sender;
        }
        finally
        {
            _sync.Release();
        }
    }
}

public sealed class DisabledRevalidationScheduler : IRevalidationScheduler
{
    public Task<RevalidationScheduleResult> ScheduleAsync(
        RevalidationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new RevalidationScheduleResult(
            false, request.Message.MessageId, request.ScheduledAt, ErrorCode: "revalidation_disabled"));
}

public sealed class RevalidationInfrastructureInitializer(
    IOptions<EmailValidationOptions> options,
    IRevalidationPersistenceInitializer persistence,
    ILogger<RevalidationInfrastructureInitializer> logger) : IRevalidationInfrastructureInitializer
{
    private readonly EmailValidationOptions _options = options.Value;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Revalidation.Enabled) return;
        await persistence.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var queue = _options.Revalidation.ServiceBus;
        if (!queue.ProvisionQueue) return;
        var administration = new ServiceBusAdministrationClient(queue.ConnectionString);
        if (await administration.QueueExistsAsync(queue.QueueName, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("Service Bus retry queue {QueueName} already exists", queue.QueueName);
            return;
        }

        var create = new CreateQueueOptions(queue.QueueName)
        {
            MaxDeliveryCount = queue.MaxDeliveryCount,
            RequiresDuplicateDetection = queue.EnableDuplicateDetection
        };
        if (queue.EnableDuplicateDetection)
            create.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(queue.DuplicateDetectionMinutes);
        await administration.CreateQueueAsync(create, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Provisioned Service Bus retry queue {QueueName}", queue.QueueName);
    }
}
