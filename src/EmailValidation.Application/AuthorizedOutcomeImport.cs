using System.Security.Cryptography;
using System.Text;
using EmailValidation.Core;

namespace EmailValidation.Application;

public enum OutcomeTruthSource { DeliveryEvent, RecipientConfirmation, ManagedDirectory, SyntheticFixture }
public sealed record AuthorizedOutcomeImport(
    string SnapshotId, DateTimeOffset SnapshotAtUtc, string Source, string SourceEventId,
    string AuthorizationReference, EvidenceCohort Cohort, OutcomeTruthSource TruthSource,
    EmailDeliveryOutcome Outcome, DateTimeOffset SendAttemptAtUtc, DateTimeOffset ObservedAtUtc,
    string? EnhancedStatusCode = null);

/// <summary>Imports an already authorized observation. Never sends or probes to create truth.</summary>
public sealed class AuthorizedOutcomeImporter(
    IEmailValidationFeatureSnapshotStore snapshots,
    IEmailDeliveryOutcomeIngestionService ingestion,
    TimeProvider clock)
{
    public async Task<OutcomeIngestionResult> ImportAsync(AuthorizedOutcomeImport input,
        CurrentConsumer consumer, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(consumer.TenantId) || !consumer.Scopes.Contains(EmailValidationScopes.Admin))
            throw new UnauthorizedAccessException("Outcome imports require an administrator with a tenant claim.");
        if (!Token(input.SnapshotId) || !Token(input.Source) || !Token(input.SourceEventId) ||
            !Token(input.AuthorizationReference) || !Enum.IsDefined(input.Outcome) || !Enum.IsDefined(input.TruthSource) ||
            input.Cohort is not (EvidenceCohort.Synthetic or EvidenceCohort.AuthorizedReal))
            throw new ArgumentException("Bounded opaque identities, explicit cohort and valid outcome are required.");
        if ((input.Cohort == EvidenceCohort.Synthetic) != (input.TruthSource == OutcomeTruthSource.SyntheticFixture))
            throw new ArgumentException("Synthetic and authorized real truth sources must remain separate.");
        if (input.Outcome is EmailDeliveryOutcome.MailboxConfirmed or EmailDeliveryOutcome.MailboxAbsent &&
            input.TruthSource == OutcomeTruthSource.DeliveryEvent)
            throw new ArgumentException("A delivery event cannot assert mailbox directory or recipient confirmation truth.");
        if (input.Outcome == EmailDeliveryOutcome.MailboxAbsent && input.TruthSource == OutcomeTruthSource.RecipientConfirmation)
            throw new ArgumentException("Mailbox absence requires managed-directory or recipient-specific rejection evidence.");
        if (input.SnapshotAtUtc == default || input.SendAttemptAtUtc < input.SnapshotAtUtc ||
            input.ObservedAtUtc < input.SendAttemptAtUtc || input.ObservedAtUtc > clock.GetUtcNow() ||
            input.EnhancedStatusCode is { Length: > 16 })
            throw new ArgumentException("Outcome timestamps or enhanced status are invalid.");
        var matches = await snapshots.QueryAsync(input.SnapshotAtUtc, input.SnapshotAtUtc,
            EvidenceBackedClassificationVersions.FeatureSchemaV2, consumer.TenantId, cancellationToken);
        var snapshot = matches.SingleOrDefault(item => item.SnapshotId == input.SnapshotId && item.TenantId == consumer.TenantId)
            ?? throw new KeyNotFoundException("Snapshot was not found in this tenant.");
        // Derive correlation, provider, validation and tenant from stored evidence; callers cannot relabel another mailbox.
        var identity = string.Join('|', consumer.TenantId, input.Source, input.SourceEventId);
        var observation = new EmailDeliveryOutcomeObservation
        {
            OutcomeEventId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant(),
            EmailCorrelationId = snapshot.EmailCorrelationId, ValidationId = snapshot.ValidationId,
            SnapshotId = snapshot.SnapshotId, TenantId = consumer.TenantId, Provider = snapshot.Domain.Provider,
            Outcome = input.Outcome, Confidence = input.Outcome == EmailDeliveryOutcome.UnknownOutcome ? OutcomeConfidence.Low : OutcomeConfidence.Authoritative,
            OutcomeSource = input.Source + ":" + input.TruthSource, SourceEventId = input.SourceEventId,
            AuthorizationReference = input.AuthorizationReference, SubmittedBy = consumer.PrincipalKey,
            Cohort = input.Cohort, SendAttemptAtUtc = input.SendAttemptAtUtc, ObservedAtUtc = input.ObservedAtUtc,
            EnhancedStatusCode = input.EnhancedStatusCode, NormalizationVersion = "authorized-outcome-v1"
        };
        return await ingestion.IngestAsync(observation, cancellationToken);
    }
    private static bool Token(string? value) => value is { Length: > 0 and <= 160 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
