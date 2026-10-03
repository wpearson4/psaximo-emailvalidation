# Email validation intelligence: architecture map and incremental plan

Review date: 2026-10-02

Repository baseline: `e251b5c` (`master`)

Scope: discovery and design before significant implementation

## Outcome

The repository already contains most of the requested evidence and intelligence platform. The safe path is to refine the existing pipeline, not introduce a second validator, cache, intelligence store, retry scheduler, or classification engine.

The highest-priority remaining work is:

1. preserve the existing numeric confidence contract while adding an explainable `HIGH` / `MEDIUM` / `LOW` presentation label;
2. separate Mongo domain, mailbox, observation, and initialization responsibilities behind capability-specific ports;
3. make freshness-driven revalidation explicit for conclusive results without weakening the existing provisional retry flow;
4. close smaller provider/dataset/documentation gaps, notably Barracuda recognition and disposable-dataset provenance;
5. keep evidence retention bounded and preserve the existing atomic observation/counter update behavior during the persistence refactor.

No paid API, SaaS validation service, commercial dataset, database, broker, or parallel validation pipeline is required.

## Non-negotiable design constraints

- **Single Responsibility:** a class has one reason to change. Persistence mapping, domain storage, mailbox storage, observation append, index creation, policy calculation, and orchestration remain separate responsibilities.
- **Interface Segregation:** consumers depend on the smallest capability they use. New code must not enlarge `IValidationIntelligenceStore`; it should move toward domain-reader/writer and mailbox-reader/writer capabilities.
- **Dependency inversion:** Domain remains dependency-free; Application owns policies and use cases; Infrastructure implements network and storage capabilities; hosts remain adapters.
- **Open/Closed is pragmatic:** add a strategy or rule only when extension is useful. A direct change to a centralized, well-tested policy is preferred over speculative plugin machinery.
- **One canonical pipeline:** every host continues to resolve the same `IEmailValidator` chain.
- **Evidence before assertion:** SMTP acceptance is not automatically mailbox proof, gateway identity is not mailbox-provider identity, and heuristic confidence is not delivery probability.
- **Privacy and restraint:** no `DATA`, no delivery generation, no raw SMTP transcript persistence, no raw-email telemetry dimensions, and no unnecessary randomized-recipient probes.

## Current architecture map

### Project boundaries

| Project | Current responsibility | Dependency direction |
| --- | --- | --- |
| `EmailValidation.Domain` | Immutable validation, evidence, provider, intelligence, prediction, and outbound-identity semantics | No outward project reference |
| `EmailValidation.Core` | Compatibility-facing ports, mailbox validation orchestration, classification, confidence, provider strategies, reuse, lifecycle, and retry policy | Domain |
| `EmailValidation.Application` | Domain-intelligence acquisition/freshness, scheduling, jobs, commercial access, evidence-backed prediction preparation, and reputation policy | Core + Domain |
| `EmailValidation.Infrastructure` | DNS, SMTP, catch-all probing, provider detection, Mongo/JSON stores, Service Bus scheduling, projection, and composition | Application + Core |
| `EmailValidation.Api` | REST host, OAuth/ownership/rate-limit adapters, health, and OpenAPI | Application + Core + Infrastructure + gRPC |
| `EmailValidation.Grpc` | Unary validation/status and streaming transport mapping | Core |
| `EmailValidation.Console` | CLI, CSV ingestion/output, and host bootstrap | Shared pipeline |
| `EmailValidation.Worker` | Thin Service Bus adapters for validation jobs, retry, and projection | Shared application/core services |

Existing dependency-guardrail tests protect the Domain/Application boundaries. `EmailValidation.Core` still has an ASP.NET framework reference for compatibility; new intelligence behavior should not deepen that coupling.

### Entry points

- REST: `POST /v1/email-validations`, bulk job endpoints, and lifecycle query endpoints in `ApiEndpoints`.
- gRPC: `EmailValidationGrpcService` and `EmailValidationStatusGrpcService`.
- CLI/CSV: `ConsoleApplication` and `CsvFileProcessor`.
- Durable jobs: `ServiceBusValidationJobWorker` -> `IValidationJobProcessor` -> the canonical validator.
- Durable retry: `ServiceBusRevalidationWorker` -> `IEmailRevalidationProcessor` -> `IEmailValidationService`.

