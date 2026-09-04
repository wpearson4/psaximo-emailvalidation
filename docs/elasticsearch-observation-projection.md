# Elasticsearch observation projection

## Architectural boundary

MongoDB remains authoritative for `EmailValidationDomainIntelligence`,
`EmailValidationMailboxIntelligence`, `EmailValidationLifecycle`, retry state, attempt history,
outbound-identity health, and reusable validation results. Elasticsearch is an asynchronous,
eventually consistent, rebuildable observation projection. No API, SMTP, cache, reuse, retry,
classification, or outbound-identity selection path queries the observation data stream.

The producer persists canonical lifecycle or health state first. It then inserts deterministic
events into `EmailValidationProjectionOutbox`. A failed outbox insert is logged and repaired by
overlapping reconciliation; it never changes the validation result. The publisher sends those
events to a dedicated Service Bus topic. The worker consumes batches and uses Elasticsearch bulk
`create` operations with `_id = eventId`; a 409 is idempotent success.

## Repository gap analysis

This matrix records the pre-change evidence and implemented action. Repository evidence, not the
workstream's expected structure, determined each action.

| Capability | Status | Existing implementation | Gap | Required action |
|---|---|---|---|---|
| Mongo canonical mailbox/domain intelligence | IMPLEMENTED | `MongoValidationIntelligenceStore` and dedicated collections | None | Preserve; no Elasticsearch read was added |
| Canonical lifecycle, retry, and attempts | IMPLEMENTED | `MongoValidationLifecycleStore` with compare-and-set and retry outbox | Observation emission absent | Decorate successful canonical writes |
| Canonical outbound health | IMPLEMENTED | `MongoOutboundIdentityHealthStore` | Material changes were not observable | Decorate state-changing writes |
| Historical domain observation store | IMPLEMENTED | Domain-scoped observations embedded in Mongo intelligence | It is runtime evidence, not a cross-validation analytics projection | Leave authoritative behavior unchanged |
| Versioned observation contracts | NOT IMPLEMENTED | No transport-independent projection contract | No schema/version guardrail | Add three P0 v1 contracts and stable envelope |
| Domain-change observation source | PARTIALLY IMPLEMENTED | Domain documents track change timestamps/counts | No clean material-change event hook or historical change sequence | Do not add `domain-intelligence.changed.v1` |
| Pseudonymous email correlation | NOT IMPLEMENTED | Raw normalized email exists in canonical Mongo | No analytics-safe correlation | Add tenant-scoped HMAC-SHA256 and key version |
| Durable projection outbox | NOT IMPLEMENTED | Retry dispatch data is embedded in lifecycle documents | Retry semantics cannot be reused as analytics events | Add separate Mongo outbox with atomic leases and TTL |
| Service Bus observation topic | NOT IMPLEMENTED | Retry queue and jobs queue only | Different semantics and no topic/subscription | Add dedicated topic publisher and guarded provisioning |
| Elasticsearch observation sink | NOT IMPLEMENTED | Elasticsearch probe-sender read client only | Reader credentials/index cannot be reused safely | Add separate HTTP bulk sink and configuration |
| Projector host | NOT IMPLEMENTED | Existing independently deployed worker host | No observation consumer | Add dedicated hosted services inside the worker |
| Strict data stream mapping and ILM | NOT IMPLEMENTED | No matching template, stream, or lifecycle policy on the inspected cluster | Dynamic auto-creation risk | Add deployment-managed v1 artifacts |
| Partial bulk failure handling | NOT IMPLEMENTED | No bulk writer | Mixed outcomes not handled | Settle each Service Bus message from its matching bulk item |
| Reconciliation/checkpoint | NOT IMPLEMENTED | Canonical lifecycle updated timestamps are indexed in Mongo | Non-transactional outbox gaps could persist | Add Mongo checkpoint, overlap, paging, and idempotent regeneration |
| Historical backfill | NOT IMPLEMENTED | No administrative projection command | No bounded replay path | Add explicit range/batch/max/dry-run command |
| Projection telemetry | NOT IMPLEMENTED | Existing `System.Diagnostics.Metrics` conventions | No lag/backlog/projection metrics | Add low-cardinality counters, histograms, and gauges |
| Failure-isolation tests | PARTIALLY IMPLEMENTED | Existing Mongo/revalidation tests | Projection-specific cases absent | Add contract, privacy, bulk, and guarded Mongo tests |

## Event contracts

- `validation.attempt.observed.v1`: identity input
  `v1|validation.attempt.observed.v1|{validationId}|{attemptNumber}`.
- `validation.lifecycle.changed.v1`: identity input
  `v1|validation.lifecycle.changed.v1|{validationId}|{sequence}`.
- `outbound-identity.health.changed.v1`: identity input
  `v1|outbound-identity.health.changed.v1|{identityId}|{observedTicks}|{state}|{failureCount}`.

The SHA-256 digest of that canonical identity is reused as Mongo `_id`, Service Bus `MessageId`,
and Elasticsearch `_id`. This is an identity hash over non-PII event coordinates, not the email
correlation mechanism.

Raw email, local part, raw SMTP response, raw exception, stack trace, uploaded row, credentials,
tokens, and arbitrary customer fields are excluded. Mailbox correlation is HMAC-SHA256 over
`tenantId + "\n" + normalizedEmail`; the event records the HMAC key version. A missing/short key
causes correlation omission and telemetry, never a plain hash or raw-email fallback. Tenant is
carried from authenticated REST/gRPC requests where available. Existing job records do not retain
tenant identity, so job observations currently include `jobId` without tenant correlation scope.

