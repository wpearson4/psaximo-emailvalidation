# SMTP reputation protection

## Outcome

Live SMTP is now a last-resort operation guarded by durable, hierarchical reputation policy. The policy evaluates mailbox, recipient-domain, provider-plus-outbound-identity, provider, and configured network-block state before a connection is opened. It defaults to `Observe`; `Enforced` suppresses the probe and returns a retryable, explicit `ReputationPolicyDeferred` outcome.

The implementation does not send `DATA`, invent new SMTP recipients, rotate source IPs after a restriction, or fall back across provider identity groups.

## Initial gap and reuse matrix

| Capability | Initial state | Resolution |
| --- | --- | --- |
| Memory and Mongo result reuse | Present | Reused before live SMTP; avoidance metrics made explicit |
| Cross-request mailbox single-flight | Present | Reused; joined callers do not consume another live-probe budget |
| Domain intelligence and catch-all reuse | Present | Reused; SMTP remains after reusable evidence |
| Domain/provider pacing | Present, process-local | Retained as the short-interval pacing layer |
| Narrow provider circuit | Present, process-local | Retained; durable hierarchy adds cross-worker authority |
| Sender/domain affinity | Present | Retained; sender-only failures may rotate the sender, not the source IP |
| Outbound identity affinity and health | Present | Reused; identity is selected once per validation attempt |
| FCrDNS readiness | Present | Reused before reputation evaluation and socket binding |
| Durable retry/lifecycle | Present | Reused; reputation deferral is scheduled without consuming an attempt |
| Normalized SMTP classification | Present | Reused as the source of pressure attribution |
| Durable probe budgets | Missing | Added with optimistic Mongo versioning and bounded counters |
| Hierarchical circuits | Missing | Added for domain, provider-identity, provider, and network scopes |
| Observe/enforce comparison evidence | Missing | Added to SMTP evidence, lifecycle attempts, and validation observations |

`DomainSmtpProbeThrottle` remains responsible for local concurrency, jitter, and immediate cooldown. `SmtpReputationProtectionService` owns the durable, cross-worker decision. These are complementary controls, not duplicate policy engines.

## Decision hierarchy

The most restrictive applicable decision wins:

1. Configured network block, currently `64.182.22.160/28`.
2. Recipient provider.
3. Provider plus selected outbound identity.
4. Recipient domain.
5. Exact normalized mailbox.

Mailbox reservation is an atomic optimistic update before SMTP. This prevents two workers from both treating a new mailbox budget as available. State is stored as one compact, versioned document per scope in `EmailValidationSmtpReputationState`; MongoDB is the operational authority. A conservative local mirror protects a worker during a transient store fault. In `Enforced`, an unavailable store with no known state safely defers; in `Observe`, SMTP continues and records what the fallback decision would have been.

Elasticsearch is not queried in the synchronous decision path. Reputation fields are additive to the existing validation-observation and lifecycle evidence models, so an analytics projection can consume them without becoming operational authority.

## Signal policy

| Signal | Scope affected | Health/circuit effect | Retry behavior | Identity behavior |
| --- | --- | --- | --- | --- |
| `MailboxNotFound`, `MailboxDisabled`, `RecipientRejected` | Mailbox/domain counters; unknown-recipient ratio only at domain | May open the recipient-domain circuit after the sample floor | Defer until cooldown; never make a provider/network inference from one mailbox | Keep selected identity |
| `ProviderRateLimit`, `ProviderConnectionLimit`, `PolicyBlock`, `IpPolicyBlock`, `ReputationBlocked`, `VerificationRefused` | Domain, provider-identity, provider, network | Degrade/open when configured count, rate, and breadth gates are met | Durable retry at circuit cooldown | Do not cycle IP; provider-group boundaries remain intact |
| Timeout, connection failure, temporary deferral | Availability counters | Evidence only under current policy; no single-failure circuit trip | Existing bounded provider retry and durable retry apply | Keep selected identity |
| Accepted, recipient-rejected, mailbox-full conclusive response | Applicable half-open scopes | Counts toward gradual recovery; rejection is not treated as identity damage | Normal validation outcome | Keep selected identity |
| Mailbox full | Mailbox/domain observation | Not unknown-recipient and not policy-block pressure | Existing mailbox-full policy applies | Keep selected identity |

