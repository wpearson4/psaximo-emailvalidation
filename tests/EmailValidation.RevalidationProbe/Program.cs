using System.Globalization;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using EmailValidation.RevalidationProbe;

// A test-only child process: contend for one persisted lease, without SMTP or broker access.
var connection = Environment.GetEnvironmentVariable("EMAIL_VALIDATION_TEST_MONGO")
    ?? throw new InvalidOperationException("A designated test Mongo connection is required.");
var settings = new EmailValidationOptions();
settings.Persistence.DatabaseName = args[0];
settings.Persistence.LifecycleCollection = args[1];
var clock = new ProbeClock(DateTimeOffset.Parse(args[5], CultureInfo.InvariantCulture));
var store = new MongoValidationLifecycleStore(new MongoClient(connection), Options.Create(settings), clock,
    NullLogger<MongoValidationLifecycleStore>.Instance);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
while (!File.Exists(args[4])) await Task.Delay(10, timeout.Token);
var acquired = await store.TryAcquireExecutionAsync(args[2], 1, 2, args[3], TimeSpan.FromMinutes(2), timeout.Token);
Console.WriteLine(acquired is null ? "CONTENDED" : "ACQUIRED");

namespace EmailValidation.RevalidationProbe
{
    public sealed class LeaseProbeMarker;
    internal sealed class ProbeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