All paths converge on the same runtime chain:

```text
LifecycleEmailValidator
  -> EvidenceBackedEmailValidationService
     -> IntelligenceEmailValidator
        -> EmailValidator
```

The decorators have distinct responsibilities:

- `LifecycleEmailValidator`: lifecycle creation/finality and retry coordination.
- `EvidenceBackedEmailValidationService`: immutable feature snapshot and optional calibrated-model projection.
- `IntelligenceEmailValidator`: hot/persistent reuse, mailbox intelligence persistence, single-flight, and risk enrichment.
- `EmailValidator`: one live mailbox-validation use case, using focused collaborators for normalization, domain acquisition, SMTP probing, provider interpretation, evidence evaluation, and classification.

### Canonical validation flow

```text
request
  -> normalize/validate syntax (`IEmailNormalizer`)
  -> hot result reuse (`IValidationResultCache`)
  -> persistent mailbox/domain reuse (`IValidationIntelligenceStore` today)
  -> per-address single-flight (`IValidationSingleFlight`)
  -> domain intelligence (`IDomainIntelligenceService`)
       -> MX/routing
       -> DNSSEC
       -> SPF/DMARC and honest DKIM observation state
       -> provider/gateway classification
       -> disposable/free/domain metadata
       -> catch-all reuse or bounded randomized probes
  -> provider-aware target SMTP probe over published MX routes
  -> normalized SMTP evidence and decision policy
  -> provider strategy interpretation
  -> historical-signal aggregation
  -> canonical classification and explainable confidence contributions
  -> additive risk/recommendation metadata
  -> bounded observations + reusable domain/mailbox state
  -> final result, or lifecycle/outbox/Service Bus provisional retry
```

### Domain intelligence and cache

`DomainIntelligenceService` is the single domain acquisition path. It:

- normalizes the domain;
- reads process memory and then the configured durable store through `PersistentDomainValidationCache`;
- applies `DomainIntelligenceFreshnessPolicy` and `ValidationPlanBuilder`;
- collapses concurrent base refresh and catch-all refresh independently by normalized domain;
- bounds live analyses with a semaphore;
- analyzes routing, DNSSEC, authentication, disposable status, provider/gateway, free-mail, MX forwarding, and mail infrastructure;
- persists one immutable snapshot with topology and policy fingerprints;
- carries compatible catch-all history forward but invalidates it when topology/provider strategy changes.

The current domain lifetime is bounded by DNS TTL and centrally clamped by `DomainIntelligenceOptions.MinimumFreshnessMinutes` and `MaximumFreshnessHours`. Catch-all freshness has its own configured window. Transient mailbox reuse has a much shorter window in `ResultReuseOptions`.

### DNS, MX, and authentication

- `MxDnsResolver` / `MailRoutingAnalyzer` handle ordered MX routes, Null MX, implicit address fallback, TTL, A/AAAA data, retries, and timeouts.
- `DnsSecurityAnalyzer` isolates DNSSEC state.
- `EmailAuthenticationAnalyzer` records SPF and DMARC state and does not pretend arbitrary DKIM selectors can be discovered.
- `MailProviderDetector` uses preferred published MX topology, with explicit provider-owned consumer domains where appropriate.
- Topology fingerprints scope reuse and historical interpretation.

### SMTP evidence and provider handling

`SmtpMailboxProbe` owns the protocol conversation and returns evidence; it does not classify the final email result. It connects to the published MX, performs greeting, EHLO/HELO, `MAIL FROM`, `RCPT TO`, reset, and quit, and never sends `DATA`.

The normalized response path is:

```text
command-stage reply
  -> `SmtpResponseClassificationOrchestrator`
  -> canonical classifier + candidate intelligence classifier
  -> `SmtpResponseDecisionPolicy`
  -> normalized reason, strength, mailbox impact, retry/cooldown, health impact
  -> provider strategy
  -> canonical classification
```

