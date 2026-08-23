# Architecture review and guardrails

Review baseline: `5623b16` (2026-08-22). This review evaluated the repository against the commercial-platform direction in the project brief. It intentionally favors incremental corrections over a wholesale rewrite.

## ALIGNED

| Area | Assessment |
| --- | --- |
| Project boundaries | `EmailValidation.Domain` owns immutable semantic models and has no outward project reference. `Application` owns domain-intelligence orchestration and policies. `Infrastructure` implements DNS, SMTP, Mongo, Service Bus, Elasticsearch, and configuration concerns. Console, Worker, and gRPC are adapters. |
| Validation orchestration and host independence | Console, CSV, Worker, and gRPC resolve the same `IEmailValidator` / `IEmailValidationService` pipeline. There are no independent bulk, retry, or transport-specific validators. |
| Domain intelligence | MX routing, provider, authentication, DNS security, disposable-domain, catch-all, topology, and lifecycle evidence are reusable across mailboxes. Freshness and compatibility policy determine whether live work is required. |
| Mailbox intelligence | Mailbox results and attempt history remain distinct from reusable domain observations. Domain facts are not treated as proof that an individual mailbox exists. |
| Reuse and single-flight | The path is process memory, persistent intelligence, single-flight, then live work. Mailbox and domain single-flight are separately keyed; catch-all refresh also has an independent flight. Waiter cancellation does not own shared work, and completed/failed flights are removed. |
| SMTP separation | SMTP implementations return stage-specific connection, EHLO, MAIL FROM, RCPT TO, timing, response, and provider evidence. Central classification/policy interprets it. No message content or `DATA` is sent. |
| MX routing | Routes are ordered by preference, equal-preference hosts are deterministic, failures can advance to another route, implicit address fallback and Null MX are explicit, and temporary DNS/host failure is not a definitive mailbox rejection. |
| Provider strategies | Provider detection records topology/evidence/confidence and provider behavior is behind focused strategies. Banner evidence may refine detection but is not the sole authority. |
| Sender policy | Sender rotation is bounded and limited to sender-specific MAIL FROM failure. RCPT rejection, provider block, greylisting, timeout, and source-IP policy do not trigger sender cycling. |
| Retry architecture | Azure Service Bus scheduled work uses stable validation/attempt identity. Mongo compare-and-set state makes processing idempotent; stale, already-final, or superseded attempts complete without SMTP. Normal validation outcomes do not use the DLQ. |
| Mongo and lifecycle | Mongo is the canonical durable lifecycle/intelligence store and remains compatible with MongoDB/FCV 4.4. Messages, streams, memory events, and caches are not authoritative. Lifecycle/result finality remain separate from mailbox status. |
| Status delivery | Host-independent query/publish/subscribe contracts own status semantics. gRPC is transport-only, late subscribers receive canonical state, and stream cancellation does not cancel validation. Sequence numbers are monotonic; clients derive countdowns from `RetryAtUtc`. |
| Risk and confidence | Role, disposable, suppression, spam-trap risk, catch-all, and technical validity remain distinct. Heuristic confidence is classification confidence, not calibrated deliverability probability. Trusted evidence is required for a known spam-trap claim. |
| Async, configuration, and security | Network/storage paths are asynchronous and accept cancellation. Strongly typed options and the App Configuration/Key Vault bootstrap are centralized in hosts. No checked-in runtime credential is required and infrastructure details are not part of domain contracts. |
| Observability | Metrics cover reuse, cache behavior, single-flight joins, topology changes, catch-all efficiency, policy blocks, retry outcomes, attempts, and time to final without introducing another telemetry stack. |
| Learning extensibility | Outcome ingestion and calibration ports/models provide a future seam for delivery ground truth without labeling current heuristics as machine learning. |

## NEEDS IMPROVEMENT

