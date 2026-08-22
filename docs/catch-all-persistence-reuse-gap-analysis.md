# Catch-all persistence and reuse gap analysis

This analysis was performed before implementation. The existing domain-intelligence architecture remains authoritative; no collection, cache, persistence service, or competing single-flight mechanism was added.

| Requirement inspected | Initial classification | Finding and implemented disposition |
|---|---|---|
| `CatchAllStatus` | Already implemented | The existing `NotCatchAll`, `LikelyNotCatchAll`, `LikelyCatchAll`, `Unknown`, and `NotAttempted` taxonomy is reused unchanged. |
| `CatchAllConfidence` | Already implemented | `CatchAllDetectionResult.Confidence` and the typed Mongo field already existed. A missing reusable-confidence policy now uses `CatchAll:MinimumReusableConfidence`. |
| `CatchAllReason` | Implemented differently/incomplete | Free-form `Detail` existed. Added a compatible structured `CatchAllReasonCode` while retaining `Detail` as the human-readable explanation. |
| `CatchAllObservedAt` | Missing | Added an evidence-specific observation time so reuse never replaces the original SMTP-evidence timestamp with result-generation time. |
| Catch-all evidence summary | Implemented differently/incomplete | Probe/accepted/rejected/ambiguous counts already existed in the model and payload. Mongo now also maps the summary to typed domain-document fields; raw SMTP transcripts remain excluded. |
| `DomainIntelligence` | Already implemented | Catch-all remains a property of normalized domain plus provider/MX topology. No mailbox-specific catch-all store was added. |
| `DomainBehaviorProfile` and observation history | Already implemented | Topology-scoped historical aggregation and bounded domain observations are reused. Inconclusive refreshes preserve prior positive history and record an unknown refresh observation. |
| `ValidationReusePolicy` | Implemented differently | The existing policy governs complete mailbox-result reuse. Domain catch-all reuse now lives in the validation planner so the persistence layer does not make execution decisions. |
| `ValidationPlanBuilder` | Missing | Added one focused planner that decides domain refresh, catch-all probing, mailbox probing, and persisted catch-all use from freshness, confidence, strategy version, and refresh backoff. |
| `MongoValidationIntelligenceStore` | Implemented but incomplete | Existing atomic domain upsert and unique normalized-domain index are reused. The same document now exposes reason, observed time, evidence counts, and catch-all strategy version; no collection was added. |
| Hot-cache update | Already implemented | `PersistentDomainValidationCache.StoreAsync` updates process memory before awaiting the durable save, so later batch rows benefit immediately and Mongo failure does not erase hot evidence. |
| Freshness | Implemented but incomplete | Domain expiry already existed. Catch-all freshness is now evaluated from its own observation time and existing catch-all TTL without introducing another TTL source. |
| MX/provider invalidation | Already implemented/incomplete at planning boundary | Topology fingerprints and provider detection existed. A domain refresh carries catch-all evidence forward only when the current topology and strategy version still match; otherwise normal discovery runs. |
| Policy/strategy versioning | Already implemented | The existing provider-strategy version is persisted with catch-all evidence and must match before reuse. Classification-policy changes continue to re-run classification rather than trusting a stored mailbox result. |
| Mailbox SMTP suppression | Missing | Fresh, high-confidence reused `LikelyCatchAll` evidence now suppresses RCPT probing when arbitrary-recipient acceptance makes it non-discriminating. Classification returns `CatchAll`, never `Valid`, and mailbox reliability stays unproven. |
| Result provenance | Implemented but incomplete | Existing result-source metadata gained `PersistentDomainIntelligence`; catch-all observation time remains separate from the newly generated result time. |
| Refresh failure behavior | Missing | An inconclusive refresh preserves historical positive evidence, marks it inconclusive, and applies the existing transient window as a retry backoff. Stale evidence cannot suppress mailbox SMTP during that backoff. |
| Contradictory evidence | Already supported by replacement semantics | A conclusive new random-recipient classification replaces the current snapshot while bounded observations retain history. |
| Domain single-flight | Already implemented | The existing domain-keyed semaphore collapses concurrent refreshes for different addresses. No second coordinator was introduced. |
| Metrics | Implemented but incomplete | Extended the existing persistence meter/snapshot with discovery, reuse, refresh, expiry, classification-change, catch-all-probe-avoided, and mailbox-probe-avoided counters. |

The implemented flow is therefore: load the existing domain record, let the planner validate its confidence/freshness/topology/version, reuse it when safe, otherwise perform one domain-coordinated refresh, update the hot cache immediately, and atomically upsert the same Mongo document.
