using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.IntegrationTests;

public sealed class ServiceBusRevalidationDispatchTests
{
    [ServiceBusTest]
    [Trait("Category", "ServiceBusIntegration")]
    public async Task DuplicateDetection_PreservesNewGenerationAndRedeliversExpiredLock()
    {
        var connection = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_SERVICEBUS")!;
        var queue = $"ev04-test-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(connection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await admin.CreateQueueAsync(new CreateQueueOptions(queue)
        {
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(1),
            LockDuration = TimeSpan.FromSeconds(30),
            MaxDeliveryCount = 5
        }, timeout.Token);
        try
        {
            var settings = new EmailValidationOptions();
            settings.Revalidation.ServiceBus.ConnectionString = connection;
            settings.Revalidation.ServiceBus.QueueName = queue;
            var options = Options.Create(settings);
            var serializer = new JsonRevalidationMessageSerializer();
            var now = DateTimeOffset.UtcNow;
            var first = new EmailRevalidationMessageV1(Guid.NewGuid().ToString("N"), 2, 2, now, now,
                now.AddSeconds(3), "GenericSmtp", EmailValidationStatus.Unknown, DetailedStatus.Unknown, "1", 2, 1);
            var deferred = first with { DispatchGeneration = 2 };
            await using (var scheduler = Scheduler())
                Assert.True((await scheduler.ScheduleAsync(new(first, first.ScheduledRetryAt), timeout.Token)).Succeeded);
            // Restart after publication but before acknowledging the durable outbox.
            await using (var restarted = Scheduler())
            {
                Assert.True((await restarted.ScheduleAsync(new(first, first.ScheduledRetryAt), timeout.Token)).Succeeded);
                Assert.True((await restarted.ScheduleAsync(new(deferred, deferred.ScheduledRetryAt), timeout.Token)).Succeeded);
            }
            await using var client = new ServiceBusClient(connection);
            await using var receiver = client.CreateReceiver(queue);
            var one = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(20), timeout.Token);
            var two = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(20), timeout.Token);
            Assert.NotNull(one);
            Assert.NotNull(two);
            Assert.NotEqual(one.MessageId, two.MessageId);
            Assert.Contains(first.MessageId, new[] { one.MessageId, two.MessageId });
            Assert.Contains(deferred.MessageId, new[] { one.MessageId, two.MessageId });
            Assert.True(serializer.TryDeserialize(one.Body.ToMemory(), out var decoded, out _));
            Assert.Equal(one.MessageId, decoded!.MessageId);
            await receiver.CompleteMessageAsync(two, timeout.Token);
            var remaining = one.LockedUntil - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, timeout.Token);
            await using var restartedReceiver = client.CreateReceiver(queue);
            var redelivered = await restartedReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(15), timeout.Token);
            Assert.NotNull(redelivered);
            Assert.Equal(one.MessageId, redelivered.MessageId);
            Assert.True(redelivered.DeliveryCount > one.DeliveryCount);
            await restartedReceiver.CompleteMessageAsync(redelivered, timeout.Token);
            Assert.Null(await restartedReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(3), timeout.Token));

            AzureServiceBusRevalidationScheduler Scheduler() => new(options, serializer,
                NullLogger<AzureServiceBusRevalidationScheduler>.Instance);
        }
        finally { await admin.DeleteQueueAsync(queue, CancellationToken.None); }
    }
}

public sealed class ServiceBusTestAttribute : FactAttribute
{
    public ServiceBusTestAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_SERVICEBUS")) ||
            Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_SERVICEBUS_ALLOW_PROVISION") != "1")
            Skip = "Requires a designated test Service Bus namespace and explicit temporary-queue provisioning opt-in.";
    }
}
