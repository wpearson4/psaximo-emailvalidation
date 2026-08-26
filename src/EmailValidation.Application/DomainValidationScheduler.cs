using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Application;

/// <summary>Fair, async, batch-scoped round-robin scheduling by normalized domain.</summary>
public sealed class DomainValidationScheduler(
    IEmailValidator validator,
    IEmailNormalizer normalizer,
    IOptions<EmailValidationOptions> options,
    ILogger<DomainValidationScheduler> logger) : IDomainValidationScheduler
{
    private readonly SchedulingOptions _options = options.Value.Scheduling;
    private readonly int _legacyGlobalConcurrency = options.Value.Smtp.GlobalConcurrency;
    private readonly int _legacyPerDomainConcurrency = options.Value.Smtp.PerDomainConcurrency;
    private long _scheduled;
    private long _completed;
    private int _uniqueDomains;
    private int _activeDomains;
    private int _maximumQueueDepth;

    public async Task<IReadOnlyList<ValidationWorkResult>> ScheduleAsync(
        IReadOnlyList<ValidationWorkItem> items,
        CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<long, ValidationWorkResult>();
        await foreach (var result in ScheduleStreamingAsync(items, cancellationToken))
            results[result.Sequence] = result;
        return items.Select(item => results[item.Sequence]).ToArray();
    }

    public async IAsyncEnumerable<ValidationWorkResult> ScheduleStreamingAsync(
        IReadOnlyList<ValidationWorkItem> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) yield break;
        Interlocked.Add(ref _scheduled, items.Count);
        var groups = Group(items);
        _uniqueDomains = groups.Count;
        _maximumQueueDepth = groups.Count == 0 ? 0 : groups.Max(group => group.Value.Count);
        var globalConcurrency = Math.Max(1,
            _options.GlobalConcurrency > 0 ? _options.GlobalConcurrency : _legacyGlobalConcurrency);
        var completed = Channel.CreateBounded<ValidationWorkResult>(new BoundedChannelOptions(
            Math.Max(1, Math.Min(items.Count, globalConcurrency * 2)))
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(groups, completed.Writer, producerCancellation.Token);

        try
        {
            await foreach (var result in completed.Reader.ReadAllAsync(cancellationToken))
                yield return result;
        }
        finally
        {
            // An async-stream consumer may stop before draining the bounded result
            // channel. Cancel the producer so iterator disposal cannot deadlock on
            // backpressure from a reader that no longer exists.
            producerCancellation.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Cancellation belongs to early iterator disposal, not the caller.
            }
        }
    }

    private async Task ProduceAsync(
        Dictionary<string, Queue<ValidationWorkItem>> groups,
        ChannelWriter<ValidationWorkResult> completed,
        CancellationToken cancellationToken)
    {
        try
        {
            var maxActiveDomains = Math.Max(1, _options.MaxActiveDomains);
            foreach (var wave in groups.Chunk(maxActiveDomains))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Exchange(ref _activeDomains, wave.Length);
                await ProcessWaveAsync(wave, completed, cancellationToken);
            }
            completed.TryComplete();
        }
        catch (Exception exception)
        {
            completed.TryComplete(exception);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _activeDomains, 0);
        }
    }

    public DomainSchedulerSnapshot GetSnapshot() => new(
        Interlocked.Read(ref _scheduled),
        Interlocked.Read(ref _completed),
        Volatile.Read(ref _uniqueDomains),
        Volatile.Read(ref _activeDomains),
        Volatile.Read(ref _maximumQueueDepth));

    private async Task ProcessWaveAsync(
        KeyValuePair<string, Queue<ValidationWorkItem>>[] wave,
        ChannelWriter<ValidationWorkResult> completed,
        CancellationToken cancellationToken)
    {
        var queues = wave.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var sync = new object();
        var remaining = wave.Sum(pair => pair.Value.Count);
        var perDomain = Math.Max(1, _options.PerDomainConcurrency > 0
            ? _options.PerDomainConcurrency
            : _legacyPerDomainConcurrency);
        var readyCapacity = Math.Max(1, wave.Sum(pair => Math.Min(perDomain, pair.Value.Count)));
        var ready = Channel.CreateBounded<string>(new BoundedChannelOptions(readyCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        foreach (var pair in wave)
        {
            var initial = Math.Min(perDomain, pair.Value.Count);
            for (var index = 0; index < initial; index++) ready.Writer.TryWrite(pair.Key);
        }

        var workerCount = Math.Min(remaining, Math.Max(1,
            _options.GlobalConcurrency > 0 ? _options.GlobalConcurrency : _legacyGlobalConcurrency));
        async Task ProcessReadyAsync()
        {
            await foreach (var domain in ready.Reader.ReadAllAsync(cancellationToken))
            {
                ValidationWorkItem item;
                lock (sync) item = queues[domain].Dequeue();
                EmailValidationResult result;
                try
                {
                    result = await validator.ValidateAsync(item.Email, item.Request, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    logger.LogWarning(exception, "Validation work item {Sequence} failed; continuing with Unknown", item.Sequence);
                    result = FailedValidation(item.Email);
                }
                await completed.WriteAsync(
                    new(item.Sequence, result, DateTimeOffset.UtcNow), cancellationToken);
                Interlocked.Increment(ref _completed);

                var shouldContinue = false;
                var finish = false;
                lock (sync)
                {
                    shouldContinue = queues[domain].Count > 0;
                    remaining--;
                    finish = remaining == 0;
                }
                if (shouldContinue) ready.Writer.TryWrite(domain);
                if (finish) ready.Writer.TryComplete();
            }
        }

        var workers = Enumerable.Range(0, workerCount).Select(_ => ProcessReadyAsync()).ToArray();
        await Task.WhenAll(workers);
    }

    private Dictionary<string, Queue<ValidationWorkItem>> Group(IReadOnlyList<ValidationWorkItem> items)
    {
        var groups = new Dictionary<string, Queue<ValidationWorkItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var normalized = normalizer.Normalize(item.Email);
            // Invalid input still gets its own ready queue and never creates a synthetic hot domain.
            var domain = normalized.Domain ?? $"\0invalid:{item.Sequence}";
            if (!groups.TryGetValue(domain, out var queue)) groups[domain] = queue = new();
            queue.Enqueue(item);
        }
        return groups;
    }

    private static EmailValidationResult FailedValidation(string email) => new()
    {
        Email = email,
        Status = EmailValidationStatus.Unknown,
        Confidence = 0,
        ConfidenceReason = "Validation could not be completed for this row.",
        UnknownContext = UnknownValidationContextBuilder.ExecutionFailure(
            "Validation could not be completed because an application or dependency operation failed."),
        Checks = new EmailValidationChecks()
    };
}