Raw response text is sanitized and bounded in live evidence. Durable mailbox/domain snapshots strip session evidence and probe result arrays. Stable semantic response fingerprints avoid addresses, IPs, hosts, timestamps, and tenant identifiers.

Provider/gateway coverage currently includes:

- Microsoft consumer and Microsoft 365/EOP;
- Google Workspace;
- Yahoo/AOL/AT&T/legacy Verizon routing normalization;
- Proofpoint;
- Mimecast;
- Apple/iCloud;
- Comcast;
- Proton;
- Zoho;
- Fastmail;
- Amazon SES;
- generic/unknown SMTP.

Microsoft, Google, Proofpoint, and Mimecast have focused provider strategies. Other recognized providers use the conservative generic strategy plus centralized response rules and provider pacing. Barracuda is not yet recognized and is a planned additive signature/gateway change.

### Catch-all intelligence

The implementation is deliberately stricter than a Boolean:

- public compatibility states remain `NotCatchAll`, `LikelyNotCatchAll`, `Unknown`, `LikelyCatchAll`, and `NotAttempted`;
- `DomainRecipientBehavior` separately models `Unknown`, `RecipientSpecific`, `CatchAll`, and `AcceptAll`;
- `CatchAllReasonCode`, confidence, counts, observation time, strategy version, and evidence-contract version preserve why the conclusion exists;
- high-entropy `dwcheck-<random>` recipients avoid real-person patterns;
- probes are capped at three and stop when another probe cannot change the decision;
- one accept-all observation is only a candidate; independent correlated evidence is required for confirmation;
- fresh compatible evidence can skip both catch-all and target probing only when the planner proves that more SMTP work cannot distinguish the mailbox.

### Classification and confidence

`EmailClassificationEngine` is the canonical deterministic classifier. Definitive syntax, domain, routing, and qualified recipient rejection evidence can be final. Temporary, policy, gateway, timeout, conflicting, and unavailable evidence abstains as `Unknown`; mailbox-full is `Risky`; catch-all is preserved separately from mailbox validity and mailing risk.

Confidence is an explainable heuristic score for confidence in the selected classification. Contributions are retained in `ConfidenceEvidence`; `EvidenceConfidenceExplainer` produces a human-readable explanation. `DeliverabilityProbability` remains null unless an authorized outcome-backed model supplies a calibrated probability.

Current compatibility concern: REST/gRPC/JSON still expose the heuristic decimal. The brief prefers a public `HIGH` / `MEDIUM` / `LOW` value. This must be additive so existing clients are not broken.

### Persistence and Mongo access map

Mongo is authoritative when `Persistence.Provider=MongoDB`; JSON is a local fallback. Current collections relevant to this objective include:

| Collection | Purpose | Important access/indexes | Retention/growth |
| --- | --- | --- | --- |
| `EmailValidationDomainIntelligence` | Current domain snapshot plus bounded observations | unique normalized domain; provider; last validated; updated | Both observation arrays use `$push/$slice`; configured bound defaults to 200 each |
| `EmailValidationMailboxIntelligence` | Reusable sanitized mailbox result and strong-evidence timestamps | unique normalized email; domain; last validated; status; updated | One current document per normalized address; no raw SMTP transcript |
| `EmailValidationLifecycle` | Canonical provisional/final lifecycle, attempts, and retry outbox state | validation id; active email/state; pending dispatch | Attempt count is bounded by policy; compare-and-set versioning |
| `EmailValidationJobs` / `EmailValidationJobItems` | Durable bulk jobs and ordered results | job time/source/state; outbox claim; unique job+position; item claim/validation/domain | Operational job retention is deployment policy |
| feature snapshot / outcome collections | Privacy-safe calibration readiness | schema/time and correlation/time indexes | Append-only evidence; retention requires governance before volume grows |
| projection outbox | Optional observation projection | claim/created; TTL for published entries | Published records expire by configured TTL |

Domain observations are appended atomically with `$push/$slice` and `$inc`. Lifecycle/job concurrency uses atomic update filters, leases, and compare-and-set versions.

One structural persistence correction is required:

1. `MongoValidationIntelligenceStore` currently owns domain storage, mailbox storage, observation storage, index initialization, document mapping, failure policy, and an internal mailbox cache. This violates the stricter Single Responsibility requirement even though it implements existing ports correctly.

