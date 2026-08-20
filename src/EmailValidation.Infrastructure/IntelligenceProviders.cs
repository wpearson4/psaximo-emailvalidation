using System.Diagnostics;
using System.Net.Sockets;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class EmailTypoDetector(IOptions<EmailValidationOptions> options) : IEmailTypoDetector
{
    private readonly Dictionary<string, string> _knownTypos =
        new Dictionary<string, string>(options.Value.Intelligence.CommonDomainTypos, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownDomains = new(
        options.Value.Intelligence.FreeEmailDomains
            .Concat(options.Value.Intelligence.CommonDomainTypos.Values),
        StringComparer.OrdinalIgnoreCase);

    public TypoDetectionResult Detect(string localPart, string domain)
    {
        if (_knownDomains.Contains(domain)) return TypoDetectionResult.None;
        if (_knownTypos.TryGetValue(domain, out var explicitSuggestion))
            return Suggest(localPart, explicitSuggestion, 0.99);

        var candidates = _knownDomains
            .Where(candidate => DamerauLevenshteinDistance(domain, candidate) == 1)
            .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.Length == 1
            ? Suggest(localPart, candidates[0], 0.90)
            : TypoDetectionResult.None;
    }

    private static TypoDetectionResult Suggest(string localPart, string domain, double confidence) =>
        new(true, domain, $"{localPart}@{domain}", confidence);

    internal static int DamerauLevenshteinDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;
        var matrix = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++) matrix[i, 0] = i;
        for (var j = 0; j <= right.Length; j++) matrix[0, j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 &&
                    char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 2]) &&
                    char.ToLowerInvariant(left[i - 2]) == char.ToLowerInvariant(right[j - 1]))
                    matrix[i, j] = Math.Min(matrix[i, j], matrix[i - 2, j - 2] + 1);
            }
        }
        return matrix[left.Length, right.Length];
    }
}

public sealed class FreeEmailProviderDetector(IOptions<EmailValidationOptions> options) : IFreeEmailProviderDetector
{
    private readonly HashSet<string> _domains = new(
        options.Value.Intelligence.FreeEmailDomains,
        StringComparer.OrdinalIgnoreCase);

    public bool IsFreeProvider(string domain) => DisposableEmailDetector.MatchesDomainOrParent(_domains, domain);
}

public sealed class ToxicDomainDetector(IOptions<EmailValidationOptions> options) : IToxicDomainDetector
{
    private readonly HashSet<string> _domains = new(
        options.Value.Intelligence.ToxicDomains,
        StringComparer.OrdinalIgnoreCase);

    public Task<ToxicDomainResult> EvaluateAsync(string domain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = DisposableEmailDetector.MatchesDomainOrParent(_domains, domain)
            ? new ToxicDomainResult(ToxicDomainStatus.KnownToxic, 0.99, EvidenceSource.ConfiguredIntelligenceProvider)
            : new ToxicDomainResult(ToxicDomainStatus.NoEvidence, 0, EvidenceSource.LocalIntelligence);
        return Task.FromResult(result);
    }
}

public sealed class SpamTrapRiskDetector(IOptions<EmailValidationOptions> options) : ISpamTrapRiskDetector
{
    private static readonly HashSet<string> SuspiciousLocalParts = new(
        ["spamtrap", "honeypot"],
        StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownAddresses = new(
        options.Value.Intelligence.KnownSpamTrapAddresses,
        StringComparer.OrdinalIgnoreCase);

    public Task<SpamTrapRiskResult> EvaluateAsync(string email, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SpamTrapRiskResult result;
        if (_knownAddresses.Contains(email))
            result = new(SpamTrapRiskStatus.KnownSpamTrap, 0.99, EvidenceSource.ConfiguredIntelligenceProvider);
        else
        {
            var at = email.LastIndexOf('@');
            result = at > 0 && SuspiciousLocalParts.Contains(email[..at])
                ? new(SpamTrapRiskStatus.PossibleSpamTrap, 0.45, EvidenceSource.Heuristic)
                : new(SpamTrapRiskStatus.NoEvidence, 0, EvidenceSource.Heuristic);
        }
        return Task.FromResult(result);
    }
}

public sealed class AbuseRiskProvider(IOptions<EmailValidationOptions> options) : IAbuseRiskProvider
{
    private readonly HashSet<string> _addresses = new(
        options.Value.Intelligence.AbuseRiskAddresses,
        StringComparer.OrdinalIgnoreCase);

