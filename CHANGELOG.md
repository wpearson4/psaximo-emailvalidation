# Changelog

## Unreleased

- Enabled production live SMTP verification with the labeled Azure App Configuration key
  `EmailValidation:Smtp:Enabled=true`; the API and worker were restarted and readiness remained healthy.
- Changed the Azure DevOps `master` pipeline to deploy validated immutable API/worker images to production by
  default; manually queued publish-only runs can explicitly set `deployProduction=false`.
- Added structured `UnknownContext` to inconclusive results with a stable cause, plain-language summary,
  retryability, recommended action, SMTP stage/category/code, MX host, and retry time. REST, gRPC, lifecycle status,
  Console text/JSON/CSV, and file-processing CSV outputs expose the same context without changing classification.
  REST responses also reconstruct the context for legacy stored Unknown results so existing jobs gain the explanation
  when they are downloaded again.
- Fixed production job creation by using native MongoDB `$in` filters for source-file deduplication and active-job
  failure transitions, avoiding an unsupported LINQ partial-evaluation path in the MongoDB driver.
- Prevented API/worker release skew with one immutable Compose release tag, made Mongo job documents tolerant of
  additive fields, persisted terminal status before dead-lettering exhausted jobs, and added durable source-file
  deduplication so completed files cannot be validated again while failed jobs can resume safely.
- Registered and pinned production deployment to the host-local `esdata03-emailvalidation` agent in
  `OMetaSearchPool`; the shared frontend agent cannot reach the API host over SSH.
- Completed the P0 commercialization gap review and documented repository evidence for persistent intelligence,
  result reuse, single-flight, durable retries, REST, async jobs, international addresses, CSV, and canonical status.
- Added `EmailValidation.Api` with versioned validation/status endpoints and durable asynchronous-job endpoints that
  delegate to the existing canonical validation and lifecycle services.
- Added Mongo-backed job headers and ordered job items, identifier-only Azure Service Bus messages, bounded chunk
  execution, idempotent progress updates, partial-error completion, and job telemetry.
- Added explicit international-address transport handling: IDN domains remain platform-normalized, Unicode local
  parts declare `RequiresSmtpUtf8`, EHLO advertises transport support, and unsupported destinations return explicit
  inconclusive evidence without sending an invalid SMTP envelope.
- Added the persisted and published `RetryScheduled` lifecycle transition and mapped it through the existing gRPC v1
  contract while preserving all previously persisted enum values.
- Added API, async-job, SMTPUTF8, lifecycle, gRPC, and structured Unknown-context coverage. The full solution now passes 371 tests.
- Completed an architecture guardrail review covering layering, shared orchestration, evidence/reuse, SMTP and sender policy, retry/lifecycle authority, risk/confidence boundaries, async safety, and commercial extensibility.
- Moved bounded domain scheduling from the Console host into Application, added asynchronous channel backpressure, and removed the duplicate legacy domain-intelligence workflow from `EmailValidator`.
- Preserved compatible historical catch-all evidence across inconclusive refreshes, including records written before richer provider fingerprints, while retaining topology/provider invalidation.
- Added assembly-dependency tests that prevent Domain/Application from acquiring infrastructure or host dependencies.
- Added separate `EmailValidation.Domain` and `EmailValidation.Application` assemblies while preserving existing public namespaces and consumers.
- Added reusable domain intelligence for MX routing/TTL and fallback metadata, resolver-validated DNSSEC state, SPF and DMARC parsing, honest DKIM observation state, provider/banner evidence, disposable-domain provenance, and lifecycle fingerprints.
- Added bounded domain-level single-flight and independent catch-all single-flight with memory/persistent reuse and topology-aware invalidation.
- Added typed role-address and deliverability-risk contracts, trusted-evidence spam-trap semantics, and a future catch-all deliverability predictor port without fabricated probability output.
- Extended Mongo domain documents additively with MongoDB 4.4-compatible updates and conservative restoration of older documents that lack the JSON payload or new fields.
- Removed local connection secrets from the checked-out console configuration; runtime secrets remain deployment-owned through App Configuration and Key Vault.

This file records user-visible behavior, output-contract changes, and operational migration notes.

## Unreleased — 2026-08-22

### Added

- A versioned `emailvalidation.status.v1` gRPC host now exposes authoritative current-status queries and
  server-streaming lifecycle watches with initial snapshots, sequence-based reconnect/deduplication, cancellation,
  access-policy, stream-limit, structured logging, and telemetry boundaries.
- Canonical lifecycle documents now distinguish lifecycle state, progress stage, mailbox status, and result finality;
  they record requested/started/update timestamps, reuse/running flags, retry reason/time, and a monotonic status
  sequence. Coarse domain, provider, SMTP, and persisted-intelligence progress is reported without exposing protocol
  diagnostics.
- Mongo change streams project lifecycle updates from validation and worker processes to gRPC hosts. Non-Mongo local
  runs use an in-memory dispatcher behind the same publish/subscription contracts.
- Durable, bounded revalidation now converts retryable `Unknown` outcomes into a provisional lifecycle with a stable
  validation ID, compact immutable attempt history, optimistic concurrency, and one canonical current result.