## Configuration

Set these values through Azure App Configuration, using Key Vault references for all secrets:

```json
{
  "EmailValidation": {
    "Projection": {
      "Enabled": true,
      "Environment": "prod",
      "Outbox": {
        "CollectionName": "EmailValidationProjectionOutbox",
        "CheckpointCollectionName": "EmailValidationProjectionCheckpoints",
        "BatchSize": 100,
        "DispatchIntervalSeconds": 5,
        "LockDurationSeconds": 60,
        "PublishedRetentionDays": 7,
        "MaximumPublishAttempts": 20
      },
      "ServiceBus": {
        "ConnectionString": "@Microsoft.KeyVault(...)",
        "TopicName": "email-validation-observations",
        "SubscriptionName": "email-validation-elasticsearch-projector",
        "ProvisionEntities": false,
        "MaxDeliveryCount": 10
      },
      "Elasticsearch": {
        "Endpoint": "http://10.10.252.28:9200",
        "ApiKey": "@Microsoft.KeyVault(...)",
        "DataStreamName": "email-validation-observations-prod-v1",
        "MaximumBatchSize": 500,
        "MaximumBatchBytes": 5242880,
        "RetryLimit": 5
      },
      "Privacy": {
        "IncludeRecipientDomain": true,
        "IncludeRawEmail": false,
        "EmailHashKey": "@Microsoft.KeyVault(...)",
        "EmailHashKeyVersion": "v1"
      },
      "Reconciliation": {
        "Enabled": true,
        "IntervalMinutes": 10,
        "OverlapMinutes": 15,
        "BatchSize": 500,
        "MaximumEventsPerRun": 5000
      }
    }
  }
}
```

Local secret-file overrides follow the existing bootstrap pattern:
`Azure:ProjectionServiceBusConnectionStringFile` plus its configured Key Vault URI, and
`Azure:ProjectionHmacSecretFile` plus its configured Key Vault URI. Never place their values in
JSON, compose files, logs, or command lines.

The projector identity needs only bulk/create access to
`email-validation-observations-*-v1`. Template/ILM/data-stream installation must use a separate
deployment identity with the relevant component-template, index-template, ILM, and data-stream
administration privileges.

## Deployment and validation

The inspected endpoint reported Elasticsearch 9.5.1, six nodes, three data nodes, green health,
and no existing email-validation observation template, data stream, or ILM policy. The artifacts
are compatible with Elasticsearch 8.19+ and 9.x. They intentionally use one primary and one
replica, 35 GB/7-day rollover, and 180-day deletion; privacy/storage owners must approve retention
before applying them.

Use this order:

1. Provision the Service Bus topic/subscription and least-privilege identities.
2. Validate, review, then install Elasticsearch artifacts:
   `ELASTICSEARCH_ENDPOINT=... EMAIL_VALIDATION_ENVIRONMENT=prod ops/elasticsearch/email-validation-observations/bootstrap.sh --apply`.
3. Deploy the worker/projector and confirm subscription/Elasticsearch readiness.
4. Deploy API/producer instances with projection enabled.
5. Observe outbox backlog, projection lag, retries, and DLQ before any backfill.

The bootstrap script performs read-only validation unless `--apply` is supplied. Runtime never
auto-creates an Elasticsearch index or installs templates.

## Backfill, reconciliation, and DLQ

Reconciliation reads bounded, ordered lifecycle pages, overlaps the saved Mongo checkpoint, and
inserts the same deterministic current attempt/lifecycle events into the normal outbox. It does no
SMTP and mutates no canonical result.

Backfill is dry-run by default:

```bash
dotnet EmailValidation.Worker.dll \
  --projection-backfill-from 2026-08-01T00:00:00Z \
  --projection-backfill-to 2026-08-02T00:00:00Z \
  --projection-backfill-batch-size 500 \
  --projection-backfill-max-events 10000
```

Add `--projection-backfill-commit` only after reviewing dry-run counts. Optionally use
`--projection-backfill-event-type`. A tenant filter is rejected because retained canonical
lifecycle records created before this change do not contain tenant identity. Replaying historical
lifecycle snapshots reconstructs the retained current attempt/state; already-expired historical
transitions cannot be recreated.

Malformed, unsupported, oversized, and strict-mapping-rejected messages go to the subscription
DLQ with concise non-PII reasons. Temporary 429/5xx/timeout failures are abandoned for redelivery
until the configured retry limit. Re-drive only after installing a compatible mapping/projector;
preserve the original body and `MessageId` so `_id` remains deterministic.

## Failure behavior

- Elasticsearch unavailable: validation and Mongo writes continue; Service Bus redelivery makes
  analytics stale and lag/retry metrics rise.
- Service Bus unavailable: validation continues; Mongo outbox leases are released with backoff and
  backlog/age rise.
- Projector crashes after indexing: redelivery receives a 409 for the same `_id`, which completes
  as duplicate success.
- Strict mapping mismatch: the individual event is dead-lettered and mapping-failure telemetry is
  emitted; it is not retried forever.
- HMAC key unavailable: correlation is omitted, raw email remains excluded, and validation output
  is unchanged.
- Canonical write succeeds but outbox insertion fails: a high-severity log is emitted and periodic
  reconciliation regenerates the deterministic event.

Key rotation changes correlation IDs. Use an approved dual-write/migration plan before changing
`EmailHashKeyVersion`; automatic rotation is intentionally not part of this implementation.