    public Task<AbuseRiskResult> EvaluateAsync(string email, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_addresses.Contains(email)
            ? new AbuseRiskResult(AbuseRiskStatus.KnownRisk, 0.99, EvidenceSource.ConfiguredIntelligenceProvider)
            : AbuseRiskResult.Unknown);
    }
}

public sealed class SuppressionIntelligenceProvider(IOptions<EmailValidationOptions> options) : ISuppressionIntelligenceProvider
{
    private readonly Dictionary<string, string> _addresses =
        new Dictionary<string, string>(options.Value.Intelligence.SuppressedAddresses, StringComparer.OrdinalIgnoreCase);

    public Task<SuppressionResult> EvaluateAsync(string email, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_addresses.TryGetValue(email, out var reason)
            ? new SuppressionResult(SuppressionStatus.Suppressed, reason, EvidenceSource.ConfiguredIntelligenceProvider)
            : new SuppressionResult(SuppressionStatus.NotSuppressed, null, EvidenceSource.ConfiguredIntelligenceProvider));
    }
}

public sealed class MxForwardDetector(IOptions<EmailValidationOptions> options) : IMxForwardDetector
{
    private readonly IReadOnlyDictionary<string, string> _suffixes =
        new Dictionary<string, string>(options.Value.Intelligence.MxForwardingSuffixes, StringComparer.OrdinalIgnoreCase);

    public MxForwardResult Evaluate(string domain, IReadOnlyList<MxRecord> mxRecords)
    {
        foreach (var record in mxRecords)
        {
            var host = record.Host.Trim().TrimEnd('.');
            var match = _suffixes.FirstOrDefault(item =>
                host.Equals(item.Key, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith('.' + item.Key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key))
                return new(MxForwardStatus.ConfirmedForwarding, match.Value, 0.95,
                    EvidenceSource.ConfiguredIntelligenceProvider);
        }
        return new(MxForwardStatus.NoEvidence, null, 0, EvidenceSource.LocalIntelligence);
    }
}

public sealed class UnavailableDomainAgeProvider : IDomainAgeProvider
{
    public Task<DomainAgeResult> GetAgeAsync(string domain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DomainAgeResult.Unknown);
    }
}

public sealed class UnknownEmailIdentityIntelligenceProvider : IEmailIdentityIntelligenceProvider
{
    public Task<EmailIdentityResult> EvaluateAsync(string email, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EmailIdentityResult.Unknown);
    }
}

