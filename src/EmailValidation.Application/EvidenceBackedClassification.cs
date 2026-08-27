using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Application;

public static class EvidenceBackedClassificationVersions
{
    public const string FeatureSchemaV1 = "email-validation-features-v1";
    public const string BuilderV1 = "training-dataset-builder-v1";
    public const string DefaultDecisionPolicyV1 = "classification-decision-policy-v1";
}

public interface IOutcomeDefinitionCatalog
{
    OutcomeDefinition Resolve(PredictionTargetKind target, string version);
    IReadOnlyList<OutcomeDefinition> GetAll();
}

public sealed class OutcomeDefinitionCatalog : IOutcomeDefinitionCatalog
{
    private static readonly OutcomeDefinition[] Definitions =
    [
        new(PredictionTargetKind.MailboxExistence, "mailbox-existence-v1", TimeSpan.FromDays(7),
            Set(EmailDeliveryOutcome.Delivered), Set(EmailDeliveryOutcome.HardBounce),
            Set(EmailDeliveryOutcome.SoftBounce, EmailDeliveryOutcome.UnknownOutcome),
            Set(EmailDeliveryOutcome.Complaint, EmailDeliveryOutcome.Suppressed,
                EmailDeliveryOutcome.RejectedBySenderPolicy),
            "duplicate event IDs are ignored", "conflicts remain auditable and the row is excluded",
            "authoritative provider event, provider event, internal delivery event, customer assertion",
            "delivery-outcome-normalization-v1"),
        new(PredictionTargetKind.TechnicalDeliveryWithinWindow, "delivery-7d-v1", TimeSpan.FromDays(7),
            Set(EmailDeliveryOutcome.Delivered), Set(EmailDeliveryOutcome.HardBounce),
            Set(EmailDeliveryOutcome.SoftBounce, EmailDeliveryOutcome.UnknownOutcome),
            Set(EmailDeliveryOutcome.Complaint, EmailDeliveryOutcome.Suppressed,
                EmailDeliveryOutcome.RejectedBySenderPolicy),
            "duplicate event IDs are ignored", "conflicts remain auditable and the row is excluded",
            "authoritative provider event, provider event, internal delivery event, customer assertion",
            "delivery-outcome-normalization-v1"),
        new(PredictionTargetKind.HardBounceWithinWindow, "hard-bounce-7d-v1", TimeSpan.FromDays(7),
            Set(EmailDeliveryOutcome.HardBounce), Set(EmailDeliveryOutcome.Delivered),
            Set(EmailDeliveryOutcome.SoftBounce, EmailDeliveryOutcome.UnknownOutcome),
            Set(EmailDeliveryOutcome.Complaint, EmailDeliveryOutcome.Suppressed,
                EmailDeliveryOutcome.RejectedBySenderPolicy),
            "duplicate event IDs are ignored", "conflicts remain auditable and the row is excluded",
            "authoritative provider event, provider event, internal delivery event, customer assertion",
            "delivery-outcome-normalization-v1"),
        new(PredictionTargetKind.VerificationReliability, "verification-reliability-v1", TimeSpan.Zero,
            Set(), Set(), Set(EmailDeliveryOutcome.UnknownOutcome),
            Set(EmailDeliveryOutcome.Delivered, EmailDeliveryOutcome.HardBounce, EmailDeliveryOutcome.SoftBounce,
                EmailDeliveryOutcome.Complaint, EmailDeliveryOutcome.Suppressed,
                EmailDeliveryOutcome.RejectedBySenderPolicy),
            "duplicate event IDs are ignored", "conflicts remain auditable and the row is excluded",
            "validation mechanism evidence only", "verification-reliability-label-v1")
    ];

    public OutcomeDefinition Resolve(PredictionTargetKind target, string version) =>
        Definitions.SingleOrDefault(item => item.Target == target && item.Version == version) ??
        throw new KeyNotFoundException($"Outcome definition '{version}' for '{target}' is not registered.");

    public IReadOnlyList<OutcomeDefinition> GetAll() => Definitions;

    private static HashSet<EmailDeliveryOutcome> Set(params EmailDeliveryOutcome[] values) =>
        new HashSet<EmailDeliveryOutcome>(values);
}

public enum AppendObservationResult { Inserted, Duplicate, Conflict }

