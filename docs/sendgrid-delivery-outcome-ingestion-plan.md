# SendGrid delivery-outcome ingestion implementation plan

Status: design only; not implemented

Prepared: 2026-08-26

Recommended initial rollout: Disabled, then controlled ingestion for one approved SendGrid account/tenant

## Purpose

Connect authoritative SendGrid delivery events to the platform's existing evidence-backed classification foundation. The integration should establish the first real ground-truth loop:

```text
Validation at T0
    -> immutable feature snapshot
    -> legitimate email send at T1
    -> signed SendGrid event at T2
    -> normalized outcome observation
    -> matured, reproducible training/evaluation dataset
```

This feature is for measuring the existing classifier before enabling a learned model. It must not automatically train, promote, or enforce a model.

If production mail already uses a different email service provider, implement that provider first instead of adopting SendGrid only for data collection. Representative outcomes from the real sending path are more valuable than webhook convenience.

## Existing repository capabilities to reuse

Do not create a second outcome subsystem. Reuse:

- `EmailDeliveryOutcomeObservation` and the normalized taxonomy in `EmailValidation.Domain/PredictionModels.cs`.
- `IEmailDeliveryOutcomeIngestionService` and `EmailDeliveryOutcomeIngestionService` in `EmailValidation.Application/EvidenceBackedClassification.cs`.
- `IEmailDeliveryOutcomeObservationStore`, backed by `MongoClassificationEvidenceStore` in production.
- `IEmailCorrelationService` and the existing tenant-scoped HMAC strategy.
- `EmailValidationFeatureSnapshot` and `EmailValidationFeatureSnapshotFactory`.
- the existing Service Bus sender/worker patterns used by revalidation, jobs, and projections.
- `EmailValidationOptionsValidator`, App Configuration, and Key Vault integration.
- the existing metrics in `ClassificationFoundationMetrics`.

MongoDB remains the authoritative observation store. Service Bus is transport only. Elasticsearch may receive a privacy-safe derived projection later and must not become the source of truth.

## Important semantics

SendGrid's `delivered` event means SendGrid delivered the message to the receiving server. It is evidence of technical delivery, not proof of inbox placement, reading, engagement, or future delivery.

Do not use opens or clicks as delivery labels. They are biased by client privacy features, tracking configuration, and user behavior.

Do not interpret every `bounce`, `dropped`, or 5xx response as a nonexistent mailbox. Sender authentication, suppression, reputation, policy, rate, content, and source-IP failures must not become negative mailbox-existence labels.

Do not interpret absence of a bounce as delivery.

Current repository outcome definitions are versioned. If the label meaning needs to change, add a new definition version; do not alter `mailbox-existence-v1`, `delivery-7d-v1`, or `hard-bounce-7d-v1` in place.

Official provider references:

