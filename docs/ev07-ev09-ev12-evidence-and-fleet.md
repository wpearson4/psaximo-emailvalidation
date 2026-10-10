# EV07, EV09 and EV12: shared evidence and endpoint accuracy

This change set prevents stale concurrent domain writes, prevents controls from one MX from establishing another MX's recipient discrimination, bounds evidence caches, and adds optional shared SMTP concurrency reservations on the existing MongoDB service. It does not deploy infrastructure or activate fleet enforcement automatically.

## EV07 — Monotonic domain evidence

Mongo domain writes read the current profile, merge component observations, and conditionally update its `ProfileVersion`. Failed comparisons retry up to `Persistence.DomainWriteRetryLimit` (default 8). Conflicts and exhaustion are recorded through persistence metrics. Exhaustion fails the write; an uncommitted snapshot is never presented as a successful cache update. Database write failures propagate instead of populating a falsely authoritative cache.

Routing, authentication, provider, and recipient behavior retain separate clocks. A refresh attempt cannot outrank a newer observation or extend routing authority. `BehaviorEvaluatedAt` records a later target-based confirmation or contradiction without changing the controls' observation time. Conflicting behavior at an equal evidence time becomes Unknown. Topology transitions invalidate controls from an earlier visit to that topology, including A → B → A. Profile updates preserve the separately appended observation arrays and their existing bounded retention.

Documents without `ProfileVersion` migrate on their next conditional write. Deploy the version-aware writer to every writer host before relying on this guarantee; old binaries can still perform unconditional writes. The JSON implementation coordinates merge/write operations across instances in one process; it is not a cross-process database. Observation timestamps still depend on correctly synchronized hosts.

## EV09 — Endpoint and routing provenance

The recipient-behavior evidence contract is now `recipient-behavior-evidence-v3`. Controls carry a normalized MX host, preference, provider/gateway, complete MX topology fingerprint, strategy version, and acquisition ID. Strong acceptance requires actual MAIL FROM/RCPT TO evidence from that endpoint, rejected controls, compatible routing/provider/policy, and a target observed after the controls within five minutes. Recipient-specific controls expire within five minutes (or the shorter configured catch-all lifetime). Positive result reuse is capped by the control expiry.

Rejected controls on MX A followed by target acceptance on MX B return Unknown. Accepted peers lacking matching controls, and temporary responses from a probed equal-preference peer, cannot produce a confident positive. Explicit recipient rejections retain their existing interpretation and conflicting-MX protection. This implementation defines no endpoint equivalence groups: sharing a provider or gateway is insufficient.

Control scope fingerprints survive observation persistence. Accept-all confirmation still requires separated observations with accepted targets in their correlated validation sessions, now with matching endpoint/policy scope across sessions. Contradiction and protected-history retention remain active. Target-specific uncertainty is presented in `CatchAllEvidence`; the domain profile retains the original scoped controls. Randomized recipients use 128 random bits without the fixed `dwcheck-` prefix. The probe-count budget is unchanged; prefix-bias tests contact only synthetic endpoints.

### Independent routing attestations

An unverified `IndependentRoutingEvidence` label is no longer sufficient. The ingestion surface is signed configuration under `EmailValidation:CatchAll:RoutingAttestations`, also accepting a previously persisted signed attestation after re-verification:

* `Authorities`: entries containing `Id`, `PublicKeyPem`, and exact canonical `AuthorizedDomains` (domain lowercase/IDNA form; no implicit wildcard authorization).
* `Attestations`: signed `SignedRoutingAttestation` records with authority ID, unique attestation ID, canonical domain, MX topology fingerprint, issue time, expiry, and Base64 signature.
* `RevokedAttestationIds`: revoked IDs. Removing an authority also revokes trust in its attestations.

An authorized routing administrator signs `RoutingAttestationPolicy.SigningPayload(record)` using RSA SHA-256 with PKCS#1 v1.5 padding and a key of at least 2048 bits. The payload is the versioned JSON array produced by that method; it includes both timestamps formatted in UTC. Only the public verification key belongs in service configuration. The signer is attesting to catch-all routing, not merely SMTP acceptance. Source authorization, signature, domain, topology, future issue time, expiry, and revocation are checked before use. Runtime verification state is excluded from JSON and must be rebuilt after persistence. No unauthenticated public ingestion endpoint or automatic source registration is added.