The current observation path already appends bounded evidence and increments `ObservationCount` in one atomic Mongo update; snapshot saves do not set that counter. The refactor must retain that invariant. Cross-instance snapshot replacement remains last-write-wins by design and should be revisited only if measured multi-instance contention demonstrates a need for snapshot compare-and-set versioning.

No TTL index should be added to domain or mailbox intelligence while those collections represent the latest reusable state. Staleness is semantic and varies by evidence type; deleting the whole document would also erase bounded history. TTL remains appropriate for true operational/outbox data with one uniform expiry field.

### Revalidation and lifecycle

The existing durable provisional flow is sound:

- `RevalidationPolicy` selects retryable evidence and terminal reasons;
- `RevalidationSchedulePolicy` centralizes provider/domain backoff and cooldown timing;
- lifecycle persistence embeds a durable outbox to close the Mongo/Service Bus dual-write gap;
- deterministic `validationId:attempt` message IDs plus Mongo compare-and-set state make worker processing idempotent;
- stale, superseded, or already-final messages perform no SMTP work;
- normal inconclusive exhaustion finalizes without abusing the dead-letter queue.

Current scope is retry of `Unknown` or explicitly retryable enforced SMTP outcomes. The broader lifecycle requested by the brief—long freshness for conclusive deliverable results, moderate freshness for catch-all, and explicit stale/revalidate-after metadata—is only implicit through reuse TTLs and on-demand validation. It needs a focused freshness policy before any periodic scheduling is enabled.

### API compatibility

The v1 REST contract returns canonical status/lifecycle, numeric confidence and reason, provider, check summary, MX consensus, and compact recipient evidence. Detailed domain/address intelligence exists in the internal result and Console output but is not fully projected to REST/gRPC.

Planned API evolution is additive:

- add `confidenceLevel` while retaining `confidence`;
- add a compact `intelligence` object only after its versioned semantics are fixed;
- never silently correct typo suggestions;
- keep role/free/disposable classifications informational and separate from technical validity;
- keep raw or sanitized SMTP conversation text out of public and durable contracts.

### Observability

Existing low-cardinality meters cover validation quality, persistence reads/writes/reuse, result-cache hits, single-flight collapse, live work avoided, catch-all discovery/reuse, SMTPUTF8, domain refresh/topology change, response-intelligence rollout/disagreement, SMTP scheduling/cooldowns, reputation protection, revalidation, jobs, projections, role/disposable/spam-trap matches, and API latency/status.

Metrics must continue to use status/provider/category/version dimensions, never raw email addresses or unbounded recipient domains.

### Test architecture

- `EmailValidation.Core.Tests`: deterministic unit and boundary tests for normalization, classification, SMTP response interpretation, provider strategy, catch-all, reuse, revalidation, intelligence, risk, scheduling, jobs, projections, options, and output compatibility.
- `EmailValidation.Api.Tests`: REST/gRPC behavior, configuration, authorization-related host behavior, and health.
- `EmailValidation.Grpc.Tests`: protobuf mappings.
- `EmailValidation.IntegrationTests`: guarded Mongo and live DNS tests; normal CI does not depend on public SMTP.
- Replay fixtures cover normalized provider-aware SMTP response intelligence.

## Requirement disposition