- Azure Service Bus scheduled enqueue is isolated behind `IRevalidationScheduler`; the new worker maps explicit
  application dispositions to complete, reschedule, abandon, or the queue's native dead-letter subqueue.
- A Mongo lifecycle document embeds a pending publication record. This outbox closes the Mongo/Service Bus dual-write
  gap and is retried by the worker without polling Mongo for validation due times.
- Retry eligibility and timing are separate policies. Timing takes the maximum of result `RetryAfter`, current local
  cooldown, provider policy cooldown, and the existing bounded domain backoff.
- Revalidation telemetry covers scheduling, execution, fresh-result skips, final/stale/duplicate outcomes, transition
  labels, attempts/time to final, worker failures, and Microsoft-specific effectiveness counters.

### Revalidation operations

- Revalidation is disabled by default. Enabling it requires Mongo lifecycle persistence and a Service Bus connection
  supplied through the existing Azure App Configuration/Key Vault pattern. Queue provisioning is separately gated by
  `ProvisionQueue`; normal runtime credentials do not need management permission.
- The default queue is `email-validation-retry`. Provisioning is idempotent and never deletes or recreates an existing
  queue. Broker duplicate detection is optional; lifecycle compare-and-set remains authoritative.
- Console, CSV, and worker retries share the existing reuse, single-flight, validation planner, catch-all intelligence,
  provider throttle, and sender policy. No retry-specific SMTP or sender-rotation path was added.

### Changed

- Domain catch-all intelligence now persists its structured reason, evidence counts, observation time, confidence,
  and strategy version in the existing domain document. Fresh, high-confidence evidence can skip redundant random
  and non-discriminating mailbox SMTP probes without promoting an individual mailbox to `Valid`.
- Catch-all reuse is freshness-, topology-, confidence-, and provider-strategy-aware. Inconclusive refreshes preserve
  historical evidence with bounded retry backoff, while contradictory evidence updates the current classification.
- Reused catch-all results expose `PersistentDomainIntelligence` provenance. Existing metrics now include discovery,
  reuse, refresh, expiry, classification changes, `CatchAllLiveProbesAvoided`, and
  `MailboxProbesAvoidedDueToCatchAll`.
- Catch-all-related gateway acceptance now returns the public `CatchAll` status with a more specific
  `Confirmed`, `Likely`, `GatewayAmbiguous`, or `Historical` classification. Explicit randomized-recipient
  rejection still supports `LikelyValid` and is not over-labeled as catch-all.
- Provider policy circuits are scoped by normalized MX host, with optional outbound-IP and tenant dimensions.
  Provider-wide concurrency and pacing remain in effect, but a block from one tenant MX no longer suppresses
  unrelated tenants hosted by the same provider.
- Locally deferred SMTP work is reported as `LocalCooldown` instead of a remote provider block. Results expose
  whether a probe was attempted, the probe disposition, evidence quality, and the earliest useful retry time.
- Heuristic confidence remains confidence in the assigned classification. It is not a delivery probability;
  `Deliverability Probability` stays empty until outcome data supports calibration.

### CSV output

- New exports use one `Classification Confidence` column instead of adding both `Confidence` and
  `Classification Confidence`.
- Existing input files containing the legacy `Confidence` column retain it and receive an updated value for
  backward compatibility.
- Exports now include `Evidence Quality`, `Catch-All Classification`, `Probe Attempted`, `Probe Disposition`,
  `SMTP Response Category`, `Retry After`, validation lifecycle state, attempt limits, and lifecycle timestamps.

### Operational notes

- Azure App Configuration bootstrap now accepts local read-only App Configuration and Mongo connection strings
  when Azure CLI, managed identity, or developer credentials are unavailable. The Mongo override is restricted to
  its configured Key Vault secret URI. Both values remain local secrets and are not committed;
  endpoint-plus-credential authentication remains the default fallback.
- Fresh reusable results now follow memory cache → persistent mailbox/domain policy → single-flight → live
  validation. Persistent hits warm a bounded cache, live results replace stale entries, and temporary/provider-block
  outcomes use a short configurable reuse window.
- Concurrent equivalent requests share one process-local validation after reuse misses. Source metadata distinguishes
  live, memory, persistent, and joined results while preserving the original evidence timestamp. Duplicate CSV rows
  benefit automatically and retain row order.
- Domain, provider, catch-all, structured observation, and mailbox intelligence now persist in two dedicated
  MongoDB collections behind the existing host-neutral store contracts. Startup creates missing indexes
  idempotently; temporary runtime Mongo failures degrade to live validation without changing classification.
- The console loads `EmailValidation:*` settings from Azure App Configuration with the configured environment
  label and resolves the Mongo connection through an existing Key Vault secret using `DefaultAzureCredential`.
  No connection string or credential is stored in the repository or written to logs.
- Validation, classification, confidence-model, and provider-strategy versions were advanced so persisted
  results created under the previous policy are not incorrectly reused.
- A `LocalCooldown` result is provisional and should be retried at or after `Retry After`; it is not evidence
  that the recipient provider rejected mailbox verification.
- Mailing-risk signals such as role accounts, disposable domains, abuse indicators, and suppressions remain
  separate from technical catch-all deliverability.

### Verification

- The release is covered by 273 offline core tests and an integration-test assembly with opt-in live DNS and
  real-Mongo collection/index coverage.