- `EmailValidation.Core` is a compatibility assembly that still combines ports, mailbox orchestration, classification, and confidence policy. It also retains an ASP.NET framework reference. New domain semantics and use cases should continue moving into Domain/Application when touched, with type forwarding or other compatibility measures; a mass namespace/project migration is not justified.
- `EmailValidator` remains a broad mailbox-use-case orchestrator. Its duplicate domain-analysis implementation has been removed, but future changes should keep extracting focused application services only when a responsibility can be separated without duplicating policy.
- `ProbeSenderHealthChecker.GetSnapshot` takes a synchronous semaphore for an in-memory diagnostic snapshot. It is not on a network/storage path, but an immutable/lock-free snapshot would remove the remaining synchronous wait if contention becomes observable.
- There is no REST API or application-level tenant/metering context yet. Public contracts should gain tenant attribution when the commercial API requirement is concrete, rather than introducing speculative global state now.

## HIGH RISK

The baseline contained two high-risk architecture drifts; both were remediated in this review:

1. `EmailValidator` contained a second, roughly 170-line domain-intelligence acquisition path used by direct construction. Production dependency injection used `DomainIntelligenceService`, so the two paths could make different freshness, compatibility, and catch-all decisions. The duplicate path and optional dependency mode were removed; the validator now requires the single application service.
2. CSV/domain scheduling policy lived in the Console host, used unbounded channels, and launched processing through `Task.Run`. Large inputs could accumulate unbounded completed work, and another host could not reuse the policy. The scheduler now lives in Application, uses bounded channels with asynchronous backpressure, propagates cancellation, and is shared without a fire-and-forget task.

The refactor also exposed and corrected a persistence-compatibility defect: a stale, still-topology-compatible catch-all record written before richer provider fingerprints could be discarded during refresh. Legacy unknown provider fields are now treated conservatively as compatibility wildcards, and an inconclusive refresh preserves historical evidence with backoff. A topology or provider mismatch still invalidates it.

No unresolved correctness, security, SMTP-circumvention, retry-idempotency, or source-of-truth issue was found at high-risk severity.

## NOT IMPLEMENTED / FUTURE

- A dedicated `EmailValidation.Api` REST adapter, APIM product surface, bulk job endpoints, webhook delivery, and tenant/billing/entitlement persistence should be driven by concrete commercial requirements.
- Stable outbound source identities, local-address binding, PTR/health inventory, and provider/domain affinity should eventually sit behind explicit discovery/selection/health ports. The current code does not implement outbound IP rotation and must not add block-circumvention behavior.
- Redis may become a non-authoritative distributed cache when measured multi-instance reuse justifies it. Mongo remains the source of truth.
- Additional durable job queues, relational billing storage, Kafka, and RabbitMQ are not justified by the current workload.
- Calibrated deliverability probability should be introduced only after sufficient delivered/bounce/complaint/suppression outcomes exist. Current confidence decomposition and historical evidence remain the honest model.
- Complaint/outcome ingestion, calibration datasets, and learned decision policy remain strategic extensions through existing outcome seams, not part of this remediation.

## SOLID review

- **Single responsibility:** the two material violations were the duplicate domain workflow in `EmailValidator` and host-owned scheduling; both were corrected. Hosts now remain adapters.
- **Open/closed:** provider strategies, risk providers, persistence, caches, status transports, retry scheduling, and outcome consumers are replaceable behind focused contracts.
- **Liskov substitution:** memory and Mongo stores share reuse/source-of-truth semantics; transport implementations do not redefine lifecycle meaning. No contract-breaking implementation was found.
- **Interface segregation:** existing ports are capability-focused. No umbrella validation/infrastructure interface was introduced.
- **Dependency inversion:** Domain has no outward dependency; Application depends on contracts and semantic types; Infrastructure and hosts supply implementations.

## Enforced guardrails

Tests now fail if Domain references Application, Infrastructure, hosts, ASP.NET, or gRPC, or if Application references Infrastructure, host, Mongo, Azure, or Elasticsearch assemblies. The same guardrail verifies that reusable domain scheduling remains in Application. Existing behavior tests cover single-flight, persistence reuse, catch-all compatibility/invalidation, sender rotation, retry identity/idempotency, status-stream cancellation, risk/validity separation, and provider-policy cooldown.

The operating principle remains: reuse known evidence, perform only work that can improve the decision, persist useful observations, and learn from real outcomes.
