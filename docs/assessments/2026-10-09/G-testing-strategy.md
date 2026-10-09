# Testing and accuracy measurement strategy

Measure correctness, real-world classification performance, and operational efficiency separately. The current passing suite is the regression baseline; authorized ground truth is the accuracy baseline. A production accuracy claim requires the latter.

## Existing test evidence

At baseline `3c5a1ccb62ea1f4da4fb1ba85a7f3f8bfa10e404`, local .NET SDK 10.0.301 runs passed:

| Project | Passed | Failed | Skipped | Coverage established |
| --- | --- | --- | --- | --- |
| Core.Tests | 572 | 0 | 0 | Deterministic policy, fakes, caches, provider/catch-all behavior, retry and model contracts |
| Api.Tests | 52 | 0 | 0 | In-memory API with fake validator/services, authorization and result contracts |
| Grpc.Tests | 2 | 0 | 0 | gRPC status mapping |
| IntegrationTests | Not run | Not measured | Not measured | Mongo and live DNS require explicit environments |

The existing tests cover normalization/SMTPUTF8, SMTP evidence and response rules, provider strategies, catch-all independent confirmation, topology reuse, risk separation, feature labels/splits/scoring, outbound identity/reputation, bulk jobs and status delivery. The supplied response-intelligence fixture corpus is useful for replay but is not a verified outcome dataset. No line-coverage percentage or load capacity is asserted.