public interface IEmailDeliveryOutcomeObservationStore
{
    Task<AppendObservationResult> AppendAsync(
        EmailDeliveryOutcomeObservation observation,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmailDeliveryOutcomeObservation>> QueryAsync(
        DateTimeOffset observedFromUtc,
        DateTimeOffset observedThroughUtc,
        string? tenantId = null,
        CancellationToken cancellationToken = default);
}

public interface IEmailValidationFeatureSnapshotStore
{
    Task<bool> AppendAsync(EmailValidationFeatureSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmailValidationFeatureSnapshot>> QueryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        string featureSchemaVersion,
        string? tenantId = null,
        CancellationToken cancellationToken = default);
}

public sealed record OutcomeIngestionResult(
    AppendObservationResult Status,
    string OutcomeEventId,
    string? RejectionReason = null);

public interface IEmailDeliveryOutcomeIngestionService
{
    Task<OutcomeIngestionResult> IngestAsync(
        EmailDeliveryOutcomeObservation observation,
        CancellationToken cancellationToken = default);
}

public interface IClassificationFoundationMetrics
{
    void RecordOutcome(AppendObservationResult result, EmailDeliveryOutcome outcome);
    void RecordSnapshot(bool created);
    void RecordModelScored(ModelRolloutMode mode, bool succeeded, bool abstained, bool disagreed, TimeSpan elapsed);
    void RecordDataset(TrainingDatasetManifest manifest);
}

public sealed class EmailDeliveryOutcomeIngestionService(
    IEmailDeliveryOutcomeObservationStore store,
    IClassificationFoundationMetrics metrics) : IEmailDeliveryOutcomeIngestionService
{
    public async Task<OutcomeIngestionResult> IngestAsync(
        EmailDeliveryOutcomeObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.OutcomeEventId) ||
            string.IsNullOrWhiteSpace(observation.EmailCorrelationId) ||
            string.IsNullOrWhiteSpace(observation.OutcomeSource) ||
            string.IsNullOrWhiteSpace(observation.NormalizationVersion))
        {
            metrics.RecordOutcome(AppendObservationResult.Conflict, observation.Outcome);
            return new(AppendObservationResult.Conflict, observation.OutcomeEventId,
                "Required outcome identity and normalization fields are missing.");
        }
        if (observation.SendAttemptAtUtc == default || observation.ObservedAtUtc == default ||
            observation.ObservedAtUtc < observation.SendAttemptAtUtc)
        {
            metrics.RecordOutcome(AppendObservationResult.Conflict, observation.Outcome);
            return new(AppendObservationResult.Conflict, observation.OutcomeEventId,
                "Outcome observation time cannot precede the send attempt.");
        }
        if (observation.Outcome == EmailDeliveryOutcome.UnknownOutcome &&
            observation.Confidence > OutcomeConfidence.Low)
        {
            metrics.RecordOutcome(AppendObservationResult.Conflict, observation.Outcome);
            return new(AppendObservationResult.Conflict, observation.OutcomeEventId,
                "Unknown outcomes cannot be asserted with high or authoritative confidence.");
        }

        var result = await store.AppendAsync(observation, cancellationToken).ConfigureAwait(false);
        metrics.RecordOutcome(result, observation.Outcome);
        return new(result, observation.OutcomeEventId);
    }
}

