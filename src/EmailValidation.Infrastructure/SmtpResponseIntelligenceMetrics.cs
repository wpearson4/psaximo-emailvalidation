using System.Diagnostics;
using System.Diagnostics.Metrics;
using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

public sealed class SmtpResponseIntelligenceMetrics : ISmtpResponseIntelligenceMetrics
{
    private static readonly Meter Meter = new("EmailValidation.SmtpResponseIntelligence", "1.0.0");
    private static readonly Counter<long> ClassifiedCounter = Meter.CreateCounter<long>("smtp_response_classified_total");
    private static readonly Counter<long> AgreementCounter = Meter.CreateCounter<long>("smtp_response_agreement_total");
    private static readonly Counter<long> DisagreementCounter = Meter.CreateCounter<long>("smtp_response_disagreement_total");
    private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>("smtp_response_candidate_failure_total");
    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>(
        "smtp_response_classification_duration_ms", "ms");
    private static readonly Counter<long> ReasonDisagreement = Meter.CreateCounter<long>(
        "smtp_normalized_reason_disagreement_total");
    private static readonly Counter<long> MailboxDisagreement = Meter.CreateCounter<long>(
        "smtp_mailbox_impact_disagreement_total");
    private static readonly Counter<long> ResultStateDisagreement = Meter.CreateCounter<long>(
        "smtp_result_state_disagreement_total");
    private static readonly Counter<long> RetryDisagreement = Meter.CreateCounter<long>(
        "smtp_retry_decision_disagreement_total");
    private static readonly Counter<long> CooldownDisagreement = Meter.CreateCounter<long>(
        "smtp_cooldown_decision_disagreement_total");
    private static readonly Counter<long> RotationDisagreement = Meter.CreateCounter<long>(
        "smtp_rotation_decision_disagreement_total");
    private static readonly Counter<long> OutboundHealthDisagreement = Meter.CreateCounter<long>(
        "smtp_outbound_health_decision_disagreement_total");
    private long _classified;
    private long _agreements;
    private long _disagreements;
    private long _candidateFailures;

    public void Record(SmtpResponseRolloutObservation observation)
    {
        var tags = new TagList
        {
            { "mode", observation.Mode.ToString() },
            { "provider", observation.Provider.ToString() },
            { "stage", observation.Stage.ToString() },
            { "reason", observation.CandidateReason.ToString() }
        };
        Interlocked.Increment(ref _classified);
        ClassifiedCounter.Add(1, tags);
        Latency.Record(observation.ClassificationLatencyMilliseconds, tags);
        if (observation.CandidateFailed)
        {
            Interlocked.Increment(ref _candidateFailures);
            FailureCounter.Add(1, tags);
        }
        else if (observation.Agreement)
        {
            Interlocked.Increment(ref _agreements);
            AgreementCounter.Add(1, tags);
        }
        else
        {
            Interlocked.Increment(ref _disagreements);
            DisagreementCounter.Add(1, tags);
        }
        if (!observation.NormalizedReasonAgreement) ReasonDisagreement.Add(1, tags);
        if (!observation.MailboxImpactAgreement) MailboxDisagreement.Add(1, tags);
        if (!observation.ResultStateAgreement) ResultStateDisagreement.Add(1, tags);
        if (!observation.RetryDecisionAgreement) RetryDisagreement.Add(1, tags);
        if (!observation.CooldownDecisionAgreement) CooldownDisagreement.Add(1, tags);
        if (!observation.RotationDecisionAgreement) RotationDisagreement.Add(1, tags);
        if (!observation.OutboundHealthDecisionAgreement) OutboundHealthDisagreement.Add(1, tags);
    }

    public SmtpResponseIntelligenceMetricsSnapshot GetSnapshot() => new(
        Interlocked.Read(ref _classified),
        Interlocked.Read(ref _agreements),
        Interlocked.Read(ref _disagreements),
        Interlocked.Read(ref _candidateFailures));
}
