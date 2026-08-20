using System.Security.Cryptography;
using EmailValidation.Core;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class CatchAllDetector(
    ISmtpMailboxProbe smtpProbe,
    IOptions<EmailValidationOptions> options) : ICatchAllDetector
{
    private readonly CatchAllOptions _options = options.Value.CatchAll;

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
                attempted > 1 ? 0.95 : 0.82), results);

        if (accepted == attempted)
        {
            if (provider == MailProvider.GoogleWorkspace)
                return WithResults(new(CatchAllStatus.Unknown, attempted, accepted, rejected, ambiguous,
                    "Google Workspace accepted randomized recipients; RCPT acceptance alone is not treated as catch-all proof.",
                    0.35), results);

            if (provider == MailProvider.Microsoft365)
                return WithResults(new(CatchAllStatus.LikelyCatchAll, attempted, accepted, rejected, ambiguous,
                    "Exchange Online Protection accepted every randomized recipient; this indicates gateway or catch-all acceptance, not mailbox existence.",
                    attempted > 1 ? 0.90 : 0.72), results);

            var minimum = Math.Clamp(_options.MinimumAcceptedProbes, 2, 3);
            if (accepted >= minimum)
                return WithResults(new(CatchAllStatus.LikelyCatchAll, attempted, accepted, rejected, ambiguous,
                    $"{accepted} independent randomized recipients were accepted.",
                    Math.Min(0.95, 0.80 + (accepted * 0.05))), results);

            return WithResults(new(CatchAllStatus.Unknown, attempted, accepted, rejected, ambiguous,
                $"A randomized recipient was accepted, but {minimum} accepted probes are required for a likely catch-all classification.",
                0.45), results);
        }

        return WithResults(new(CatchAllStatus.Unknown, attempted, accepted, rejected, ambiguous,
            "Randomized recipient responses were mixed or ambiguous.",
            0.20), results);
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

    private static CatchAllDetectionResult WithResults(
        CatchAllDetectionResult result,
        IReadOnlyList<SmtpProbeResult> results) => result with { ProbeResults = results };
}