- [SendGrid Event Webhook reference](https://www.twilio.com/docs/sendgrid/for-developers/tracking-events/event)
- [SendGrid signed webhook security](https://www.twilio.com/docs/sendgrid/for-developers/tracking-events/getting-started-event-webhook-security-features)
- [SendGrid Event Webhook overview](https://www.twilio.com/docs/sendgrid/for-developers/tracking-events/twilio-sendgrid-event-webhook-overview)

Re-check the official documentation during implementation. Provider payloads and security guidance can evolve.

## Proposed architecture

```text
SendGrid
  |
  | HTTPS array of signed events
  v
EmailValidation.Api
  SendGrid webhook endpoint
  - bounded raw-body read
  - timestamp/signature verification
  - integration/account lookup
  - strict schema validation
  - PII removal
  |
  | sanitized SendGridOutcomeEnvelopeV1 only
  v
Azure Service Bus queue
  |
  v
EmailValidation.Worker
  - deserialize/version check
  - resolve validation/send correlation
  - provider-specific normalization
  - call IEmailDeliveryOutcomeIngestionService
  - settle duplicate/conflict/retry/dead-letter
  |
  v
MongoClassificationEvidenceStore
```

The webhook endpoint must return success only after every accepted event in the request has been durably queued. It must not wait for Mongo normalization or dataset work.

## Correlating a validation, send attempt, and provider event

The controlled sending path must add non-PII SendGrid `custom_args` to each legitimate email:

```json
{
  "ev_validation_id": "01J...",
  "ev_send_attempt_id": "01J...",
  "ev_correlation_version": "sendgrid-correlation-v1"
}
```

Do not place the recipient email, local part, customer name, tenant name, HMAC key, or arbitrary customer data in `custom_args`. SendGrid warns that these fields should not contain PII and may be retained.

Prefer resolving `EmailCorrelationId` internally from the canonical validation and its tenant rather than sending the HMAC value to SendGrid.

Add a focused send-attempt correlation abstraction if no equivalent source already exists:

```csharp
public interface IEmailSendAttemptCorrelationStore
{
    Task<EmailSendAttemptCorrelation?> GetAsync(
        string sendAttemptId,
        CancellationToken cancellationToken = default);
}

public sealed record EmailSendAttemptCorrelation(
    string SendAttemptId,
    string ValidationId,
    string TenantId,
    string EmailCorrelationId,
    DateTimeOffset SendAttemptAtUtc,
    string SenderContextId,
    string CorrelationVersion);
```

Inspect the real sending application before adding a collection. Reuse its durable send record if it already provides these fields. The webhook payload alone is not authoritative for `SendAttemptAtUtc` or tenant ownership.

The worker must verify:

1. the webhook integration maps to the expected SendGrid account/subuser and tenant;
2. `ev_send_attempt_id` resolves to a durable send record;
3. the send record and `ev_validation_id` agree;
4. the validation belongs to the same tenant;
5. the send occurred after the feature snapshot;
6. the event timestamp is not before the send attempt;
7. the correlation/schema version is supported.

Missing or cross-tenant correlation must never be guessed from the raw email address. Treat it as an uncorrelated event, record a low-cardinality diagnostic, and route it to review/dead-letter according to policy.

## Webhook endpoint

Suggested route:

```text
POST /webhooks/v1/sendgrid/events/{integrationId}
```

`integrationId` must be an opaque configured identifier. It must not be a tenant name. It selects the expected public verification key, SendGrid account/subuser identity, and authorized tenant scope.

Do not place this endpoint under the normal JWT-protected customer `/v1` group. It uses provider webhook authentication:

- require `X-Twilio-Email-Event-Webhook-Signature`;
- require the SendGrid timestamp header specified by the current official documentation;
- verify the signature over the exact raw request bytes using the configured public key;
- reject invalid, missing, or malformed signatures;
- enforce a bounded timestamp/replay policy compatible with legitimate webhook retries;
- optionally apply documented SendGrid IP allowlisting as defense in depth, not as a substitute for signature validation;
- cap body bytes and maximum event count before allocation;
- accept only `application/json`;
- apply a dedicated rate limiter;
- never log the raw body, recipient address, response text, custom arguments, or signature.

Read and retain the raw body only in memory long enough to verify the signature. After verification, deserialize into a provider DTO and immediately construct a sanitized queue envelope.

Recommended response behavior:

| Condition | Response |
|---|---|
| Signature valid and all accepted events durably queued | 2xx |
| Unsupported but well-formed event type | 2xx after recording an ignored-event metric; do not repeatedly retry it |
| Invalid signature, unsupported integration, malformed JSON, oversized body | 4xx |
| Service Bus unavailable or durable enqueue incomplete | 5xx so the provider can retry |

Confirm SendGrid's current retry behavior before finalizing status codes.

## PII-free Service Bus contract

Do not enqueue the original SendGrid JSON because it contains the recipient email. Define a versioned sanitized message:

```csharp
public sealed record SendGridOutcomeEnvelopeV1(
    int MessageVersion,
    string SourceEventId,
    string IntegrationId,
    string ValidationId,
    string SendAttemptId,
    string CorrelationVersion,
    string EventType,
    string? BounceType,
    string? EnhancedStatusCode,
    int? SmtpReplyCode,
    string? NormalizedProviderReasonCode,
    string? ProviderMessageIdHash,
    DateTimeOffset EventAtUtc,
    DateTimeOffset ReceivedAtUtc);
```

The exact shape should follow actual SendGrid payload evidence, but it must never contain:

- raw email or local part;
- subject, content, or headers;
- raw SMTP response text;
- arbitrary `custom_args`;
- customer names or tenant names;
- API keys or signatures.

If provider message identity is required for conflict investigation, store a keyed hash or bounded non-identifying provider identifier after privacy review.

Service Bus message properties:

```text
MessageId       = "sendgrid:{integrationId}:{sg_event_id}"
CorrelationId   = validationId
Subject         = "sendgrid-delivery-outcome-v1"
ContentType     = "application/json"
messageVersion  = 1
eventType       = normalized low-cardinality provider event name
```

Use `sg_event_id` as the source event identifier and deduplication input. The normalized outcome observation ID should remain deterministic:

```text
sendgrid:{integrationId}:{sg_event_id}
```

Do not use a random GUID for redelivered events.

## Provider normalization policy

Create one provider-specific normalizer in Infrastructure; do not put SendGrid DTOs or event names in Domain or generic Application policy.

Conceptual contract:

```csharp
public interface ISendGridOutcomeNormalizer
{
    OutcomeNormalizationResult Normalize(
        SendGridOutcomeEnvelopeV1 source,
        EmailSendAttemptCorrelation correlation);
}
```

Initial mapping:

| SendGrid event/evidence | Normalized outcome | Confidence | Notes |
|---|---|---|---|
| `delivered` | `Delivered` | High | Receiving server acceptance; not inbox placement |
| `bounce` with recipient-specific `5.1.1` or equivalent authoritative unknown-user evidence | `HardBounce` | Authoritative or High | Valid negative mailbox evidence |
| Other permanent recipient-specific unknown-mailbox response | `HardBounce` | High | Require stage/status/reason normalization |
| `bounce` caused by authentication, reputation, source IP, sender, relay, content, or provider policy | `RejectedBySenderPolicy` or `UnknownOutcome` | High/Medium | Must not label mailbox nonexistent |
| `deferred` | `SoftBounce` | High | Unresolved for binary delivery/mailbox training |
| `dropped` caused by suppression | `Suppressed` | High | Excluded from mailbox-existence target |
| `dropped` caused by sender/account policy | `RejectedBySenderPolicy` | High | Excluded from mailbox-existence target |
| `spamreport` | `Complaint` | Authoritative | Separate risk outcome; not mailbox existence |
| `processed` | no observation or `UnknownOutcome` | Low | Not delivery proof |
| open/click/unsubscribe/group events | ignored | N/A | Not technical-delivery labels |
| unknown event or ambiguous reason | `UnknownOutcome` | Low | Preserve uncertainty |

The normalizer should use the repository's existing SMTP enhanced-status and normalized-reason concepts where appropriate. Do not invent provider-specific mailbox-invalid rules in the API controller or worker.

Construct `EmailDeliveryOutcomeObservation` with:

- `OutcomeEventId`: deterministic `sendgrid:{integrationId}:{SourceEventId}`;
- `EmailCorrelationId`: resolved internally from the approved send/validation correlation;
- `TenantId` and `ValidationId`: from the authoritative correlation record;
- `Outcome`, `Confidence`, enhanced status, and normalized reason: from the normalizer;
- `OutcomeSource`: stable value such as `sendgrid-event-webhook`;
- `SourceEventId`: SendGrid `sg_event_id`;
- `Provider`: recipient provider captured at validation/send time when available, not SendGrid itself;
- `SendAttemptAtUtc`: authoritative send record;
- `ObservedAtUtc`: provider event timestamp;
- `NormalizationVersion`: `sendgrid-outcome-normalization-v1`.

## Worker settlement and failure policy

Follow the existing Service Bus worker conventions:

- complete after `Inserted`;
- complete after `Duplicate`;
- complete after an auditable `Conflict`, but increment `outcome_conflict_total` and retain the conflicting observation;
- abandon/retry on transient Mongo, Key Vault, or dependency failure;
- dead-letter unsupported message versions, impossible timestamps, missing required identifiers, or permanently unresolvable correlation;
- cap delivery attempts through queue configuration;
- do not initiate SMTP validation or retries from an outcome event;
- do not alter canonical validation status synchronously.

The normalized ingestion service already validates temporal ordering, unknown-outcome confidence, duplicates, and conflicts. Extend it only when provider-independent behavior is genuinely missing.

## Configuration

Add a focused options group rather than reusing revalidation or projection settings:

```json
{
  "EmailValidation": {
    "DeliveryOutcomeIngestion": {
      "Enabled": false,
      "Provider": "SendGrid",
      "QueueName": "email-validation-delivery-outcomes",
      "ProvisionQueue": false,
      "MaxConcurrentCalls": 4,
      "PrefetchCount": 50,
      "MaxAutoLockRenewalMinutes": 5,
      "MaxDeliveryCount": 10,
      "MaximumWebhookBodyBytes": 1048576,
      "MaximumEventsPerRequest": 1000,
      "MaximumSignatureAgeMinutes": 10,
      "NormalizationVersion": "sendgrid-outcome-normalization-v1",
      "Integrations": {
        "opaque-integration-id": {
          "TenantId": "configured-tenant-id",
          "PublicVerificationKey": "Key Vault reference or resolved secret",
          "ExpectedAccountId": "configured-account-or-subuser-id"
        }
      }
    }
  }
}
```

Exact fields should be adjusted to the current SendGrid signature scheme. Public keys, Service Bus credentials, and provider account identifiers belong in App Configuration/Key Vault or the established secret-file bootstrap—not source control.

Startup validation should reject:

- enabled ingestion without MongoDB authority;
- missing queue or Service Bus configuration;
- duplicate/empty integration IDs;
- missing tenant/account association;
- invalid public key material;
- unsafe body/event limits;
- missing normalization version;
- collection/queue name collisions where relevant.

## Telemetry and reconciliation

Add low-cardinality metrics:

```text
sendgrid_webhook_requests_total{result}
sendgrid_webhook_signature_failure_total{reason}
sendgrid_webhook_events_total{event_type,result}
sendgrid_webhook_enqueue_failure_total{reason}
sendgrid_outcome_normalized_total{outcome,confidence}
sendgrid_outcome_uncorrelated_total{reason}
sendgrid_outcome_worker_total{disposition}
sendgrid_outcome_latency_seconds{stage}
outcome_ingested_total
outcome_duplicate_total
outcome_conflict_total
```

Never use email, local part, raw domain, validation ID, send-attempt ID, provider message ID, tenant ID, or arbitrary reason text as metric labels.

Create a reconciliation report for the pilot:

- SendGrid event count by event type;
- webhook request/event count received;
- sanitized queue count;
- normalized observation count;
- duplicate/conflict/dead-letter count;
- uncorrelated count;
- end-to-end latency;
- counts by recipient-provider segment where privacy policy permits.

The counts should reconcile for a fixed UTC window before the data is approved for evaluation.

## Testing requirements

### Signature and API tests

- valid signed request is accepted;
- body modification after signing is rejected;
- invalid/missing signature is rejected;
- wrong integration key is rejected;
- stale/replayed timestamp follows documented policy;
- oversized body and excessive event count are rejected before large allocation;
- JSON array and event fields are bounded;
- Service Bus failure returns a retryable HTTP status;
- endpoint logs and queued payload contain no raw email or message content.

Use a checked-in non-secret SendGrid test public/private key pair or official signature fixture solely for tests. Never use a production key.

### Serialization and queue tests

- deterministic `MessageId` from `sg_event_id`;
- same event retry produces the same message/outcome ID;
- unsupported message version is rejected;
- malformed timestamp/identifier is rejected;
- sanitized envelope round-trips without PII;
- event batches preserve all accepted events;
- partial enqueue failure does not acknowledge the request as completely durable.

### Normalization tests

- delivered -> Delivered/High;
- recipient-specific 5.1.1 -> HardBounce/High or Authoritative;
- sender authentication 5xx does not become HardBounce;
- reputation/source-IP block does not become mailbox invalid;
- deferred -> SoftBounce;
- suppression drop -> Suppressed;
- sender-policy drop -> RejectedBySenderPolicy;
- spam report -> Complaint;
- processed/open/click are not delivery positives;
- unknown event/reason remains UnknownOutcome/Low;
- normalization version is recorded.

### Correlation and privacy tests

- valid send attempt joins the expected snapshot/outcome;
- send before snapshot is excluded from training;
- event before send is rejected;
- tenant mismatch is rejected;
- unknown validation/send attempt is not joined by raw email;
- HMAC key unavailable does not fabricate correlation;
- raw email/local part never reaches Service Bus, Mongo outcome documents, logs, metrics, manifests, or Elasticsearch.

### Worker and persistence tests

- Inserted, Duplicate, and Conflict settlement;
- transient Mongo failure retries;
- permanent invalid correlation dead-letters;
- out-of-order events remain auditable;
- soft bounce followed by delivery resolves according to the versioned outcome definition;
- hard bounce followed by authoritative delivery is flagged for label review;
- Mongo indexes and queries work on MongoDB 4.4.31/FCV 4.4;
- older outcome documents remain readable.

### Dataset/evaluation tests

- only outcomes after `SnapshotAtUtc` join;
- unresolved and right-censored observations are not negatives;
- provider/sender context is retained as an authorized feature/segment;
- grouped mailbox, unseen-domain, calibration, and final out-of-time splits remain separate;
- dataset hash and manifest are stable;
- heuristic calibration report is generated before any model training.

## Suggested implementation sequence

1. Confirm the actual sending path and SendGrid account/subuser/tenant ownership model.
2. Confirm where authoritative send attempts are persisted; reuse that store if possible.
3. Define `SendGridOutcomeEnvelopeV1` and its serializer/validator.
4. Add options, startup validation, secret resolution, and disabled defaults.
5. Implement signed webhook verification against official fixtures.
6. Implement the PII-stripping API adapter and durable Service Bus dispatcher.
7. Implement or reuse send-attempt/validation correlation resolution.
8. Implement the provider normalizer with explicit reason tests.
9. Add the worker and settlement policy.
10. Add Mongo/Service Bus integration tests and failure tests.
11. Deploy Disabled, then enable for a test SendGrid stream/account.
12. Reconcile provider, queue, and Mongo counts.
13. Run a controlled tenant/sender pilot with legitimate transactional mail.
14. Allow the configured outcome window to mature.
15. Build the first versioned dataset and measure the existing heuristic.
16. Only if sufficiency gates pass, train/calibrate a baseline and move it to Shadow.

## Rollout gates

Do not approve collected outcomes for model evaluation until:

- webhook signature verification is enforced;
- raw email is absent from queue/Mongo/log/metric samples;
- provider-to-ingested event reconciliation is acceptably complete;
- duplicate handling is proven under webhook retry;
- cross-tenant correlation tests pass;
- uncorrelated and conflict rates are understood;
- recipient-provider and time coverage are documented;
- label confidence and normalization rules are reviewed;
- MongoDB 4.4 compatibility is verified;
- operational alerts and a disable switch are available.

Do not move model rollout beyond Disabled merely because ingestion is enabled. Data must mature, pass sufficiency checks, survive label-quality review, and support out-of-time evaluation first.

## Definition of done

The feature is complete when:

1. A signed SendGrid webhook can be received without customer JWT authentication.
2. The signature is verified against the exact raw payload before parsing.
3. Only a versioned PII-free envelope is queued.
4. A durable send attempt and tenant-owned validation resolve the email correlation.
5. Provider events normalize without turning sender/provider policy failures into mailbox-invalid labels.
6. Ingestion is idempotent and conflicting events remain auditable.
7. MongoDB is authoritative and remains compatible with 4.4/FCV 4.4.
8. API/worker failures cause appropriate retry/dead-letter behavior without affecting live validation.
9. Raw email is absent from Service Bus, outcome Mongo documents, logs, metrics, manifests, and Elasticsearch.
10. Provider, queue, and Mongo event counts reconcile for the pilot window.
11. The existing heuristic can be evaluated from matured outcomes by probability/evidence bands and provider segments.
12. No model is automatically trained, promoted, or enforced.

## Decisions required before implementation

- Which SendGrid account/subuser and tenant will supply the pilot traffic?
- Which application owns the legitimate SendGrid send call and can attach correlation arguments?
- Where is the authoritative send attempt currently stored?
- Is `delivered` approved as a positive for `mailbox-existence-v1`, or should a narrower `mailbox-existence-v2` be defined?
- Which permanent bounce reasons are approved as recipient-specific hard-bounce labels?
- What retention and deletion policy applies to send correlations and outcome observations?
- What minimum label/time/provider coverage gates must governance approve?
- Who approves the first dataset, normalization policy, and future Shadow model card?
