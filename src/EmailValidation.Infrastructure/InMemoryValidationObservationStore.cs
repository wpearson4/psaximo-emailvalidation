using System.Collections.Concurrent;
using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

public sealed class InMemoryValidationObservationStore : IValidationObservationStore
{
    private const int MaximumObservationsPerDomain = 200;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ValidationObservation>> _observations =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ValidationObservation>> GetDomainObservationsAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ValidationObservation> result = _observations.TryGetValue(domain, out var queue)
            ? queue.ToArray()
            : [];
        return Task.FromResult(result);
    }

    public Task RecordAsync(ValidationObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queue = _observations.GetOrAdd(observation.Domain, _ => new ConcurrentQueue<ValidationObservation>());
        queue.Enqueue(observation);
        while (queue.Count > MaximumObservationsPerDomain) queue.TryDequeue(out _);
        return Task.CompletedTask;
    }
}

public sealed class HistoricalSignalAggregator : IHistoricalSignalAggregator
{
    public HistoricalSignalSummary Aggregate(IReadOnlyList<ValidationObservation> observations)
    {
        var mailbox = observations.Where(item => item.Type == ValidationObservationType.MailboxProbe).ToArray();
        var targetAccepted = mailbox.Count(item => item.ResponseCategory is SmtpResponseCategory.Accepted or SmtpResponseCategory.GatewayAccepted);
        var targetRejected = mailbox.Count(item => item.ResponseCategory == SmtpResponseCategory.RecipientRejected);
        var randomAccepted = observations.Sum(item => item.RandomRecipientAcceptedCount);
        var randomProbes = observations.Sum(item => item.RandomRecipientProbeCount);
        var randomRejected = observations.Sum(item => item.RandomRecipientRejectedCount);
        var temporaryFailures = mailbox.Count(item => item.ResponseCategory is SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted);
        var rateLimited = mailbox.Count(item => item.ResponseCategory == SmtpResponseCategory.RateLimited);
        var gatewayAccepted = mailbox.Count(item => item.ResponseCategory == SmtpResponseCategory.GatewayAccepted);
        var totalTemporaryFailures = observations.Count(item => item.ResponseCategory is SmtpResponseCategory.TemporaryFailure or SmtpResponseCategory.Greylisted);
        var totalRateLimited = observations.Count(item => item.ResponseCategory == SmtpResponseCategory.RateLimited);
        var totalGatewayAccepted = observations.Count(item => item.ResponseCategory == SmtpResponseCategory.GatewayAccepted);
        var totalGreylisted = observations.Count(item => item.ResponseCategory == SmtpResponseCategory.Greylisted);

        var targetAcceptanceRate = Rate(targetAccepted, mailbox.Length);
        var randomAcceptanceRate = Rate(randomAccepted, randomProbes);
        var recipientRejectionRate = Rate(targetRejected, mailbox.Length);
        var temporaryFailureRate = Rate(temporaryFailures, mailbox.Length);
        var rateLimitRate = Rate(rateLimited, mailbox.Length);
        var gatewayAcceptanceRate = Rate(gatewayAccepted, mailbox.Length);
        var greylistingProbability = Rate(totalGreylisted, observations.Count);
        var reliability = randomProbes == 0
            ? 0
            : Math.Clamp(
                0.05 + (0.90 * Rate(randomRejected, randomProbes)) + (0.05 * targetAcceptanceRate) -
                (0.30 * temporaryFailureRate) - (0.30 * rateLimitRate),
                0,
                1);
        var reliabilityLevel = reliability switch
        {
            >= 0.80 => VerificationReliabilityLevel.High,
            >= 0.50 => VerificationReliabilityLevel.Medium,
            > 0 => VerificationReliabilityLevel.Low,
            _ => VerificationReliabilityLevel.Unknown
        };

        return new HistoricalSignalSummary(
            observations.Count,
            observations.Count(item => item.CatchAllStatus == CatchAllStatus.LikelyCatchAll),
            observations.Count(item => item.ResponseCategory == SmtpResponseCategory.VerificationBlocked),
            totalGatewayAccepted,
            totalTemporaryFailures,
            totalRateLimited,
            randomAccepted,
            targetAccepted,
            targetRejected,
            randomProbes,
            randomRejected,
            targetAcceptanceRate,
            randomAcceptanceRate,
            recipientRejectionRate,
            temporaryFailureRate,
            rateLimitRate,
            gatewayAcceptanceRate,
            Math.Round(reliability, 2),
            reliabilityLevel,
            totalGreylisted,
            greylistingProbability);
    }

    private static double Rate(int numerator, int denominator) => denominator == 0
        ? 0
        : Math.Round((double)numerator / denominator, 4);
}

public sealed class InMemoryDeliveryOutcomeRecorder : IDeliveryOutcomeStore
{
    private readonly ConcurrentQueue<DeliveryOutcome> _outcomes = new();
    private readonly ConcurrentQueue<DeliveryOutcomeRecord> _records = new();

    public Task RecordOutcomeAsync(DeliveryOutcome outcome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _outcomes.Enqueue(outcome);
        return Task.CompletedTask;
    }

    public Task RecordAsync(DeliveryOutcomeRecord outcome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records.Enqueue(outcome with
        {
            Prediction = outcome.Prediction with { ReasonCodes = outcome.Prediction.ReasonCodes.ToArray() }
        });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeliveryOutcomeRecord>> QueryAsync(
        CalibrationQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DeliveryOutcomeRecord> records = _records.Where(item =>
            (!query.Provider.HasValue || item.Prediction.Provider == query.Provider) &&
            (!query.Status.HasValue || item.Prediction.PredictedStatus == query.Status) &&
            (!query.MinimumConfidence.HasValue || item.Prediction.PredictedConfidence >= query.MinimumConfidence) &&
            (!query.MaximumConfidence.HasValue || item.Prediction.PredictedConfidence <= query.MaximumConfidence) &&
            (!query.CatchAllStatus.HasValue || item.Prediction.CatchAllStatus == query.CatchAllStatus) &&
            (!query.VerificationReliability.HasValue || item.Prediction.VerificationReliability == query.VerificationReliability) &&
            (!query.ReasonCode.HasValue || item.Prediction.ReasonCodes.Contains(query.ReasonCode.Value)) &&
            (query.DomainType is null || string.Equals(item.Prediction.DomainType, query.DomainType, StringComparison.OrdinalIgnoreCase)) &&
            (query.ClassificationPolicyVersion is null || item.Prediction.Policy.ClassificationPolicyVersion == query.ClassificationPolicyVersion) &&
            (query.ProviderStrategyVersion is null || item.Prediction.Policy.ProviderStrategyVersion == query.ProviderStrategyVersion) &&
            (!query.MaximumEvidenceAgeHours.HasValue || item.Prediction.EvidenceAgeHours <= query.MaximumEvidenceAgeHours)).ToArray();
        return Task.FromResult(records);
    }
}