Provider circuits require pressure across at least two affected identities. The network circuit requires at least two providers and three identities by default, so one noisy provider cannot quarantine the `/28`. Circuit cooldown transitions to `HalfOpen`; only the configured bounded probes are admitted. Conclusive successes move the scope to `Degraded` and then `Healthy`. A policy failure during half-open reopens the circuit.

## Configuration defaults

| Setting | Default |
| --- | ---: |
| `Enabled` | `true` |
| `Mode` | `Observe` |
| `NetworkBlock` | `64.182.22.160/28` |
| `WindowMinutes` | `60` |
| `FailureFallbackMinutes` | `5` |
| `PolicyVersion` | `2026.08.1` |
| Mailbox minimum interval | `60` minutes |
| Mailbox maximum | `2` probes per 24 hours |
| `CircuitBreaker.Enabled` | `true` |
| Minimum circuit sample | `20` observations |
| Circuit cooldown | `30` minutes |
| Half-open maximum | `2` probes |
| Recovery successes required | `3` |
| Provider-identity direct block count | `3` |
| Provider breadth | `2` identities |
| Network breadth | `2` providers and `3` identities |
| `UnknownRecipientPressure.Enabled` | `true` |
| Unknown-recipient domain open ratio | `0.50` after `20` RCPT observations |
| `PolicyBlockPressure.Enabled` | `true` |
| Policy degraded/open ratios | `0.15` / `0.30` after `10` observations |
| Persistence collection | `EmailValidationSmtpReputationState` |

The complete example is [appsettings.outbound-identities.json](../examples/appsettings.outbound-identities.json). Startup validation rejects malformed CIDRs, a reputation network that differs from the outbound identity CIDR, unsafe breadth/sample values, invalid ratios, missing policy versions, and colliding Mongo collection names.

## Evidence and metrics

Each SMTP result may carry the actual decision, shadow decision, rollout mode, restricting scope, circuit state, retry time, suppression reason, selected identity, scope-state summary, mailbox probe count, evaluation time, and policy version. The same decision fields flow into validation observations and revalidation lifecycle attempts.

Meters emitted by the implementation include:

- `smtp_validation_required_total`, `smtp_validation_avoided_total`, `smtp_validation_performed_total`
- `smtp_probe_allowed_total`, `smtp_probe_deferred_total`
- `smtp_connection_attempt_total`, `smtp_rcpt_attempt_total`
- `smtp_unknown_recipient_total`, `smtp_policy_block_total`
- `smtp_provider_rate_limit_total`, `smtp_ip_policy_block_total`
- `smtp_reputation_observe_would_block_total`
- `smtp_circuit_open_total`, `smtp_circuit_half_open_total`, `smtp_circuit_closed_total`
- `smtp_reputation_half_open_probe_total`, `smtp_reputation_half_open_success_total`, `smtp_reputation_half_open_failure_total`

Before enforcement, alert on any persistent store failure, any unexpected network-scope shadow block, provider shadow-block rates above 10% for 15 minutes, and a rising performed-to-required ratio without a corresponding freshness or traffic change. Operational alert rules belong in the deployment monitoring repository, not in this application.

## Rollout

1. Deploy with `Enabled=true` and `Mode=Observe`.
2. Compare actual SMTP outcomes with `WouldDecision` for at least one full traffic cycle. Segment by provider, identity, domain, and network scope.
3. Verify Mongo update-conflict and fallback rates are negligible, and tune sample/rate thresholds from observed traffic.
4. Enable `Enforced` only after validating that network/provider breadth gates do not produce false quarantines.
5. Roll back immediately by setting `Mode=Observe`; use `Disabled` only when the evaluation and recording path must be bypassed entirely.

## Remaining operational gaps

- Production thresholds still require calibration from Observe-mode traffic; the defaults are deliberately conservative, not claimed as universally optimal.
- Alert rules and dashboards must be added to the deployment's monitoring stack.
- The repository has no outbound Elasticsearch observation projector. The application now supplies projection-ready additive evidence, but implementing a sink should follow the existing analytics pipeline when one exists. Elasticsearch must remain analytics-only.
- Azure deployment, secret/configuration publication, and live provider verification remain environment work; none is required to compile or test this policy offline.