Use the existing controlled configuration deployment process for ingestion and revocation; hosts consume their loaded `IOptions` snapshot, so deploy/restart all hosts for authority or revocation changes. Expiry is checked on every acquisition/reuse. Routing attestations and their verification tests are separate from synthetic SMTP evidence.

### Version rollout

Defaults and the console configuration advance the validation engine to `1.3.0` and provider strategy to `1.4.0`. Update any external overrides to these versions as part of deployment. Prior policy results and pre-v3 recipient controls require fresh evaluation. Older case-folded mailbox evidence continues to follow EV06's refresh-only migration.

## EV12 — Bounded caches and fleet concurrency

The shared cache implementation has one bookkeeping node per key. Replacement and removal discard that node, and capacity and absolute expiry apply to domain, mailbox, and result caches. `Persistence.EvidenceCacheSizeLimit` defaults to 10,000 per evidence cache; result capacity remains `ResultReuse.MemoryCacheSizeLimit`. Evidence cache freshness defaults to 30 seconds and is configurable from 0–300 seconds. Zero disables evidence caching.

With Mongo, a new request sees persisted domain/mailbox contradictions after at most the configured local evidence-cache interval plus read latency; a hot result additionally checks persisted mailbox and domain evidence. Routing or control authority can expire sooner. JSON's nested domain caches can add two cache intervals, and JSON is intended for one process. This change does not claim to bound every unrelated observation, suppression, or scheduler dictionary.

`EmailValidation:Smtp:FleetBudget` defaults:

| Setting | Default |
| --- | --- |
| Mode | `Disabled` |
| Collection | `EmailValidationSmtpProbeLeases` |
| GlobalConcurrency | 16 |
| PerProviderConcurrency | 4 |
| PerDomainConcurrency | 1 |
| StoreTimeoutSeconds | 5 |
| RetrySeconds | 5 |

`Observe` records reservation outcomes but permits probes when capacity/store access would deny them. Only `Enforced` applies the aggregate limit and fails closed on store unavailability. This is distinct from the existing reputation protection mode. Non-disabled modes require MongoDB and a dedicated collection on the existing database. All fleet participants must use the same database, collection, provider-key mapping, limits, session timeout, and mode. Drain active probes before changing configuration; incompatible active scope configurations fail closed until their documents expire.

Each SMTP session reserves domain, provider and global capacity atomically per scope, without waiting. A denied later scope rolls back earlier reservations. This avoids holding capacity while waiting for a cooling provider and does not charge the per-validation session budget for a denied reservation. Deferrals flow through the existing Unknown/local-cooldown retry path. Metrics use `smtp_fleet_reservation_total` with mode/outcome, without recipient labels.

Reservations use Mongo server time and unique owner IDs. Owner-matched release cannot remove a replacement lease. A client execution deadline starts before acquisition at SMTP session timeout + 5 seconds; the server lease expires at session timeout + 15 seconds. No new renewal service is required because EV05 bounds the whole SMTP session. Failed cleanup is recovered by expiry, with a TTL index for scope cleanup. Cancellation is cooperative; as with EV04, no lease can retract a command already transmitted to an external SMTP server.

## Verification

Synthetic regressions cover CAS interleavings, legacy version migration, retry exhaustion, concurrent independent components, A → B → A, derived confirmation persistence, separate refresh/observation clocks, endpoint mismatch and prefix bias, stale/changed scope, signed/unauthorized/expired/revoked provenance, and protected confirmation history. Mongo tests cover two instances and separate processes, provider/domain/global reservations, stale release, server expiry, store failure, and persisted contradiction visibility. A churn fixture performs two million writes and one million removals while asserting bounded entries and bookkeeping.

Validation on 2026-10-09:

* Solution build: zero warnings/errors.
* Core: 735 passed; REST/API: 52 passed; gRPC: 2 passed.
* Isolated localhost MongoDB integration suite: 23 passed. Excluded the previously reproduced unrelated `Outbox_IsIdempotentAtomicallyClaimedReclaimableAndTtlSafe` parallel-arrays index failure, external DNS, and cloud Service Bus tests.
* Isolated churn run: two million writes plus one million removals in 274 ms; retained process heap delta 29,808 bytes; 127 entries and 127 nodes with capacity 128. This is a local synthetic observation, not a production throughput guarantee.
* An initial concurrent suite run hit the existing EV04 cancellation-observation timing assertion. It passed in isolation and in the final complete core run; no EV04 implementation was changed here.

These regressions reproduce failure mechanisms; they do not measure a production accuracy percentage. No third-party SMTP or production database was contacted.