The integration tests return normally when environment configuration is absent, so a successful unconfigured solution test run can overstate exercised coverage. CI should explicitly mark unavailable integration tests skipped and require configured suites in a separate release gate. [LiveDnsTests.cs · if (!string.Equals(Environment.GetEnvironmentVariable](/Users/wpearson4/projects/EmailValidation/tests/EmailValidation.IntegrationTests/LiveDnsTests.cs:14), [MongoValidationIntelligenceStoreTests.cs · if (string.IsNullOrWhiteSpace(connectionString)) return;](/Users/wpearson4/projects/EmailValidation/tests/EmailValidation.IntegrationTests/MongoValidationIntelligenceStoreTests.cs:16).

## Missing regression layers

1. **Cross-layer DNS:** resolver → domain service → evaluator → retry → lifecycle/API, including every transient failure. Unit tests of the classifier alone miss the NoMailRouting contamination.
2. **Evidence time:** fake-clock routing TTL, zero TTL, SOA negative TTL, stale controls, catch-all refresh and topology changes. Cache writes must not renew observations.
3. **Fresh retries:** execute the real reuse wrapper during a scheduled retry, with cooldowns shorter/longer than reuse TTL. Measure new-evidence yield rather than invocation count.
4. **Protocol transcripts:** consistent/inconsistent multiline codes, missing delimiters, oversized replies, partial reads, disconnect at each stage, delayed greetings, RSET/QUIT failure, TLS requirement/handshake/certificate failure, and SMTPUTF8 after post-TLS EHLO or HELO fallback.
5. **Identity:** case-distinct local parts across every key path; 64/65-octet local boundaries, total path limits, Unicode bytes, IDNs, quoted local parts and explicit unsupported input policies.
6. **Endpoint behavior:** accepted target on MX B with rejected controls on A; equal/lower-priority failover; mixed gateway/backend behavior; randomized-prefix rejection; time-separated independent confirmation and contradictions.
7. **Distributed faults:** two worker instances, two Mongo clients, stale domain writes, process death after claim, lease renewal/loss, schedule acknowledgement lost, duplicate detection enabled, same attempt rescheduled, outbox recovery and DLQ finalization.
8. **Operational bounds:** repeated cache replace/remove, many domains/addresses, result-channel backpressure, provider hotspot fairness, aggregate provider concurrency and persistence outage fallback.
9. **Current risk:** suppression update on hot-cache hit, tenant policy isolation, retention/deletion across operational stores and projections.
10. **Transport consistency:** REST/gRPC/CSV report the same status, confidence type, recipient behavior, evidence age and finality.

Use fake DNS and scripted SMTP streams for these cases. Loopback SMTP/TLS fixtures may verify the transport itself. Block external SMTP/DNS in the test environment; never use random third-party addresses to generate a benchmark.

## Benchmark cohorts and labels

Each record needs a pseudonymous mailbox/domain key, authorization/provenance, provider and endpoint scope, input policy version, evidence time, truth time/window, truth type, confidence, and cohort type (`synthetic` or `authorized_real`). Keep raw recipients outside report artifacts and restrict the minimum operational mapping.

| Cohort | Truth source | Expected evaluation |
| --- | --- | --- |
| Known-valid mailbox | Authorized owner confirmation or managed test-domain mailbox directory | Positive mailbox-existence truth within a stated time window |
| Known-invalid mailbox | Authorized managed-domain nonexistence or verified recipient-specific permanent rejection tied to actual permitted delivery | Negative mailbox-existence/technical-recipient truth; exclude policy blocks |
| Confirmed catch-all routing | Controlled domain configuration plus authorized routed-delivery evidence | Routing truth distinct from public accept-all |
| Public accept-all without routing truth | Scripted endpoint or authorized observed endpoint | Accept-all behavior accuracy; mailbox existence remains unlabeled |
| Non-catch-all | Controlled directory where target acceptance and arbitrary nonexistent rejection are known | Recipient-specific behavior and mailbox accuracy |
| Provider-restricted/gateway | Authorized traces plus controlled simulations | Correct Inconclusive/restriction cause, not presumed negative |
| Greylist/rate-limit/temporary disconnect | Scripted state transitions; approved real traces when available | Correct retry time/cause, no permanent invalidity, recovery yield |
| NXDOMAIN/Null MX/no-address/transient DNS | Controlled authoritative DNS or wire-response fixtures | DNS reason/expiry/classification invariants |
| Ambiguous SMTP and multi-MX conflict | Labeled synthetic transcripts | Abstention and evidence consistency |
| SMTPUTF8 and syntax limits | Standards/product-policy fixtures | Correct normalization, support and transport behavior |

A provider’s accepted-delivery event measures acceptance by a receiving system, not inbox placement or user ownership. In accept-all/gateway cases, do not use it as mailbox-existence truth. Missing bounce events, opens or clicks alone are not definitive labels. Treat suppression, policy rejection, complaint and temporary bounce as distinct outcomes; do not turn every bounce into nonexistent mailbox.

Use the existing `OutcomeDefinitionCatalog`, ingestion validation, label maturation and dataset builder. Bind outcomes to the correct validation/send attempt and tenant; keep conflicting or incomplete outcomes unresolved. Preserve selection bias information: historical sends may exclude rejected/unknown predictions, so an outcome-only sample cannot estimate all-address recall. Include independently known valid/invalid controlled mailboxes and representative permitted cohorts. Do not initiate new deliveries simply to fill labels without authorization.

## Split and compare without leakage

Freeze a baseline dataset, classifier version, capability profile, configuration fingerprint and outcome cutoff. Run both baseline and candidate on identical captured evidence for interpretation changes. For changes to evidence acquisition, replay controlled server state and then use a bounded authorized shadow/canary experiment; cached transcript replay cannot estimate how TLS or fewer controls changes remote behavior.

Use chronological training/calibration/test boundaries and mailbox-group deduplication. Hold whole domains out to evaluate generalization, including provider-specific custom domains. Keep retries, repeated addresses and correlated endpoints from leaking across partitions. The repository already implements an earliest-mailbox deduplication/time/domain splitter; document its sampling consequences and test that outcome windows cannot leak into features. [EvidenceBackedClassification.cs · public sealed class LeakageSafeDatasetSplitter](/Users/wpearson4/projects/EmailValidation/src/EmailValidation.Application/EvidenceBackedClassification.cs:545).

Report separate slices for provider, gateway/consumer/custom domain, recipient behavior, input source, Unicode, fresh/reused evidence, evidence age, retry stage and new/seen domain. Deduplicate or cluster by domain for uncertainty estimates; millions of addresses behind one gateway are not millions of independent provider experiments.

## Metric definitions

For a binary mailbox-existence cohort, define positive truth as known existence and negative truth as known absence. Fix in advance which detailed statuses count as a positive/negative **action**. Suggested strict evaluation: Valid is positive, Invalid negative, and LikelyValid/LikelyInvalid/Risky/CatchAll/Unknown abstain. Also publish a separate consumer-policy evaluation when likely statuses are acted upon. Do not silently change the mapping between versions.

| Metric | Definition |
| --- | --- |
| False-positive rate | FP / all truth-negative cases; publish abstentions among negatives alongside it |
| False-negative rate | FN / all truth-positive cases; publish abstentions among positives alongside it |
| Precision | TP / (TP + FP), where denominator is positive predictions |
| Recall | TP / all truth-positive cases; abstained positives count as unresolved, not recovered |
| False-valid rate | FP / (TP + FP), complementary to precision; not the same denominator as false-positive rate |
| False-invalid rate | FN / (TN + FN), error among negative predictions |
| Inconclusive rate | Unknown/Inconclusive / all requests, plus labeled-cohort abstention and actionable coverage |
| Catch-all accuracy | Confusion matrix for recipient-specific, accept-all, confirmed routing, restricted and unknown against corresponding known truth; macro precision/recall and abstention |
| Validation latency | Mean plus p50/p95/p99 elapsed per request, and request-to-final duration; split cache/live, provider and retries |
| Retry frequency | Fraction of validations scheduling a retry, dispatches and fresh observations per validation, with useful-resolution yield |
| Infrastructure cost | Allocated compute time + DNS/SMTP network + Mongo operations/storage + Service Bus operations + projection overhead, divided by validations; also cost per resolved result |
| Restriction rate | Provider blocks/rate limits per attempted SMTP session and per domain window |
| Calibration | Brier score, log loss, calibration plots, expected calibration error and interval estimates on a named matured outcome |

The existing `ProbabilityModelEvaluator` has Brier/log loss/ECE, false-valid/false-invalid rates and abstention; it does **not** supply all requested confusion-matrix metrics. Its calibration line is ordinary regression of binary label on probability, not the usual logistic calibration intercept/slope on log odds. Label that statistic explicitly or implement the intended logistic calibration diagnostic. Empty segments currently return zero metrics; change reports to `not estimable` with n=0. Wald standard errors can collapse to zero at zero/all successes; use Wilson/exact intervals or domain-cluster bootstrap. [EvidenceBackedClassification.cs · private static ProbabilityEvaluationMetrics Calculate](/Users/wpearson4/projects/EmailValidation/src/EmailValidation.Application/EvidenceBackedClassification.cs:602), [EvidenceBackedClassification.cs · private static (double Intercept, double Slope) CalibrationLine](/Users/wpearson4/projects/EmailValidation/src/EmailValidation.Application/EvidenceBackedClassification.cs:631).

No numerical cost baseline can be given from source alone. Use actual existing infrastructure rates and measured resource utilization; report marginal cost separately from fully allocated cost and keep the same accounting between versions.

## Sample size and confidence

Choose sample size from the tolerated false-valid/false-invalid error and required provider slices before evaluating. As a planning illustration, with independent cases and zero observed errors, the one-sided 95% upper error bound is `1 − 0.05^(1/n)`, approximately 3/n: 300 cases bound error near 1%, and 3,000 near 0.1%. Domain correlation reduces effective sample size, so these are not promises for this platform. Report denominators, cluster-aware intervals and missing-label rates; never market a point estimate with tiny provider samples as calibrated accuracy.

Calibrate probabilities on a held-out calibration cohort after training. Identify the outcome and horizon, show reliability bands with counts, and abstain outside supported providers/data distributions. Compare the existing heuristic baseline with a simple logistic baseline before considering additional model complexity. Artifact acceptance must include data sufficiency, provenance, cohort coverage, evaluation results and rollback metadata.

## Baseline and post-change release gates

1. Save baseline test summaries, synthetic reproduction output, dataset manifest/hash, feature/rule/profile versions and all segmented metrics. Production incidence remains unknown until this step includes authorized real cohorts.
2. Require every deterministic invariant to pass: temporary failure is never permanent mailbox rejection; pre-RCPT evidence never proves a mailbox; accept-all never proves existence; stale evidence cannot be refreshed by storage; no blocked-provider bypass; one valid retry lease owns an attempt.
3. Compare candidate to baseline using paired domain-aware confidence intervals. Select business tolerances before viewing candidate results. Set segment-specific minimum support; insufficient support prevents a claim or broad rollout.
4. Require no unacceptable increase in false-valid/false-invalid rates, and report any recall/coverage tradeoff. A higher Inconclusive rate may be justified when correcting false certainty, but must be explicit.
5. Require useful retry yield, provider restriction rate, sessions/address, latency and cost to remain within approved budgets. Load tests must not contact third-party SMTP servers.
6. Shadow first, then a small authorized production cohort under existing provider limits. Monitor new response fingerprints, status shifts, evidence-age distributions and error/retry/cost changes. Roll back the candidate policy/configuration on breach, retaining observations for analysis.

Implementation is complete only when its acceptance tests pass; an accuracy improvement is established only when the corresponding held-out outcomes support it. Keep those two conclusions separate.