| Requested capability | Current disposition | Planned action |
| --- | --- | --- |
| Evidence-based result | Implemented | Preserve one model; improve public projection only |
| Explainable confidence | Implemented internally; public shape partial | Add additive qualitative level and document thresholds |
| SMTP taxonomy/raw-to-normalized provenance | Implemented | Keep rollout/replay coverage current |
| Provider/gateway classification | Implemented with Barracuda gap | Add conservative Barracuda signature and tests |
| Mongo domain intelligence | Implemented | Split responsibilities/ports; fix concurrent counter overwrite |
| Mongo indexes and bounded growth | Implemented | Document query-to-index rationale; define outcome/job retention before growth |
| Evidence-specific freshness/cache | Implemented for domain/catch-all/mailbox/transient reuse | Centralize public freshness metadata without a duplicate cache |
| Historical observations | Implemented and bounded | Preserve domain aggregates; avoid mailbox-event accumulation |
| Multi-state catch-all | Implemented through status + recipient behavior | Do not collapse accept-all into mailbox proof |
| Randomized probe safety | Implemented | Retain caps, early stop, cooldown, and historical reuse |
| Adaptive retry | Implemented for provisional failures | Add separate conclusive-evidence freshness policy before scheduled refresh |
| Lifecycle | Implemented for provisional/final; stale implicit | Add explicit freshness/revalidate-after metadata additively |
| Typo suggestion | Implemented conservatively | Keep separate from normalization/validity |
| Role account | Implemented | Keep as metadata/risk only |
| Free mailbox | Implemented | Add public projection only if contract demand exists |
| Disposable mailbox | Implemented with local configurable data | Document dataset source/license/version/update workflow; do not claim unknown domains are clean |
| Outcome feedback/calibration readiness | Implemented | No training/enforcement until labeled-data gates pass |
| Performance/concurrency | Largely implemented | Preserve atomic bounded observation updates; benchmark cache/probe avoidance before tuning |
| Privacy/minimization | Implemented | Add retention decisions for outcome/job collections |
| Observability | Implemented | Add qualitative confidence/freshness metrics with bounded tags |
| Tests/docs | Broadly implemented | Add tests for each remaining change and keep this map current |

## Incremental implementation plan

### Phase 1 — discovery and design

Status: complete in this document.

Acceptance gates:

- one current-architecture map names every canonical entry point and shared pipeline;
- existing capabilities are explicitly reused;
- gaps and compatibility risks are recorded before code changes;
- the SRP/interface-segregation migration is specified.

### Phase 2 — confidence presentation and API compatibility

Status: qualitative confidence policy and additive REST/gRPC projection implemented in this change; broader compact intelligence projection remains in Phase 6.

Introduce one pure `IConfidenceLevelPolicy` in Core or Application with documented, versioned thresholds over classification status, evidence quality, confidence contributions, and ambiguity. Do not derive the label from the decimal alone when a high score merely means “high confidence that the result is unknown.”

Add a `ConfidenceLevel` enum (`HIGH`, `MEDIUM`, `LOW`) and an additive result/API/gRPC field. Retain the numeric `confidence`, `confidenceType`, reason, evidence contributions, and null-until-calibrated probability.

Implemented policy v1 is intentionally conservative:

- `Unknown` is `LOW`, even when the legacy numeric score is high confidence that validation is inconclusive;
- conclusive `Valid` or `Invalid` evidence with a score of at least `0.85` is `HIGH`;
- catch-all is `HIGH` only when its public catch-all classification is confirmed and its score is at least `0.85`;
- other supported conclusions with a score of at least `0.65` are `MEDIUM`;
- absent/unknown evidence or weaker conclusions are `LOW`.

Tests:

- deterministic/qualified positive and negative evidence;
- catch-all and gateway ambiguity;
- high-confidence `Unknown` maps to the appropriate evidence label rather than appearing deliverable;
- REST/gRPC/JSON backward compatibility.

### Phase 3 — segregated persistence and Mongo concurrency

Introduce capability-specific ports:

- `IDomainIntelligenceReader` / `IDomainIntelligenceWriter`;
- `IMailboxIntelligenceReader` / `IMailboxIntelligenceWriter`;
- retain `IValidationObservationStore` as its own capability;
- retain a focused initializer capability.

Keep `IValidationIntelligenceStore` temporarily as a compatibility composite if necessary, but new consumers receive only the capabilities they use.

Split the Mongo implementation into narrowly responsible adapters and separate document mappers. Share only a small Mongo collection context/factory. Move the process-local mailbox cache to the existing result-cache layer or a focused decorator; a durable repository should not also be a cache policy.

Preserve and verify concurrent domain-write invariants so:

- observation append uses atomic `$push/$slice/$inc`;
- snapshot save does not replace observation counters or bounded observation arrays;
- topology replacement remains atomic;
- no unbounded arrays are introduced.

Tests:

