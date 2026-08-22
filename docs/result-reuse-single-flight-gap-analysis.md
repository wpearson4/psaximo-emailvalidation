# Result reuse and single-flight gap analysis

This analysis was completed before implementing the result-reuse changes. The existing validation engine and host-neutral service boundaries were retained; only incomplete or missing orchestration was added.

| Capability inspected | Classification before this change | Finding and disposition |
|---|---|---|
| Normalized mailbox identity | Already implemented correctly | `IEmailNormalizer` and normalized mailbox persistence already supplied the logical address key. Reuse and in-flight keys now use that identity plus request mode and all four policy versions; raw input is never a key. |
| Persistent mailbox/domain intelligence | Already implemented correctly | `IValidationIntelligenceStore` already separated mailbox results from current domain/MX/provider evidence. Its contracts and Mongo mapping were reused. |
| Mailbox freshness evaluation | Implemented but incomplete | `IValidationResultReusePolicy` already checked status TTL, SMTP strength, policy versions, domain expiry, and topology. It now returns a typed decision, uses strong-evidence timestamps, requires conclusive mailbox evidence for long positive/negative reuse, and recognizes short-lived transient/provider-block outcomes. |
| Domain reuse | Already implemented under another abstraction | The persistent domain cache and adaptive validation pipeline already reuse DNS, MX, provider, catch-all, and behavioral evidence independently. A stale mailbox therefore continues through live mailbox work without discarding fresh domain evidence. |
| Hot result cache | Missing | Added bounded `IValidationResultCache` and a process-local implementation with absolute policy-derived expiry. Persistent hits warm it; successful live results replace stale entries; cache failures degrade to misses/no-op writes. |
| In-flight coordination | Implemented but incomplete | `IValidationSingleFlight` already shared a task and removed completed/failed entries with waiter-aware cancellation. It previously enclosed persistence lookup. Orchestration now enters it only after memory and persistent reuse miss, and leaders recheck memory before live work. |
| Per-email locking / `AsyncLazy` alternatives | Implemented under another abstraction | The existing lazy task per normalized key is the single coordination mechanism. No parallel per-email lock or second task dictionary was added. |
| Cache invalidation | Missing | A successful live result removes the prior hot entry and caches only the newly evaluated reusable result. Unexpected live exceptions are never cached. |
| Policy versioning | Already implemented correctly | Validation-engine, classification, confidence-model, and provider-strategy versions existed. They now participate in both reuse decisions and in-flight/cache keys. |
| Reuse provenance | Missing | Additive metadata reports `LiveValidation`, `MemoryCache`, `PersistentReuse`, or `JoinedInFlightValidation`, while preserving the original validation time and separately reporting return time and reuse age. |
| Observability | Implemented but incomplete | Persistence counters existed. Added request, cache, reuse miss/rejection, live execution, leader/joiner, invalidation, avoided-live-work, and collapse-ratio measurements. |
| CSV duplicates | Already routed through shared application service | No CSV-specific cache was added. Concurrent duplicate rows now share the application-level flight; completed duplicates use the hot cache, and row order remains unchanged. `Validation Date/Time` uses the original evidence timestamp. |

The resulting dependency direction remains Core contracts/orchestration → Infrastructure cache/persistence implementations → host composition. The memory cache and single-flight coordinator are deliberately process-local; durable Mongo intelligence remains reusable across hosts, while future distributed coordination can implement the existing abstractions.