public sealed class MailInfrastructureInspector(IOptions<EmailValidationOptions> options) : IMailInfrastructureInspector
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.Dns.TimeoutSeconds));

    public async Task<MailInfrastructureResult> InspectAsync(
        string domain,
        DnsLookupResult dns,
        CancellationToken cancellationToken = default)
    {
        if (dns.Status != DnsStatus.Success) return MailInfrastructureResult.Unknown;
        if (dns.ExplicitNullMx || !dns.MxPresent)
            return new(MailInfrastructureStatus.Unroutable, [], [], 0.99);

        var watch = Stopwatch.StartNew();
        var hosts = dns.MxRecords.Select(record => record.Host).Distinct(StringComparer.OrdinalIgnoreCase);
        var resolutions = await Task.WhenAll(hosts.Select(host => ResolveHostAsync(host, cancellationToken)));
        var resolved = resolutions.Where(item => item.Resolved).Select(item => item.Host).ToList();
        var unusable = resolutions.Where(item => item.Unusable).Select(item => item.Host).ToList();
        var transientFailure = resolutions.Any(item => item.TransientFailure);
        watch.Stop();
        var status = resolved.Count > 0
            ? MailInfrastructureStatus.Routable
            : transientFailure
                ? MailInfrastructureStatus.Unknown
                : MailInfrastructureStatus.Unroutable;
        var confidence = status switch
        {
            MailInfrastructureStatus.Routable => 0.95,
            MailInfrastructureStatus.Unroutable => 0.95,
            _ => 0.25
        };
        return new(status, resolved, unusable, confidence, watch.ElapsedMilliseconds);
    }

    private async Task<HostResolution> ResolveHostAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token);
            return addresses.Length > 0
                ? new(host, Resolved: true, Unusable: false, TransientFailure: false)
                : new(host, Resolved: false, Unusable: true, TransientFailure: false);
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            return new(host, Resolved: false, Unusable: true, TransientFailure: false);
        }
        catch (Exception exception) when ((exception is SocketException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return new(host, Resolved: false, Unusable: false, TransientFailure: true);
        }
    }

    private sealed record HostResolution(string Host, bool Resolved, bool Unusable, bool TransientFailure);
}

public sealed class EmailIntelligenceEvaluator(
    IEmailTypoDetector typoDetector,
    ISpamTrapRiskDetector spamTrapDetector,
    IAbuseRiskProvider abuseRiskProvider,
    ISuppressionIntelligenceProvider suppressionProvider,
    IEmailIdentityIntelligenceProvider identityProvider) : IEmailIntelligenceEvaluator
{
    public async Task<EmailAddressIntelligence> EvaluateAsync(
        string email,
        string localPart,
        string domain,
        CancellationToken cancellationToken = default)
    {
        var trapTask = spamTrapDetector.EvaluateAsync(email, cancellationToken);
        var abuseTask = abuseRiskProvider.EvaluateAsync(email, cancellationToken);
        var suppressionTask = suppressionProvider.EvaluateAsync(email, cancellationToken);
        var identityTask = identityProvider.EvaluateAsync(email, cancellationToken);
        await Task.WhenAll(trapTask, abuseTask, suppressionTask, identityTask);
        return new EmailAddressIntelligence
        {
            Email = email,
            Typo = typoDetector.Detect(localPart, domain),
            SpamTrapRisk = await trapTask,
            AbuseRisk = await abuseTask,
            Suppression = await suppressionTask,
            Identity = await identityTask
        };
    }
}

public sealed class DomainIntelligenceEvaluator(
    IDisposableDomainIntelligenceProvider disposableProvider,
    IFreeEmailProviderDetector freeEmailDetector,
    IToxicDomainDetector toxicDomainDetector,
    IMxForwardDetector mxForwardDetector,
    IDomainAgeProvider domainAgeProvider,
    IMailInfrastructureInspector mailInfrastructureInspector) : IDomainIntelligenceEvaluator
{
    public async Task<SupplementalDomainIntelligence> EvaluateAsync(
        string domain,
        DnsLookupResult dns,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var toxicTask = toxicDomainDetector.EvaluateAsync(domain, cancellationToken);
        var ageTask = domainAgeProvider.GetAgeAsync(domain, cancellationToken);
        var infrastructureTask = mailInfrastructureInspector.InspectAsync(domain, dns, cancellationToken);
        await Task.WhenAll(toxicTask, ageTask, infrastructureTask);
        watch.Stop();
        return new(
            disposableProvider.Evaluate(domain),
            freeEmailDetector.IsFreeProvider(domain),
            await toxicTask,
            mxForwardDetector.Evaluate(domain, dns.MxRecords),
            await ageTask,
            await infrastructureTask,
            watch.ElapsedMilliseconds);
    }
}