public interface IEmailValidationFeatureSnapshotFactory
{
    Task<EmailValidationFeatureSnapshot?> CreateAsync(
        EmailValidationResult result,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class EmailValidationFeatureSnapshotFactory(
    IEmailCorrelationService correlations,
    TimeProvider timeProvider) : IEmailValidationFeatureSnapshotFactory
{
    public async Task<EmailValidationFeatureSnapshot?> CreateAsync(
        EmailValidationResult result,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (result.NormalizedEmail is null || result.Metadata is null || string.IsNullOrWhiteSpace(request.ValidationId))
            return null;
        var at = timeProvider.GetUtcNow();
        var email = await correlations.TryCreateAsync(request.TenantId, result.NormalizedEmail, cancellationToken)
            .ConfigureAwait(false);
        var domainName = result.NormalizedEmail[(result.NormalizedEmail.LastIndexOf('@') + 1)..];
        var domain = await correlations.TryCreateAsync(request.TenantId, $"domain:{domainName}", cancellationToken)
            .ConfigureAwait(false);
        if (email is null || domain is null) return null;

        var domainEvidence = result.DomainIntelligence;
        var smtp = result.SmtpEvidence;
        var session = result.SmtpSessionEvidence;
        var history = result.HistoricalEvidence ?? HistoricalSignalSummary.Empty;
        var reputation = smtp?.Reputation;
        var snapshotId = StableId(request.ValidationId, result.AttemptNumber,
            EvidenceBackedClassificationVersions.FeatureSchemaV1, result.Metadata.ValidatedAt);
        return new EmailValidationFeatureSnapshot
        {
            SnapshotId = snapshotId,
            ValidationId = request.ValidationId,
            EmailCorrelationId = email.Id,
            DomainCorrelationId = domain.Id,
            TenantId = request.TenantId,
            SnapshotAtUtc = at,
            FeatureSchemaVersion = EvidenceBackedClassificationVersions.FeatureSchemaV1,
            Syntax = new(
                result.Checks.SyntaxValid,
                result.NormalizedEmail is not null,
                result.RequiresSmtpUtf8,
                result.Checks.RoleAccount,
                result.Checks.DisposableDomain,
                domainEvidence?.FreeEmailProvider == true),
            Domain = new(
                result.Checks.DomainExists,
                domainEvidence?.Dns.Status ?? DnsStatus.Failure,
                result.Checks.MxPresent,
                domainEvidence?.Dns.ExplicitNullMx == true,
                result.MxRecords.Count,
                result.UsedImplicitMxFallback,
                result.MailProvider,
                result.Provider?.Confidence ?? 0,
                domainEvidence?.DnsSecurity.State ?? DnsSecurityState.Unknown,
                domainEvidence?.Authentication.Spf.State ?? AuthenticationRecordState.Unknown,
                domainEvidence?.Authentication.Dmarc.State ?? AuthenticationRecordState.Unknown,
                result.Checks.CatchAll,
                result.CatchAll?.Confidence ?? 0,
                result.Metadata.MxTopologyFingerprint),
            Smtp = new(
                result.ProbeDisposition,
                smtp?.Command ?? (session is { Stages.Count: > 0 } ? session.Stages[^1].Stage : null),
                smtp?.ResponseCode,
                smtp?.EnhancedStatusCode,
                result.ProviderValidation?.EffectiveCategory ?? SmtpResponseCategory.NotAttempted,
                smtp?.Intelligence?.Reason,
                session?.RecipientStageReached == true,
                result.Checks.Mailbox == SmtpMailboxStatus.Accepted,
                result.ReasonCodes.Contains(ReasonCode.SenderIdentityRejected),
                result.ReasonCodes.Any(reason => reason is ReasonCode.PolicyBlock or
                    ReasonCode.ProviderVerificationBlocked or ReasonCode.ProviderBlockedVerification),
                result.ReasonCodes.Contains(ReasonCode.Greylisted),
                result.Checks.Mailbox == SmtpMailboxStatus.MailboxFull),
            History = new(
                history.ObservationCount,
                history.TargetAcceptedCount + history.TargetRejectedCount,
                Math.Min(history.TargetAcceptedCount, history.TargetRejectedCount),
                history.VerificationReliability,
                history.VerificationReliabilityLevel,
                history.TargetAcceptanceRate,
                history.RecipientRejectionRate,
                history.TemporaryFailureRate,
                history.RateLimitRate,
                history.GreylistingProbability),
            Operational = new(
                result.Metadata.ResultSource,
                result.AttemptNumber,
                result.EvidenceQuality,
                result.ProbeAttempted,
                reputation?.Mode,
                reputation?.Decision),
            HeuristicEvidenceStrength = result.HeuristicEvidenceStrength,
            HeuristicStatus = result.Status
        };
    }

    private static string StableId(string validationId, int attempt, string schema, DateTimeOffset validatedAt)
    {
        var value = $"{validationId}|{attempt}|{schema}|{validatedAt:O}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

public interface ITrainingDatasetBuilder
{
    Task<TrainingDataset> BuildAsync(TrainingDatasetRequest request, CancellationToken cancellationToken = default);
}

public sealed class TrainingDatasetBuilder(
    IEmailValidationFeatureSnapshotStore snapshots,
    IEmailDeliveryOutcomeObservationStore outcomes,
    IOutcomeDefinitionCatalog definitions,
    IClassificationFoundationMetrics metrics,
    TimeProvider timeProvider) : ITrainingDatasetBuilder
{
    public async Task<TrainingDataset> BuildAsync(
        TrainingDatasetRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.StartUtc >= request.EndUtc || request.MaturationCutoffUtc < request.EndUtc)
            throw new ArgumentException("Dataset time range or maturation cutoff is invalid.", nameof(request));
        var definition = definitions.Resolve(request.Target, request.OutcomeDefinitionVersion);
        var featureRows = await snapshots.QueryAsync(
            request.StartUtc, request.EndUtc, request.FeatureSchemaVersion, request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        var outcomeRows = await outcomes.QueryAsync(
            request.StartUtc, request.MaturationCutoffUtc, request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        var outcomeLookup = outcomeRows
            .Where(item => item.Confidence >= request.MinimumOutcomeConfidence)
            .GroupBy(item => item.EmailCorrelationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.ObservedAtUtc).ToArray(),
                StringComparer.Ordinal);
        var rows = new List<TrainingDatasetRow>();
        var excluded = 0;
        var unresolved = 0;
        var censored = 0;
        foreach (var snapshot in featureRows.OrderBy(item => item.SnapshotAtUtc).ThenBy(item => item.SnapshotId, StringComparer.Ordinal))
        {
            if (request.Providers is { Count: > 0 } && !request.Providers.Contains(snapshot.Domain.Provider))
                continue;
            outcomeLookup.TryGetValue(snapshot.EmailCorrelationId, out var candidateOutcomes);
            var candidates = (candidateOutcomes ?? [])
                .Where(item => item.SendAttemptAtUtc >= snapshot.SnapshotAtUtc &&
                    item.ObservedAtUtc >= item.SendAttemptAtUtc && item.ObservedAtUtc <= request.MaturationCutoffUtc)
                .ToArray();
            var resolved = Resolve(snapshot, candidates, definition, request.MaturationCutoffUtc);
            switch (resolved.State)
            {
                case OutcomeLabelState.Matured:
                    rows.Add(new TrainingDatasetRow(
                        snapshot.SnapshotId, snapshot.EmailCorrelationId, snapshot.DomainCorrelationId,
                        snapshot.SnapshotAtUtc, snapshot, resolved.Label!.Value,
                        resolved.Observation!.OutcomeEventId, resolved.Observation.ObservedAtUtc,
                        resolved.Observation.Confidence));
                    break;
                case OutcomeLabelState.Excluded: excluded++; break;
                case OutcomeLabelState.RightCensored: censored++; break;
                default: unresolved++; break;
            }
        }

        var createdAt = timeProvider.GetUtcNow();
        var hash = HashRows(rows);
        var manifest = new TrainingDatasetManifest(
            $"dataset-{hash[..16]}", createdAt, request.FeatureSchemaVersion,
            request.OutcomeDefinitionVersion, request.StartUtc, request.EndUtc,
            request.MaturationCutoffUtc, rows.Count,
            rows.Count(item => item.Label == BinaryOutcomeLabel.Positive),
            rows.Count(item => item.Label == BinaryOutcomeLabel.Negative),
            excluded, unresolved, censored,
            rows.GroupBy(item => item.Snapshot.Domain.Provider)
                .ToDictionary(group => group.Key, group => group.Count()),
            hash,
            $"snapshots:{featureRows.Count};outcomes:{outcomeRows.Count};cutoff:{request.MaturationCutoffUtc:O}",
            EvidenceBackedClassificationVersions.BuilderV1);
        metrics.RecordDataset(manifest);
        return new(manifest, rows);
    }

    private static ResolvedLabel Resolve(
        EmailValidationFeatureSnapshot snapshot,
        EmailDeliveryOutcomeObservation[] outcomes,
        OutcomeDefinition definition,
        DateTimeOffset cutoff)
    {
        if (outcomes.Length == 0) return new(OutcomeLabelState.Unresolved);
        var labels = outcomes.Select(item =>
            definition.PositiveOutcomes.Contains(item.Outcome) ? BinaryOutcomeLabel.Positive :
            definition.NegativeOutcomes.Contains(item.Outcome) ? BinaryOutcomeLabel.Negative : (BinaryOutcomeLabel?)null)
            .Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();
        if (labels.Length > 1) return new(OutcomeLabelState.Excluded);
        if (labels.Length == 1)
        {
            var observation = outcomes.First(item =>
                (labels[0] == BinaryOutcomeLabel.Positive && definition.PositiveOutcomes.Contains(item.Outcome)) ||
                (labels[0] == BinaryOutcomeLabel.Negative && definition.NegativeOutcomes.Contains(item.Outcome)));
            return new(OutcomeLabelState.Matured, labels[0], observation);
        }
        if (outcomes.Any(item => definition.ExcludedOutcomes.Contains(item.Outcome)))
            return new(OutcomeLabelState.Excluded);
        var lastSend = outcomes.Max(item => item.SendAttemptAtUtc);
        return lastSend + definition.MaturationPeriod > cutoff
            ? new(OutcomeLabelState.RightCensored)
            : new(OutcomeLabelState.Unresolved);
    }

    private static string HashRows(IReadOnlyList<TrainingDatasetRow> rows)
    {
        var canonical = rows.OrderBy(item => item.SnapshotId, StringComparer.Ordinal).Select(item => new
        {
            item.SnapshotId,
            item.Snapshot.FeatureSchemaVersion,
            item.Snapshot.SnapshotAtUtc,
            item.Label,
            item.OutcomeEventId,
            item.OutcomeObservedAtUtc
        });
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))).ToLowerInvariant();
    }

    private sealed record ResolvedLabel(
        OutcomeLabelState State,
        BinaryOutcomeLabel? Label = null,
        EmailDeliveryOutcomeObservation? Observation = null);
}

public sealed record DataSufficiencyPolicy(
    int MinimumMaturedRows,
    int MinimumPositiveRows,
    int MinimumNegativeRows,
    TimeSpan MinimumTimeCoverage,
    int MinimumProviders,
    int MinimumUnseenDomainRows,
    double MaximumUnresolvedFraction,
    double MinimumHighConfidenceFraction);

public sealed record DataSufficiencyAssessment(bool ReadyToModel, IReadOnlyList<string> FailedGates);

public sealed class DataSufficiencyEvaluator
{
    public static DataSufficiencyAssessment Evaluate(
        TrainingDataset dataset,
        DataSufficiencyPolicy policy,
        int totalCandidateSnapshots,
        int highConfidenceRows,
        int unseenDomainRows)
    {
        var failures = new List<string>();
        var manifest = dataset.Manifest;
        if (manifest.TrainingRowCount < policy.MinimumMaturedRows) failures.Add("minimum matured rows");
        if (manifest.PositiveCount < policy.MinimumPositiveRows) failures.Add("minimum positive rows");
        if (manifest.NegativeCount < policy.MinimumNegativeRows) failures.Add("minimum negative rows");
        if (manifest.EndUtc - manifest.StartUtc < policy.MinimumTimeCoverage) failures.Add("minimum time coverage");
        if (manifest.ProviderDistribution.Count < policy.MinimumProviders) failures.Add("minimum provider coverage");
        if (unseenDomainRows < policy.MinimumUnseenDomainRows) failures.Add("minimum unseen-domain test size");
        var unresolvedFraction = totalCandidateSnapshots == 0 ? 1 :
            (manifest.UnresolvedCount + manifest.RightCensoredCount) / (double)totalCandidateSnapshots;
        if (unresolvedFraction > policy.MaximumUnresolvedFraction) failures.Add("maximum unresolved percentage");
        var confidenceFraction = manifest.TrainingRowCount == 0 ? 0 : highConfidenceRows / (double)manifest.TrainingRowCount;
        if (confidenceFraction < policy.MinimumHighConfidenceFraction) failures.Add("minimum label-confidence percentage");
        return new(failures.Count == 0, failures);
    }
}

public sealed record EvaluationSplits(
    IReadOnlyList<TrainingDatasetRow> Training,
    IReadOnlyList<TrainingDatasetRow> Calibration,
    IReadOnlyList<TrainingDatasetRow> OutOfTimeTest,
    IReadOnlyList<TrainingDatasetRow> UnseenDomainTest);

public sealed class LeakageSafeDatasetSplitter
{
    public static EvaluationSplits Split(
        IReadOnlyList<TrainingDatasetRow> rows,
        DateTimeOffset calibrationStartsUtc,
        DateTimeOffset testStartsUtc,
        double unseenDomainFraction = 0.2)
    {
        if (calibrationStartsUtc >= testStartsUtc || unseenDomainFraction is <= 0 or >= 1)
            throw new ArgumentException("Split boundaries are invalid.");
        var grouped = rows.GroupBy(item => item.EmailCorrelationId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.SnapshotAtUtc).First())
            .OrderBy(item => item.SnapshotAtUtc).ToArray();
        var domainKeys = grouped.Select(item => item.DomainCorrelationId).Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var unseenCount = Math.Max(1, (int)Math.Floor(domainKeys.Length * unseenDomainFraction));
        var unseen = domainKeys.TakeLast(unseenCount).ToHashSet(StringComparer.Ordinal);
        var unseenRows = grouped.Where(item => unseen.Contains(item.DomainCorrelationId)).ToArray();
        var temporal = grouped.Where(item => !unseen.Contains(item.DomainCorrelationId)).ToArray();
        return new(
            temporal.Where(item => item.SnapshotAtUtc < calibrationStartsUtc).ToArray(),
            temporal.Where(item => item.SnapshotAtUtc >= calibrationStartsUtc && item.SnapshotAtUtc < testStartsUtc).ToArray(),
            temporal.Where(item => item.SnapshotAtUtc >= testStartsUtc).ToArray(),
            unseenRows);
    }
}

