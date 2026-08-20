using System.Collections.Concurrent;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class DomainSmtpProbeThrottle : ISmtpProbeThrottle, IDisposable
{
    private readonly SmtpOptions _options;
    private readonly SemaphoreSlim _globalGate;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _domainGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<MailProvider, SemaphoreSlim> _providerGates = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequest = new(StringComparer.OrdinalIgnoreCase);

    public DomainSmtpProbeThrottle(IOptions<EmailValidationOptions> options)
    {
        _options = options.Value.Smtp;
        _globalGate = new SemaphoreSlim(Math.Max(1, _options.GlobalConcurrency));
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        SmtpThrottleContext context,
        CancellationToken cancellationToken = default)
    {
        var domainGate = _domainGates.GetOrAdd(
            context.Domain,
            _ => new SemaphoreSlim(Math.Max(1, _options.PerDomainConcurrency)));
        var providerGate = _providerGates.GetOrAdd(
            context.Provider,
            _ => new SemaphoreSlim(Math.Max(1, _options.PerProviderConcurrency)));
        await _globalGate.WaitAsync(cancellationToken);
        var providerAcquired = false;
        try
        {
            await providerGate.WaitAsync(cancellationToken);
            providerAcquired = true;
            await domainGate.WaitAsync(cancellationToken);
        }
        catch
        {
            if (providerAcquired) providerGate.Release();
            _globalGate.Release();
            throw;
        }

        try
        {
            if (_lastRequest.TryGetValue(context.Domain, out var last))
            {
                var minimum = TimeSpan.FromMilliseconds(Math.Max(0, _options.DelayBetweenDomainRequestsMilliseconds));
                var remaining = minimum - (DateTimeOffset.UtcNow - last);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
            }
        }
        catch
        {
            domainGate.Release();
            providerGate.Release();
            _globalGate.Release();
            throw;
        }
        return new Lease(this, context.Domain, domainGate, providerGate);
    }

    private sealed class Lease(
        DomainSmtpProbeThrottle owner,
        string domain,
        SemaphoreSlim domainGate,
        SemaphoreSlim providerGate) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner._lastRequest[domain] = DateTimeOffset.UtcNow;
                domainGate.Release();
                providerGate.Release();
                owner._globalGate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        _globalGate.Dispose();
        foreach (var gate in _domainGates.Values) gate.Dispose();
        foreach (var gate in _providerGates.Values) gate.Dispose();
    }
}
