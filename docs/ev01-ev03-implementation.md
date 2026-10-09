# EV01–EV03 implementation and verification

This implements the first three items from the [2026-10-09 assessment backlog](assessments/2026-10-09/F-prioritized-backlog.md). It changes the existing validator, caches, and retry worker without adding services, queues, collections, or other infrastructure. Deployment has not been performed.

## EV01: DNS outcomes remain authoritative

`DnsLookupResult.Status` governs classification, routing details, bounce risk, evidence quality, presentation, and retry eligibility. Timeout and failure remain Unknown with partial evidence; an empty result from a failed lookup cannot add the terminal `NoMailRouting` reason. Typed DNS outcomes also take precedence over contradictory legacy routing reason codes.

The implicit address fallback distinguishes negative answers (`HostNotFound`, `NoData`) from transient resolver errors. Socket timeouts retain the Timeout status. Caller cancellation propagates. The wire parser checks response identity, flags, question, record bounds and MX data lengths; malformed and mixed ordinary/Null MX answers become structured failures. UDP accepts responses only from the connected resolver endpoint. Valid NXDOMAIN, Null MX, and successful lookup with no route remain permanent negatives.

The synthetic matrix covers the full domain acquisition → classification → evaluation → presentation → retry policy path, plus injected resolver failures and malformed DNS packets. The two original transient-DNS regressions were run and failed before the fix.

## EV02: evidence expires from observation time

Domain intelligence now carries separate routing, provider, and authentication observation/expiry pairs. Catch-all behavior has its own observation and absolute expiry. Provider identity derived from MX inherits the routing clock; authentication uses its own observation and the configured DNS cache policy. Updating recipient behavior changes neither the routing observation nor routing expiry. Persistent and memory cache writes honor an existing absolute expiry.

Routing lifetime is bounded by the observed TTL and the configured maximum; the legacy minimum freshness setting no longer extends a DNS TTL. Zero TTL is immediately stale for reuse, and 30 seconds stays 30 seconds. NXDOMAIN negative TTL uses the smaller of SOA TTL and SOA MINIMUM, following [RFC 2308](https://www.rfc-editor.org/rfc/rfc2308.html). A negative response with no available negative TTL is not reused. The operating-system address fallback exposes no address TTL, so positive fallback is capped by the missing-TTL policy and combined negative fallback has zero TTL. Invalid Null MX combinations are treated as uncertainty under [RFC 7505](https://www.rfc-editor.org/rfc/rfc7505.html).

Legacy records without component clocks are bounded by their original observation plus the available TTL or the conservative missing-TTL/transient horizon. Reading or rewriting a legacy record does not create a new observation. New fields are additive and round-trip through the existing JSON payloads used by persistence. Expired persisted evidence remains historical context; it cannot suppress required refresh work.

The eight initial expiry regressions were run and failed before the fix. Additional tests cover negative TTL parsing, conservative legacy reuse, topology invalidation, behavior updates, and a real local JSON-store restart across a fake-clock expiry boundary.

## EV03: retry budgets count new observations

The worker supplies `EmailValidationRequest.EvidenceObservedAfter`, based on the preceding attempt and its evidence clocks. Such requests bypass mailbox/result reuse and cannot join an ordinary validation flight. Unresolved transient DNS is refreshed when its evidence predates the boundary; genuinely fresh successful routing remains reusable. SMTP retry requests perform a mailbox observation through the existing throttle, sender-health, session-budget, and reputation controls.

`MailboxEvidenceObservedAt` records completion of an actual probe. Before committing a retry result, the worker requires a newer mailbox observation, relevant routing observation, or attempted catch-all refresh. A new result-generation timestamp alone is insufficient. Old evidence, including an incorrectly labeled live result, reschedules the same observation attempt without consuming its budget. Existing cooldown deferrals and maximum observation counts remain enforced.

Retry scheduling includes cause-specific minimums, configured provider block cooldowns, the accept-all confirmation separation, and positive jitter. Existing backoff, result RetryAfter, and local/provider cooldowns remain lower bounds.

The five-second retry/two-minute result-reuse case and stale-result budget-consumption case were run and failed before the fix. End-to-end synthetic tests run the real validator, intelligence/result caches, retry processor, and lifecycle coordinator with fake DNS/SMTP/time. They verify DNS recovery, fresh SMTP with unchanged DNS reuse, final negative results, and exhaustion after a genuinely new temporary failure. Separate tests verify cache/live mislabeling cannot consume an attempt, cooldown preservation, and scheduling floors.

## Configuration defaults

All keys are under `EmailValidation`.

| Key | Default | Meaning |
| --- | ---: | --- |
| `DomainIntelligence:MissingRoutingTtlSeconds` | 60 | Upper bound when a positive routing TTL is unavailable |
| `DomainIntelligence:TransientDnsFreshnessSeconds` | 5 | Short reuse horizon for DNS timeout/failure |
| `Revalidation:MinimumRetrySeconds` | 5 | General retry floor |
| `Revalidation:GreylistRetrySeconds` | 300 | Greylisting floor |
| `Revalidation:MailboxFullRetrySeconds` | 1800 | Mailbox-full floor when eligible for retry |
| `Revalidation:MaximumPositiveJitterMilliseconds` | 1000 | Adds 1–1000 ms to the cause floor; 0 disables jitter |

`DomainIntelligence:MinimumFreshnessMinutes` remains bindable for configuration compatibility but is no longer a TTL floor. Existing provider-specific block cooldown and maximum-attempt settings still apply.

## Verification and rollout

Verified locally on 2026-10-09: **616 Core, 52 API, and 2 gRPC tests passed** (670 total). The full solution build completed with zero warnings and zero errors; `git diff --check` passed. The Core suite includes 44 additional cases over the assessed baseline.

Run the following without live DNS/SMTP targets:

```sh
dotnet test tests/EmailValidation.Core.Tests/EmailValidation.Core.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test tests/EmailValidation.Api.Tests/EmailValidation.Api.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test tests/EmailValidation.Grpc.Tests/EmailValidation.Grpc.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false
dotnet build EmailValidation.sln --no-restore -m:1 -p:UseSharedCompilation=false
```

The Core tests use synthetic DNS/SMTP and fake clocks; the API/gRPC tests use local test hosts. No production database, broker, DNS, or SMTP probes are needed. The Mongo/Service Bus integration environment has not been exercised by this change.

Apply the corresponding regression gate before each deployment. Deploy the updated validation host and retry worker together for EV03: old producers do not emit the new observation stamp and their results will correctly fail the freshness gate. Shorter and conservative legacy TTLs intentionally increase DNS refreshes during transition. EV04's execution fencing and broker dispatch identity work remains outside this change.