- concurrent snapshot save and observation append;
- concurrent upsert from an absent document;
- bounded normal and protected observation arrays;
- observation counters remain monotonic across snapshot saves;
- topology change and stale evidence behavior;
- existing Mongo 4.4 compatibility.

### Phase 4 — explicit freshness and revalidation policy

Create a pure `IEvidenceFreshnessPolicy` responsible only for status/evidence-specific freshness. It should return reuse expiry and an optional `revalidateAfter`, not schedule work.

Extend the existing lifecycle/revalidation coordinator to consume that policy. Preserve the current provisional retry policy as a separate responsibility.

Initial policy categories:

- deterministic permanent syntax/domain/routing/qualified recipient rejection: no automatic retry; refresh only on a documented long policy window or caller demand;
- strong deliverable evidence: long freshness;
- catch-all/accept-all behavior: moderate freshness;
- transient DNS/SMTP: short retry through the existing queue;
- greylist/rate/provider policy block: provider-aware backoff/cooldown;
- local cooldown: reschedule without consuming an SMTP attempt;
- topology/provider strategy change: invalidate compatible reuse and reanalyze on demand.

Do not add another scheduler. Use the existing lifecycle outbox and Service Bus only when a concrete scheduled-refresh use case is enabled. Default rollout should expose metadata first, then observe, then schedule.

### Phase 5 — provider and dataset completion

- Add conservative Barracuda MX/greeting recognition and gateway identity; keep mailbox provider unknown unless recipient-differentiating evidence exists.
- Keep Yahoo/AOL, Apple, Comcast, Proton, Zoho, and Fastmail on conservative generic semantics unless fixtures prove a focused strategy adds value.
- Move local disposable/free/typo data to versioned maintainable resources if the lists grow beyond configuration defaults.
- Record source, license, retrieval/version date, normalization rules, checksum, and update steps for any third-party disposable dataset. Do not fetch at runtime.

### Phase 6 — public intelligence projection

After semantics stabilize, add a versioned additive projection containing only useful fields such as:

- qualitative confidence and reason;
- provider and gateway;
- catch-all classification and recipient behavior;
- role type;
- mailbox type (`FREE`, `BUSINESS`, `UNKNOWN`);
- disposable status with conservative unknown;
- typo suggestion;
- evidence observed/fresh/revalidate timestamps.

Keep detailed internal observations, raw response excerpts, topology internals, and policy-health state private.

### Phase 7 — retention, operations, and measured optimization

- approve retention for jobs, lifecycle history, feature snapshots, outcomes, and optional projection data;
- measure Mongo read/write latency, domain/mailbox hit rate, single-flight collapse, DNS/SMTP work avoided, retry resolution, and validation latency;
- remove or change indexes only from observed query evidence;
- add distributed coordination only if multi-instance duplicate work is measured and material;
- keep Mongo authoritative; do not add Redis, another database, or another broker speculatively.

## Definition of done

- No validation host bypasses the canonical validator chain.
- Every final classification can identify the normalized observations, source, strategy/policy versions, and explanation used.
- Public confidence is qualitative and explainable; numeric heuristics are not described as probabilities.
- Mongo writes are race-safe and observation storage is bounded.
- Domain, mailbox, observation, initialization, policy, and scheduling responsibilities are represented by focused classes and interfaces.
- Provider/gateway strategy never turns gateway acceptance into mailbox proof.
- Catch-all probing remains bounded, randomized, reusable, and cooldown-aware.
- Retry and freshness policies are centralized and use the existing Service Bus/outbox infrastructure.
- Existing REST/gRPC/CLI clients remain compatible through additive changes.
- Unit, contract, Mongo, and worker tests pass without relying on live SMTP.
- Documentation covers models, indexes/query rationale, retention, confidence, SMTP taxonomy, provider behavior, and revalidation.

## Explicit non-goals

- no validator rewrite;
- no second intelligence database, cache authority, retry scheduler, or provider engine;
- no paid API or runtime disposable-domain API;
- no email sending to manufacture delivery outcomes;
- no bypass of published MX gateways or provider controls;
- no opaque machine-learning confidence and no enforcement before labeled-data gates pass;
- no unbounded event history and no raw-email metric dimensions.