public sealed class ProbabilityModelEvaluator
{
    public static ProbabilityEvaluationReport Evaluate(
        IReadOnlyList<ScoredEvaluationRow> rows,
        IReadOnlySet<string>? trainingDomains = null,
        double positiveThreshold = 0.8,
        double negativeThreshold = 0.2)
    {
        var overall = Calculate(rows, positiveThreshold, negativeThreshold);
        var bands = Enumerable.Range(0, 10).Select(index =>
        {
            var minimum = index / 10d;
            var maximum = (index + 1) / 10d;
            var samples = rows.Where(item => item.Probability >= minimum &&
                (index == 9 ? item.Probability <= maximum : item.Probability < maximum)).ToArray();
            var observed = samples.Length == 0 ? 0 : samples.Count(item => item.Label == BinaryOutcomeLabel.Positive) /
                (double)samples.Length;
            return new ProbabilityBandEvaluation(minimum, maximum, samples.Length,
                samples.Length == 0 ? 0 : samples.Average(item => item.Probability), observed,
                samples.Length == 0 ? 0 : Math.Sqrt(observed * (1 - observed) / samples.Length));
        }).ToArray();
        var providers = rows.GroupBy(item => item.Provider).ToDictionary(
            group => group.Key,
            group => Calculate(group.ToArray(), positiveThreshold, negativeThreshold));
        var unseen = trainingDomains is null
            ? []
            : rows.Where(item => !trainingDomains.Contains(item.DomainCorrelationId)).ToArray();
        return new(overall, bands, providers, Calculate(unseen, positiveThreshold, negativeThreshold));
    }

