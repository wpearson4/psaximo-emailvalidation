# Elasticsearch observation projection implementation report

## Final gap analysis

| Capability | Status | Evidence / remaining gap |
|---|---|---|
| Mongo authoritative mailbox/domain intelligence | IMPLEMENTED | Existing Mongo stores remain unchanged as the decision source |
| Mongo authoritative lifecycle/retry/attempt history | IMPLEMENTED | Existing compare-and-set lifecycle and retry outbox remain canonical |
| Mongo authoritative outbound-identity health | IMPLEMENTED | Existing store remains the selection/health source |
| Elasticsearch absent from synchronous decisions | IMPLEMENTED | Projection is only outbox/topic/worker code; no observation queries are registered |
| Attempt v1 observations | IMPLEMENTED | Emitted after successful canonical lifecycle writes |
| Lifecycle v1 observations | IMPLEMENTED | Sequence-based deterministic events include material status transitions |
| Outbound-health v1 observations | IMPLEMENTED | Emitted only after material persisted health changes |
| Domain-intelligence v1 observations | NOT IMPLEMENTED | Existing repository lacks a clean material-change event source; intentionally omitted |
| Deterministic event identity | IMPLEMENTED | SHA-256 canonical identity is reused by outbox, Service Bus, and Elasticsearch |
| Tenant-scoped HMAC correlation | PARTIALLY IMPLEMENTED | REST/gRPC observations are tenant scoped; historical/job lifecycle records do not retain tenant identity |
| Raw-email/raw-response exclusion | IMPLEMENTED | Contracts, serializer tests, and strict mapping exclude them |
| HMAC failure isolation | IMPLEMENTED | Missing key omits correlation, records telemetry/logging, and leaves validation unchanged |
| Durable Mongo projection outbox | IMPLEMENTED | Idempotent insert, atomic expiring claim, retry schedule, terminal state, and published-only TTL |
| Atomic canonical/outbox transaction | NOT IMPLEMENTED | Mongo topology/credentials were unavailable; canonical-first plus reconciliation is used for Mongo 4.4 compatibility |
| Observation topic publisher | IMPLEMENTED | Batched topic sends use deterministic metadata and retry release/backoff |
| Topic/subscription provisioning | IMPLEMENTED | Guarded, opt-in administration path; not executed against a live namespace |
| Dedicated projector | IMPLEMENTED | Existing worker host contains isolated publisher, projector, and reconciler services |
| Bounded bulk create/indexing | IMPLEMENTED | Byte/count bounds, recursive batch split, `refresh=false`, deterministic create `_id` |
| Per-item partial failure handling | IMPLEMENTED | 2xx/409/429/5xx/mapping failures settle independently |
| DLQ handling | IMPLEMENTED | Malformed/unsupported/oversized/mapping failures use concise non-PII reasons |
| Strict v1 mapping/template/ILM | IMPLEMENTED | Deployment artifacts pass Elasticsearch 9.5.1 template simulation |
| Data stream deployed | NOT IMPLEMENTED | Production mutation withheld pending retention approval and deployment credentials |
| Least-privilege projector role | IMPLEMENTED | Role descriptor grants only `create_doc` and `view_index_metadata` on the stream pattern |
| Lag/backlog/projection telemetry | IMPLEMENTED | Low-cardinality gauges, counters, and histograms are registered |
| Reconciliation/checkpoint | PARTIALLY IMPLEMENTED | Repairs retained current lifecycle attempt/state; expired historical transitions and health history cannot be reconstructed |
| Bounded resumable backfill | PARTIALLY IMPLEMENTED | Range, paging, max, event type, dry run, and checkpoint coordinates exist; tenant filter is rejected because canonical history lacks tenant identity |
| Unit/contract tests | IMPLEMENTED | Identity, privacy, schemas, mapping drift, metadata, bulk outcomes, timestamps, options, and disabled-mode DI |
| Mongo integration tests | IMPLEMENTED | Guarded test covers idempotency, atomic claims, expired-lock reclaim, retry timing, publish state, and TTL index |
| Live Service Bus integration tests | NOT IMPLEMENTED | No test namespace/credentials supplied |
| Live Elasticsearch write integration tests | NOT IMPLEMENTED | Production cluster was inspected/simulated read-only; no test cluster supplied |

## Canonical data confirmation

The following remain authoritative in MongoDB: `EmailValidationDomainIntelligence`,
`EmailValidationMailboxIntelligence`, `EmailValidationLifecycle`, lifecycle attempt history, retry
state/pending retry dispatch, `EmailValidationOutboundIdentityHealth`, and SMTP reputation state.
The new `EmailValidationProjectionOutbox` and `EmailValidationProjectionCheckpoints` collections
carry delivery/reconciliation state only. Elasticsearch is not registered as a result-reuse,
lifecycle, retry, classification, provider-strategy, or outbound-identity decision source.

## Code and infrastructure changes

- Application: v1 envelope/payload contracts, identity factory, HMAC abstraction, outbox and replay
  contracts.
- Infrastructure: HMAC-SHA256 implementation, Mongo outbox, lifecycle and health decorators,
  Service Bus publisher, guarded entity provisioning, Elasticsearch bulk sink, Mongo checkpoint and
  reconciliation/backfill, metrics, strongly typed validation, and Azure/Key Vault override hooks.
- Worker: outbox publisher, batched projector with per-message settlement, reconciliation service,
  and dry-run-first administrative backfill command.
- Hosts: authenticated REST/gRPC consumer/tenant context flows into canonical lifecycle requests;
  jobs provide stable job IDs.
- Elasticsearch: strict mapping/settings component templates, data-stream index template, ILM
  policy, least-privilege role descriptor, and validate-before-apply bootstrap script.
- Deployment: compose carries separate projection endpoint/stream/environment values; existing
  probe-sender index/client remains separate.

## Failure-mode results

- Elasticsearch unavailable: bulk results are retryable; validation and Mongo state are unaffected.
- Service Bus unavailable: claimed events return to pending with backoff; validation is unaffected.
- Outbox delayed: pending count and oldest-age gauges rise; reconciliation remains idempotent.
- Projector duplicate/redelivery: Elasticsearch 409 is completed as success.
- Mapping incompatible: the individual message is dead-lettered and mapping-failure telemetry rises.
- HMAC unavailable: no email/hash substitute is emitted; correlation is absent and validation is unchanged.

## Verification

- `dotnet build EmailValidation.sln --no-restore`: succeeded with zero warnings/errors.
- Full solution tests: 500 passed, zero failed (469 core, 24 API, 5 integration, 2 gRPC).
- JSON and shell syntax validation: succeeded.
- Live cluster: Elasticsearch 9.5.1, green, six nodes/three data nodes; inline strict data-stream
  template simulation succeeded with no overlapping template.
- Mongo integration test: included and guarded by `EMAIL_VALIDATION_TEST_MONGO`; the current local
  run had no live Mongo credential, so it exited without external writes.
- Service Bus integration: not run (no test namespace/credential).
- Elasticsearch write integration: not run (production cluster deliberately left unchanged).

## Backfill status

Backfill is implemented and unit/build tested. It was not executed. No production backfill or
Elasticsearch template/data-stream mutation was performed.
