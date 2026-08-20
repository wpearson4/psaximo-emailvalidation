using System.Collections.Concurrent;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

/// <summary>
/// Maintains the process-wide pool of legitimate configured probe identities.
/// DNS health is cached and sender-specific rejections cause a bounded cooldown.
/// </summary>
public sealed class ProbeSenderHealthChecker(
    IEmailNormalizer normalizer,
    IDnsMailResolver dnsResolver,
    IOptions<EmailValidationOptions> options) : IProbeSenderHealthChecker, IProbeSenderPool, IDisposable
{
    private readonly SmtpOptions _options = options.Value.Smtp;
    private readonly SemaphoreSlim _healthGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SenderState> _states = new(StringComparer.OrdinalIgnoreCase);
    private int _roundRobin = -1;

    public async Task<ProbeSenderHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        var configured = ConfiguredSenders();
        if (configured.Length == 0)
            return new(ProbeSenderHealthStatus.NotConfigured, null, null,
                "EmailValidation:Smtp:ProbeSenders or the legacy ProbeSender must contain an enabled sender.");

        var health = await GetHealthAsync(configured, cancellationToken);
        return health.FirstOrDefault(item => item.IsOperational)
            ?? health[0];
    }

    public async Task<ProbeSenderHealth?> SelectAsync(
        IReadOnlySet<string> excludedSenders,
        CancellationToken cancellationToken = default)
    {
        var configured = ConfiguredSenders();
        if (configured.Length == 0) return null;
        var health = await GetHealthAsync(configured, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var eligible = health.Where(item =>
                item.IsOperational && item.Sender is not null &&
                !excludedSenders.Contains(item.Sender) &&
                (!_states.TryGetValue(item.Sender, out var state) || state.CoolingDownUntil <= now))
            .ToArray();
        if (eligible.Length == 0) return null;

        var next = (int)((uint)Interlocked.Increment(ref _roundRobin) % (uint)eligible.Length);
        return eligible[next];
    }

    public void ReportResult(string sender, SmtpProbeResult result)
    {
        if (!SmtpSenderRotationPolicy.IsSenderSpecificFailure(result)) return;
        var state = _states.GetOrAdd(sender, _ => new SenderState());
        state.CoolingDownUntil = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(_options.SenderCooldownSeconds, 1, 86_400));
    }

    private async Task<IReadOnlyList<ProbeSenderHealth>> GetHealthAsync(
        string[] configured,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (configured.All(sender => _states.TryGetValue(sender, out var state) && state.HealthExpiresAt > now))
            return configured.Select(sender => _states[sender].Health!).ToArray();

        await _healthGate.WaitAsync(cancellationToken);
        try
        {
            var results = new List<ProbeSenderHealth>(configured.Length);
            foreach (var sender in configured)
            {
                var state = _states.GetOrAdd(sender, _ => new SenderState());
                if (state.Health is null || state.HealthExpiresAt <= now)
                {
                    state.Health = await EvaluateAsync(sender, cancellationToken);
                    state.HealthExpiresAt = DateTimeOffset.UtcNow.AddMinutes(
                        Math.Max(1, _options.ProbeSenderHealthCacheMinutes));
                }
                results.Add(state.Health);
            }
            return results;
        }
        finally
        {
            _healthGate.Release();
        }
    }

    private async Task<ProbeSenderHealth> EvaluateAsync(string sender, CancellationToken cancellationToken)
    {
        var normalized = normalizer.Normalize(sender);
        if (!normalized.IsValid || normalized.Domain is null)
            return new(ProbeSenderHealthStatus.InvalidSyntax, sender, null,
                "The configured SMTP probe sender is not a valid email address.");

        var domain = normalized.Domain;
        if (domain.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(domain, "example.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(domain, "example.org", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(domain, "example.net", StringComparison.OrdinalIgnoreCase))
            return new(ProbeSenderHealthStatus.NoMailRouting, normalized.NormalizedEmail, domain,
                "The configured SMTP probe sender uses a reserved or placeholder domain.");

        var dns = await dnsResolver.ResolveAsync(domain, cancellationToken);
        if (dns.Status == DnsStatus.DomainNotFound || !dns.DomainExists)
            return new(ProbeSenderHealthStatus.DomainNotFound, normalized.NormalizedEmail, domain,
                "The configured SMTP probe sender domain does not exist.");
        if (dns.Status is DnsStatus.Timeout or DnsStatus.Failure)
            return new(ProbeSenderHealthStatus.DnsUnavailable, normalized.NormalizedEmail, domain,
                "The configured SMTP probe sender domain could not be checked reliably.");
        if (dns.ExplicitNullMx || !dns.MxPresent)
            return new(ProbeSenderHealthStatus.NoMailRouting, normalized.NormalizedEmail, domain,
                "The configured SMTP probe sender domain has no usable return-path mail route.");

        return new(ProbeSenderHealthStatus.Valid, normalized.NormalizedEmail, domain,
            "The configured probe sender has valid syntax and a usable DNS mail route.");
    }

    private string[] ConfiguredSenders()
    {
        var configuredPool = _options.ProbeSenders
            .Where(sender => sender.Enabled && !string.IsNullOrWhiteSpace(sender.Address))
            .Select(sender => sender.Address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (configuredPool.Length > 0) return configuredPool;
        return string.IsNullOrWhiteSpace(_options.ProbeSender)
            ? []
            : [_options.ProbeSender.Trim()];
    }

    public void Dispose() => _healthGate.Dispose();

    private sealed class SenderState
    {
        public ProbeSenderHealth? Health { get; set; }
        public DateTimeOffset HealthExpiresAt { get; set; }
        public DateTimeOffset CoolingDownUntil { get; set; }
    }
}

internal static class SmtpSenderRotationPolicy
{
    private static readonly string[] SenderMarkers =
        ["sender", "mail from", "from address", "return path", "return-path"];
    private static readonly string[] SourceOrProviderMarkers =
        ["rate limit", "too many", "throttl", "source ip", "your ip", "ip address", "blacklist", "spamhaus", "reputation", "anti-abuse"];

    internal static bool IsSenderSpecificFailure(SmtpProbeResult result)
    {
        var session = result.SessionEvidence;
        var evidence = result.Evidence;
        if (session?.FailedStage != SmtpCommand.MailFrom && evidence?.Command != SmtpCommand.MailFrom)
            return false;
        if (evidence?.Category is SmtpResponseCategory.RateLimited or
            SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted)
            return false;

        var response = evidence?.SanitizedResponse ?? result.Response ?? string.Empty;
        if (SourceOrProviderMarkers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return false;
        return SenderMarkers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
