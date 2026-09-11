using System.Security.Cryptography;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class CatchAllDetector(
    ISmtpMailboxProbe smtpProbe,
    IOptions<EmailValidationOptions> options) : ICatchAllDetector
{
    private readonly CatchAllOptions _options = options.Value.CatchAll;
    private readonly string _strategyVersion = options.Value.Policy.ProviderStrategyVersion;

    public async Task<CatchAllDetectionResult> DetectAsync(
        string domain,
        string mxHost,
        MailProvider provider,
        CancellationToken cancellationToken = default)
    {
        var accepted = 0;
        var rejected = 0;
        var ambiguous = 0;
        var attempted = 0;
        var results = new List<SmtpProbeResult>();
        var minimumInitial = Math.Clamp(_options.ProbeCount, 1, 3);
        var maximum = Math.Clamp(Math.Max(minimumInitial, _options.MaxProbeCount), 1, 3);
        for (var index = 0; index < maximum; index++)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var result = await smtpProbe.ProbeAsync(mxHost, $"dwcheck-{token}@{domain}", provider, cancellationToken);
            results.Add(result);
            attempted++;
            if (result.Status == SmtpMailboxStatus.Accepted) accepted++;
            else if (result.Status == SmtpMailboxStatus.Rejected) rejected++;
            else ambiguous++;

            if (attempted >= minimumInitial && !WouldAdditionalProbeMatter(
                    attempted, accepted, rejected, ambiguous, maximum,
                    Math.Clamp(_options.MinimumAcceptedProbes, 2, 3)))
                break;
        }

        if (rejected == attempted)
            return WithResults(new(attempted > 1 ? CatchAllStatus.NotCatchAll : CatchAllStatus.LikelyNotCatchAll,
                attempted, accepted, rejected, ambiguous,
                "Every randomized recipient was explicitly rejected.",
                attempted > 1 ? 0.95 : 0.82)
            {
                ReasonCode = CatchAllReasonCode.RecipientSpecificObserved,
                RecipientBehavior = DomainRecipientBehavior.RecipientSpecific
            }, results);

        if (accepted == attempted)
        {
            var confidence = attempted > 1 ? Math.Min(0.95, 0.80 + (accepted * 0.05)) : 0.72;
            return WithResults(new(CatchAllStatus.Unknown, attempted, accepted, rejected, ambiguous,
                $"The SMTP endpoint accepted {accepted} randomized recipient probe(s). This establishes accept-all behavior at the public SMTP layer, not catch-all routing.",
                confidence)
            {
                ReasonCode = CatchAllReasonCode.AcceptAllObserved,
                RecipientBehavior = DomainRecipientBehavior.AcceptAll
            }, results);
        }

        return WithResults(new(CatchAllStatus.Unknown, attempted, accepted, rejected, ambiguous,
            "Randomized recipient responses were mixed or ambiguous.",
            0.20)
        {
            ReasonCode = CatchAllReasonCode.MixedOrInconclusive
        }, results);
    }

    private static bool WouldAdditionalProbeMatter(
        int attempted,
        int accepted,
        int rejected,
        int ambiguous,
        int maximum,
        int minimumAccepted)
    {
        if (attempted >= maximum || rejected == attempted || accepted >= minimumAccepted) return false;
        if (accepted > 0) return true;
        if (ambiguous == attempted) return attempted < 2;
        return accepted > 0 && rejected > 0 && attempted < 3;
    }

    private CatchAllDetectionResult WithResults(
        CatchAllDetectionResult result,
        IReadOnlyList<SmtpProbeResult> results)
    {
        var now = DateTimeOffset.UtcNow;
        return result with
        {
            ProbeResults = results,
            ObservedAt = now,
            StrategyVersion = _strategyVersion,
            RefreshAttemptedAt = now,
            RefreshInconclusive = result.Status == CatchAllStatus.Unknown &&
                result.EffectiveRecipientBehavior == DomainRecipientBehavior.Unknown
        };
    }
}