    private static ProbabilityEvaluationMetrics Calculate(
        IReadOnlyList<ScoredEvaluationRow> rows,
        double positiveThreshold,
        double negativeThreshold)
    {
        if (rows.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var accepted = rows.Where(item => !item.Abstained).ToArray();
        var brier = rows.Average(item => Math.Pow(item.Probability - Label(item), 2));
        var logLoss = rows.Average(item =>
        {
            var probability = Math.Clamp(item.Probability, 1e-15, 1 - 1e-15);
            return -(Label(item) * Math.Log(probability) + (1 - Label(item)) * Math.Log(1 - probability));
        });
        var ece = Enumerable.Range(0, 10).Sum(index =>
        {
            var samples = rows.Where(item => Math.Min(9, (int)(item.Probability * 10)) == index).ToArray();
            return samples.Length == 0 ? 0 : samples.Length / (double)rows.Count * Math.Abs(
                samples.Average(item => item.Probability) - samples.Average(Label));
        });
        var (intercept, slope) = CalibrationLine(rows);
        var predictedPositive = accepted.Where(item => item.Probability >= positiveThreshold).ToArray();
        var predictedNegative = accepted.Where(item => item.Probability <= negativeThreshold).ToArray();
        return new(rows.Count, Round(brier), Round(logLoss), Round(ece), Round(intercept), Round(slope),
            Rate(predictedPositive.Count(item => item.Label == BinaryOutcomeLabel.Negative), predictedPositive.Length),
            Rate(predictedNegative.Count(item => item.Label == BinaryOutcomeLabel.Positive), predictedNegative.Length),
            Round(accepted.Length / (double)rows.Count),
            Round((rows.Count - accepted.Length) / (double)rows.Count));
    }

    private static (double Intercept, double Slope) CalibrationLine(IReadOnlyList<ScoredEvaluationRow> rows)
    {
        var xMean = rows.Average(item => item.Probability);
        var yMean = rows.Average(Label);
        var variance = rows.Sum(item => Math.Pow(item.Probability - xMean, 2));
        if (variance == 0) return (yMean, 0);
        var slope = rows.Sum(item => (item.Probability - xMean) * (Label(item) - yMean)) / variance;
        return (yMean - slope * xMean, slope);
    }

    private static double Label(ScoredEvaluationRow row) =>
        row.Label == BinaryOutcomeLabel.Positive ? 1 : 0;
    private static double Rate(int numerator, int denominator) => denominator == 0 ? 0 : Round(numerator / (double)denominator);
    private static double Round(double value) => Math.Round(value, 6);
}

public interface IClassificationPredictionOrchestrator
{
    Task<EmailValidationPrediction?> ScoreAsync(
        EmailValidationFeatureSnapshot snapshot,
        EmailValidationResult heuristicResult,
        CancellationToken cancellationToken = default);
}

public interface IProbabilityScorer
{
    RawModelPrediction Score(EmailValidationFeatureSnapshot snapshot);
}

public interface IProbabilityCalibrator
{
    CalibratedPrediction Calibrate(RawModelPrediction prediction);
}

public interface IPredictionUncertaintyPolicy
{
    PredictionUncertainty Evaluate(
        CalibratedPrediction prediction,
        EmailValidationFeatureSnapshot snapshot);
}

public interface IValidationDecisionPolicy
{
    ValidationDecision Decide(
        EmailValidationResult heuristicResult,
        CalibratedPrediction prediction,
        PredictionUncertainty uncertainty);
}

public sealed class TransparentPredictionUncertaintyPolicy(
    IOptions<EmailValidationOptions> options) : IPredictionUncertaintyPolicy
{
    private readonly ClassificationModelOptions _options = options.Value.ClassificationModel;

    public PredictionUncertainty Evaluate(
        CalibratedPrediction prediction,
        EmailValidationFeatureSnapshot snapshot)
    {
        if (snapshot.Domain.Provider == MailProvider.Unknown)
            return new(PredictionDisposition.OutOfDistribution, "Provider support is unknown.");
        var missing = MissingFraction(snapshot);
        if (missing > _options.MaximumMissingFeatureFraction)
            return new(PredictionDisposition.InsufficientSupport,
                "Too many prediction-time features are missing.", missing);
        if (snapshot.History.VerificationReliability < _options.MinimumVerificationReliability &&
            snapshot.History.ObservationCount > 0)
            return new(PredictionDisposition.InsufficientSupport,
                "Historical verification reliability is below the supported policy.", missing);
        if (prediction.Probability >= _options.AbstentionLowerBound &&
            prediction.Probability <= _options.AbstentionUpperBound)
            return new(PredictionDisposition.Abstain,
                "The calibrated probability is inside the configured abstention interval.", missing);
        return new(PredictionDisposition.AcceptedPrediction, "Prediction is inside supported policy.", missing);
    }

    private static double MissingFraction(EmailValidationFeatureSnapshot snapshot)
    {
        const int total = 8;
        var missing = 0;
        if (snapshot.Domain.Provider == MailProvider.Unknown) missing++;
        if (string.IsNullOrWhiteSpace(snapshot.Domain.MxTopologyFingerprint)) missing++;
        if (snapshot.Domain.DnsSecurity == DnsSecurityState.Unknown) missing++;
        if (snapshot.Domain.SpfState == AuthenticationRecordState.Unknown) missing++;
        if (snapshot.Domain.DmarcState == AuthenticationRecordState.Unknown) missing++;
        if (snapshot.Domain.CatchAllState is CatchAllStatus.Unknown or CatchAllStatus.NotAttempted) missing++;
        if (snapshot.Smtp.StageReached is null) missing++;
        if (snapshot.History.ObservationCount == 0) missing++;
        return missing / (double)total;
    }
}

public sealed class VersionedValidationDecisionPolicy(
    IOptions<EmailValidationOptions> options) : IValidationDecisionPolicy
{
    private readonly ClassificationModelOptions _options = options.Value.ClassificationModel;

    public ValidationDecision Decide(
        EmailValidationResult heuristicResult,
        CalibratedPrediction prediction,
        PredictionUncertainty uncertainty)
    {
        if (heuristicResult.Status is EmailValidationStatus.Valid or EmailValidationStatus.Invalid or
            EmailValidationStatus.CatchAll)
            return new(heuristicResult.Status, "Deterministic/conclusive heuristic evidence remains authoritative.", true);
        if (uncertainty.Disposition != PredictionDisposition.AcceptedPrediction)
            return new(EmailValidationStatus.Unknown, uncertainty.Reason);
        if (prediction.Probability >= _options.LikelyValidThreshold)
            return new(EmailValidationStatus.LikelyValid, "Calibrated probability exceeds the likely-valid threshold.");
        if (prediction.Probability <= _options.LikelyInvalidThreshold)
            return new(EmailValidationStatus.LikelyInvalid, "Calibrated probability is below the likely-invalid threshold.");
        return new(EmailValidationStatus.Risky, "Calibrated probability is between the approved decision thresholds.");
    }
}

public sealed class ClassificationPredictionOrchestrator(
    IProbabilityScorer scorer,
    IProbabilityCalibrator calibrator,
    IPredictionUncertaintyPolicy uncertaintyPolicy,
    IValidationDecisionPolicy decisionPolicy,
    IClassificationFoundationMetrics metrics,
    IOptions<EmailValidationOptions> options,
    TimeProvider timeProvider) : IClassificationPredictionOrchestrator
{
    private readonly ClassificationModelOptions _options = options.Value.ClassificationModel;

    public Task<EmailValidationPrediction?> ScoreAsync(
        EmailValidationFeatureSnapshot snapshot,
        EmailValidationResult heuristicResult,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Mode == ModelRolloutMode.Disabled)
            return Task.FromResult<EmailValidationPrediction?>(null);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var raw = scorer.Score(snapshot);
            if (!string.Equals(raw.Model.FeatureSchemaVersion, snapshot.FeatureSchemaVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("Model feature schema does not match the prediction snapshot.");
            var calibrated = calibrator.Calibrate(raw);
            if (!double.IsFinite(calibrated.Probability) || calibrated.Probability is < 0 or > 1)
                throw new InvalidOperationException("Calibrator produced an invalid probability.");
            var provenance = calibrated.Model with
            {
                DecisionPolicyVersion = _options.DecisionPolicyVersion,
                ScoredAtUtc = timeProvider.GetUtcNow(),
                RolloutMode = _options.Mode
            };
            calibrated = calibrated with { Model = provenance };
            var uncertainty = uncertaintyPolicy.Evaluate(calibrated, snapshot);
            var decision = decisionPolicy.Decide(heuristicResult, calibrated, uncertainty);
            var prediction = new EmailValidationPrediction
            {
                HeuristicEvidenceStrength = snapshot.HeuristicEvidenceStrength,
                MailboxExistenceProbability = calibrated.Target == PredictionTargetKind.MailboxExistence
                    ? calibrated.Probability : null,
                TechnicalDeliveryProbability = calibrated.Target == PredictionTargetKind.TechnicalDeliveryWithinWindow
                    ? calibrated.Probability : null,
                HardBounceProbability = calibrated.Target == PredictionTargetKind.HardBounceWithinWindow
                    ? calibrated.Probability : null,
                VerificationReliability = snapshot.History.VerificationReliability,
                Uncertainty = uncertainty,
                Model = provenance,
                Decision = decision
            };
            metrics.RecordModelScored(_options.Mode, true,
                uncertainty.Disposition != PredictionDisposition.AcceptedPrediction,
                decision.Status != heuristicResult.Status, started.Elapsed);
            return Task.FromResult<EmailValidationPrediction?>(prediction);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            metrics.RecordModelScored(_options.Mode, false, false, false, started.Elapsed);
            return Task.FromResult<EmailValidationPrediction?>(null);
        }
    }
}

public sealed class DisabledClassificationPredictionOrchestrator : IClassificationPredictionOrchestrator
{
    public Task<EmailValidationPrediction?> ScoreAsync(
        EmailValidationFeatureSnapshot snapshot,
        EmailValidationResult heuristicResult,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<EmailValidationPrediction?>(null);
    }
}

public sealed class EvidenceBackedEmailValidationService(
    IntelligenceEmailValidator inner,
    IEmailValidationFeatureSnapshotFactory snapshotFactory,
    IEmailValidationFeatureSnapshotStore snapshotStore,
    IClassificationPredictionOrchestrator scoring,
    IClassificationFoundationMetrics metrics,
    ILogger<EvidenceBackedEmailValidationService> logger) : IEmailValidationService
{
    public async Task<EmailValidationResult> ValidateAsync(
        string email,
        EmailValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.ValidateAsync(email, request, cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await snapshotFactory.CreateAsync(result, request, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                metrics.RecordSnapshot(false);
                return result;
            }
            var created = await snapshotStore.AppendAsync(snapshot, cancellationToken).ConfigureAwait(false);
            metrics.RecordSnapshot(created);
            var prediction = await scoring.ScoreAsync(snapshot, result, cancellationToken).ConfigureAwait(false);
            if (prediction is null) return result;
            var staged = result with { Prediction = prediction };
            // Shadow and Advisory cannot alter canonical behavior. Enforced still passes
            // through the decision policy, which protects deterministic Valid/Invalid evidence.
            return prediction.Model?.RolloutMode == ModelRolloutMode.Enforced
                ? staged with { Status = prediction.Decision.Status }
                : staged;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metrics.RecordSnapshot(false);
            logger.LogWarning(exception,
                "Evidence-backed scoring was unavailable; the heuristic validation result remains canonical");
            return result;
        }
    }
}
