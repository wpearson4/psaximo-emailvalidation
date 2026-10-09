# Incremental architecture improvement plan

Retain the public adapters, MongoDB as the durable authority, Azure Service Bus for scheduled work, and the current strategy/port structure. Establish a single evidence contract across classification, persistence, planning and retry. Preserve existing diagnostic fields and add explicit semantics rather than renaming persisted enums in place.

## Phase 1 Immediate improvements

Start with EV01, EV02 and EV03. Make DNS result type authoritative across evaluator/retry/presentation. Separate evidence clocks and require each retry to obtain evidence newer than its preceding attempt. Add focused end-to-end synthetic regressions before deploying each change. These changes address reproduced failures without introducing new infrastructure.

Implement EV06 mailbox identity with a versioned key migration. Preserve domain lowercasing and IDNA, preserve local-part case by default, and allow any provider-specific equivalence only through an explicit tested policy. Old case-folded evidence must be refreshed rather than guessed apart.

Deliver EV11 additive confidence metadata and a stable public summary. Keep the old detailed status values during migration. Document Final Inconclusive and heuristic confidence clearly so clients do not discard recoverable addresses.

Split EV05 into bounded parser fixes first and TLS negotiation second. Introduce protocol replay coverage before enabling TLS broadly. Keep successful sender identity stable and honor all existing throttles.

Exit gate: all reproduced DNS/retry/identity/parser cases pass with intended semantics; no protocol regression in the existing suite; baseline benchmark definitions and artifacts are frozen. This phase is expected to improve correctness but earns no accuracy percentage claim until outcomes support one.

## Phase 2 Intelligence and distributed reliability

Complete EV04 retry leases and dispatch generations, EV07 domain CAS, and EV12 bounded caches/shared budgets. Store a worker owner, lease expiry, fencing token and dispatch generation alongside lifecycle intent. Keep network execution claims separate from outbox publish claims. Recovery must handle expired execution ownership, and a stale worker must not finalize over a new owner.

Extend the existing DomainIntelligence profile for EV08/EV09/EV14 with these fields:

| Addition | Purpose |
| --- | --- |
| RoutingObservedAt and RoutingExpiresAt | Preserve DNS freshness independently |
| RecipientBehaviorObservedAt and ExpiresAt | Bound endpoint behavior reuse |
| Endpoint fingerprint and MX preference coverage | Prevent one endpoint’s controls proving another endpoint’s discrimination |
| Control/target session references and evidence version | Audit independent confirmation and contradictions |
| Verification capability and restriction cause | Distinguish blocked/unknown from actual accept-all |
| Provider capability version and next useful check | Select conservative action and retry interval |
| Windowed response counts, sample size and evidence age | Avoid unbounded historical influence and arbitrary certainty |
| Profile version and last accepted observation sequence | Reject stale writes across workers |
| Independent routing evidence source and expiry | Support authoritative catch-all facts without inventing them from SMTP |

Add `ValidationIntent` or equivalent request metadata: interactive, batch, scheduled fresh-observation retry. The planner produces explicit actions such as refresh routing, refresh endpoint behavior, probe target, or return current non-discriminating evidence. It must state the evidence gap each action can resolve and the relevant budget. Do not expand parallel probing just to increase throughput.

Implement EV13 current risk overlays and retention. A reusable transport fact must not freeze a later suppression or customer policy decision. Keep tenant context out of shared anonymous domain facts, but retain tenant scope on outcomes, access, and current customer decisions.

Build EV10 authorized outcome ingestion through the existing service/store and a reproducible benchmark command. Keep all new model/rule outputs in shadow mode until the release gates pass. Integrate an existing authorized delivery source if available; do not send messages merely to create labels without authorization.

Exit gate: distributed fault tests pass; every count-based inference has a time scope/sample size; endpoint ambiguity cannot promote certainty; provider segments show acceptable precision/recall/coverage and lower or justified SMTP cost. Rollout may remain heuristic indefinitely if labeled data is insufficient.

## Phase 3 Advanced capabilities

Train and calibrate separate named targets only after data sufficiency gates pass. Use existing offline logistic training as the interpretable baseline; compare against the deterministic engine on out-of-time and unseen-domain cohorts. Do not add a more complex model unless its measured benefit exceeds its operational cost.

Introduce drift review for novel response fingerprints, changing restriction rates, calibration drift and topology changes. Drift should trigger review, increased abstention or rollback, not automatically relax restrictions or promote acceptance.

Test limited SMTP connection reuse only if it can preserve stage isolation, RSET state, endpoint identity, small recipient limits and provider policies. Stop on blocks; cap session age and total commands. Compare connection count, latency and provider restriction rates in authorized fixtures and controlled production cohorts. Connection reuse is an optimization hypothesis, not a prerequisite to fix accuracy.

Optional differentiators include customer-visible evidence history, next-action explanations, authenticated domain-owner routing attestations and completion callbacks for long jobs. Keep age/alias enrichment optional unless customer experiments establish value.

## Versioning and rollout

Persist evidence schema, provider capability, classification, normalization and model versions separately. Deploy dual readers before new writers; invalidate incompatible evidence explicitly. Keep old status semantics available during API migration. For every policy change, retain baseline replay results, candidate shadow comparisons and a rollback configuration. Roll back rules, not source IP identity, when provider behavior worsens.

No new Redis, Kafka, validation subscription, or database is required by this plan. Use existing Mongo conditional writes and Service Bus capabilities first; add infrastructure only if measured bottlenecks justify it.
