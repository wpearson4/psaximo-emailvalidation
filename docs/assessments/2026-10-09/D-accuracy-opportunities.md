# Ranked accuracy improvement opportunities

The following ranking prioritizes correctness and recoverable evidence over additional signals. No production accuracy gain or cost reduction was measured. “High expected impact” means a broad failure mechanism is addressed, not a predicted percentage improvement. Confidence describes confidence in the recommendation, not confidence in an email result.

Complexity: S is approximately 1–3 engineer-days; M is 4–8; L is 2–4 engineer-weeks, including targeted tests but excluding external approval/ground-truth collection time. These are planning estimates. Operational risk describes rollout risk; retaining the current defect may be riskier.

| Rank | Backlog | Expected accuracy or usefulness improvement | Confidence | Complexity | Incremental infrastructure cost | Operational risk |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | EV01 DNS outcome integrity | High: prevent recoverable DNS failure becoming no-route invalidity or losing retry | High; composition reproduced | M | Negligible; appropriate retries add bounded lookups | Low with replay tests |
| 2 | EV02 Component evidence expiry | High: prevent stale routing and outages from remaining current | High; expiry behavior reproduced | M | Some extra correct DNS refreshes; fewer stale decisions | Medium cache migration |
| 3 | EV03 Fresh evidence on retries | High: turn retry budget into actual new observations | High; schedule/reuse mismatch reproduced | M | Fewer queue-only attempts; useful probes may rise | Medium scheduling changes |
| 4 | EV04 Retry ownership and dispatch identity | High operational reliability; avoids duplicate/missing work | High mechanism; distributed incidence unmeasured | L | Mongo lease writes; no new service | Medium/high lifecycle migration |
| 5 | EV05 SMTP protocol and TLS | Medium/high in affected endpoints: avoid malformed acceptance and TLS-induced uncertainty | High protocol gap; provider coverage gain unmeasured | M/L | TLS CPU/handshakes; possible connection savings later | Medium |
| 6 | EV06 Mailbox identity and syntax limits | High per affected address; population size unknown | High; case collision and overlength input reproduced | M | Negligible steady-state; migration work | Medium key migration |
| 7 | EV07 Monotonic domain intelligence | Prevent newer evidence being overwritten under concurrency | High code evidence; rate unknown | M | Small conditional-write overhead | Medium |
| 8 | EV10 Outcome benchmark and release gates | Highest strategic value: establish actual accuracy and prevent silent regressions | High; outcome availability unknown | L plus collection time | Existing storage/compute only; bounded retention | Low in offline/shadow mode |
| 9 | EV09 Endpoint-scoped catch-all evidence | Reduce false-valid promotion on heterogeneous/pattern-filtering endpoints | High scope issue; bias impact hypothesis | M | Neutral with reuse; controlled tests only | Medium |
| 10 | EV08 Reuse non-discriminating behavior | Low direct certainty gain; high potential traffic and restriction reduction | High redundant-plan reproduction; savings unmeasured | M | Expected decrease in SMTP sessions | Medium freshness tradeoff |
| 11 | EV11 Public confidence semantics | Prevent clients interpreting heuristic numbers as deliverability odds | High contract evidence | S/M | Negligible | Low with additive contract |
| 12 | EV12 Bounded caches and shared budgets | Preserve availability and fleet-safe probe rates | High memory/process scope evidence | M/L | Neutral or reduced; Mongo leases if needed | Medium |
| 13 | EV13 Risk freshness and retention | Prevent stale send recommendations; reduce data exposure/storage | High cache path evidence; incidence unknown | M | Small per-request risk lookup; retention reduces storage | Medium |
| 14 | EV14 Provider capabilities and rule rollout | Better abstention, fewer useless retries, safe behavior adaptation | High architecture fit; accuracy lift hypothesis | M/L | Neutral; lower probe count possible | Medium if enforced |
| 15 | EV15 Honest authentication and dataset metadata | More accurate explanations; less irrelevant enrichment work | High implementation evidence | S/M | Neutral or lower | Low |

## What is measured

The evidence is a passing 626-test baseline and nine synthetic observations covering component composition, plans, parser behavior, identity and expiry. These are correctness measurements, not real-world validation benchmarks. No post-change results exist because this assessment intentionally changes no production code.

The principal hypotheses to test are that endpoint-specific negative-control evidence reduces false-valid classifications, delayed fresh retries improve resolution yield, and known accept-all suppression lowers provider block rates and cost. Measure all three against the same frozen baseline with abstention and provider segmentation; improving apparent precision by returning Unknown for everything is not success.

## What should not be prioritized

Do not purchase third-party validation coverage, infer mailboxes from SPF/DKIM/DMARC, guess downstream mail hosts behind gateways, rotate identities around provider blocks, or add random probes indiscriminately. Do not add a new “domain intelligence service” that duplicates the existing one. A learned classifier should wait for valid labels and proven release gates; a model artifact checksum alone is not evidence of calibration.
