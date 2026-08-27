# Provider-Aware SMTP Response Intelligence P0

## Repository gap analysis

| Capability | Repository status before this change | Action | Result |
|---|---|---|---|
| Single safe SMTP conversation through `RCPT TO` | IMPLEMENTED | Reused | The existing probe still performs connect, greeting, EHLO/HELO, MAIL FROM, RCPT TO, RSET, and QUIT. It never sends DATA. No second probe client or retry engine was added. |
| Provider detection and provider/gateway/mailbox-provider identity | IMPLEMENTED | Reused | Existing `ProviderDetectionResult` remains authoritative. Its family, gateway, and mailbox-provider fields are now retained in immutable attempt history. |
| Stage-aware response parsing | PARTIALLY IMPLEMENTED | Corrected | Candidate classification always receives the command stage and refuses to convert non-RCPT evidence into recipient invalidity. |
| Reply class and enhanced status parsing | PARTIALLY IMPLEMENTED | Corrected | Structured reply/enhanced-code interpretation now precedes provider and generic text rules. |
| Provider-aware interpretation | PARTIALLY IMPLEMENTED | Corrected | High-confidence Microsoft, Yahoo, Google, Proofpoint, and Mimecast refinements run only for an already detected provider. Generic rules do not guess a provider. |
| Normalized reason taxonomy | NOT IMPLEMENTED | Added | Added command, mailbox, sender, routing, greylist/temporary, provider pressure/policy, connection, greeting, EHLO, protocol, DNS, TLS, and unknown reasons. |
| Deterministic decision policy | NOT IMPLEMENTED | Added | Application policy independently maps classification to mailbox impact, result state, retry, cooldown scope, health impact, sender rotation, and compatibility category. |
| Regex safety and rule validation | IMPLEMENTED BUT INCORRECT | Corrected | Developer-owned rules are centralized, compiled outside the hot path with non-backtracking matching where compatible, explicit timeouts, bounded input, stable priority/tie-break ordering, duplicate-id validation, and safe timeout fallback. |
| Stable response fingerprints | NOT IMPLEMENTED | Added | Stable semantic fingerprints (for example `yahoo-ts01` and `generic-mailbox-not-found`) exclude raw responses, addresses, IPs, MX hosts, timestamps, and customer identifiers. |
| Domain/provider backoff and circuit breaking | IMPLEMENTED | Reused | Candidate policy feeds the existing throttle only in Enforced mode. Rate/connection pressure uses MX-provider scope; IP policy/reputation uses source-IP scope. No IP cycling was added. |
| Sender health and strict sender rotation | IMPLEMENTED | Corrected | Existing sender pool remains authoritative. Enforced candidate decisions rotate only for strongly sender-specific MAIL FROM failures; recipient or provider failures cannot rotate a sender. |
| Durable retry and idempotency | IMPLEMENTED | Reused | Existing lifecycle/outbox, deterministic `ValidationId:AttemptNumber` message ids, Service Bus queue, stale/final checks, and Mongo 4.4-compatible persistence remain unchanged. |
| Mailbox-full revalidation | IMPLEMENTED BUT INCORRECT | Corrected | In Enforced mode only, mailbox full can enter existing provisional retry/backoff. Shadow mode retains prior canonical behavior. |
| Immutable attempt evidence | PARTIALLY IMPLEMENTED | Corrected | Existing attempt history now retains stage, reply/enhanced code, normalized reason, fingerprint, scopes/health, provider identity, optional identity/topology context, versions, and rollout mode without storing raw SMTP text. No parallel Mongo aggregate was added. |
| Rollout controls | NOT IMPLEMENTED | Added | `Disabled`, `Shadow`, and `Enforced` are supported. Default is `Shadow`; shadow attaches candidate evidence and telemetry but cannot alter canonical categories, retries, cooldowns, or sender health. |
| Telemetry and replay coverage | PARTIALLY IMPLEMENTED | Corrected | Low-cardinality counters/histogram cover mode, provider, stage, normalized reason, multidimensional disagreements, candidate/regex failure, and classification latency. Sanitized versioned replay fixtures cover generic and common providers, ambiguity, malformed/long inputs, and safe fallback. |

## Reused, corrected, added, and deferred

Reused without duplication:

- `SmtpMailboxProbe` and its single bounded SMTP session sequence.
- Existing MX/banner provider detection and provider strategy resolver.
- `DomainSmtpProbeThrottle`, provider circuits, pacing, half-open recovery, and optional outbound-IP dimension.
- Probe-sender pool, affinity, health state, and strict attempt budget.
- Revalidation lifecycle/outbox, `email-validation-retry`, deterministic message ids, worker idempotency/final-state guards, and status publishing.
- Existing Mongo lifecycle document and append-only attempt list; no new collection or destructive migration.

Corrected:

- Stage/reply constraints now prevent non-RCPT and ambiguous policy evidence from invalidating a recipient.
- Enhanced-code parsing and remote-text regex evaluation are bounded and timeout-safe.
- Sender rotation in Enforced mode requires strongly sender-specific MAIL FROM evidence.
- Mailbox full can use the existing bounded provisional revalidation path only when Enforced.
- Attempt history retains compact response intelligence instead of losing it when raw probe payloads are sanitized.

Added:

- Normalized response reasons, semantic fingerprints, evidence strength, result-state/retry/cooldown/health decisions, and optional observation identity context.
- Deterministic provider/generic pattern registry and startup validation.
- Disabled/Shadow/Enforced orchestration with Shadow default and safe failure fallback.
- Multi-dimensional shadow comparison telemetry and a versioned sanitized replay corpus.

Deferred by scope:

- Historical percentiles and aggregate provider-response analytics.
- Automatic retry-policy tuning or automatic rollout promotion.
- Dashboards and new admin/public API versions.
- Machine learning or calibrated deliverability models.
- Runtime/customer-authored regex rules, IP discovery, IP pools, and IP cycling.

## Interpretation precedence

The candidate classifier follows this order:

1. SMTP stage constraints.
2. Reply class constraints.
3. Enhanced status semantics.
4. High-confidence rules for the provider already detected by the existing detector.
5. Generic text fallback.
6. `UnknownProviderResponse`.

Provider rules refine an already known provider; response text never changes provider identity.

## Decision matrix

| Evidence | Mailbox impact | Retry | Cooldown/health scope | Sender rotation | Canonical category in Enforced mode |
|---|---|---|---|---|---|
| RCPT 250/251 accepted | Valid | None | Success | No | Accepted |
| RCPT mailbox not found/disabled/inactive/recipient-specific permanent rejection | Invalid | None | Recipient permanent | No | RecipientRejected |
| RCPT mailbox full | Provisional | Backoff | Domain temporary | No | MailboxFull |
| Greylisting | Provisional | Backoff | Domain temporary | No | Greylisted |
| Generic temporary/routing temporary/provider unavailable | Provisional | Backoff | Domain temporary | No | TemporaryFailure |
| Provider rate or connection limit | Provisional | Cooldown | MX provider | No | RateLimited |
| IP policy or reputation block | Provisional | Cooldown | Source IP | No | VerificationBlocked |
| Verification refused/provider policy block | Provisional | Cooldown | MX provider | No | VerificationBlocked |
| Sender invalid/rejected/policy rejected at MAIL FROM | No recipient conclusion | None | Outbound identity | Yes, sender only | VerificationBlocked |
| Greeting/connect timeout/failure | Provisional | Backoff | Domain/connection | No | Timeout or ConnectionRejected |
| EHLO/protocol/DNS/TLS/routing permanent failure | No recipient conclusion | None | Protocol infrastructure | No | ProtocolFailure |
| Ambiguous or unknown response | No recipient conclusion | None | None | No | Unknown |

## Rollout and configuration

Configuration path: `EmailValidation:SmtpResponseIntelligence`.

```json
{
  "Mode": "Shadow",
  "ClassificationVersion": "smtp-response-rules-1.0.0",
  "DecisionPolicyVersion": "smtp-response-policy-1.0.0",
  "MaximumResponseCharacters": 4096,
  "RegexTimeoutMilliseconds": 100
}
```

- `Disabled`: legacy interpretation only; invocation telemetry records kill-switch usage.
- `Shadow`: legacy canonical result plus candidate evidence and comparison telemetry.
- `Enforced`: deterministic candidate policy supplies the canonical compatibility category and may use existing retry/cooldown/sender-health mechanisms.

Rollback is a configuration-only change from `Enforced` to `Shadow` or `Disabled`.

## Metrics

Meter: `EmailValidation.SmtpResponseIntelligence`.

- `smtp_response_classified_total`
- `smtp_response_agreement_total`
- `smtp_response_disagreement_total`
- `smtp_response_candidate_failure_total`
- `smtp_response_classification_duration_ms`
- `smtp_normalized_reason_disagreement_total`
- `smtp_mailbox_impact_disagreement_total`
- `smtp_result_state_disagreement_total`
- `smtp_retry_decision_disagreement_total`
- `smtp_cooldown_decision_disagreement_total`
- `smtp_rotation_decision_disagreement_total`
- `smtp_outbound_health_decision_disagreement_total`

Labels are limited to rollout mode, provider enum, SMTP stage, and normalized reason. They contain no email address, validation id, MX hostname, source IP, or raw response.

## Fixtures and verification

Replay corpus: `tests/EmailValidation.Core.Tests/Fixtures/smtp-response-intelligence-v1.json`.

Tests verify normalized interpretation, policy outcomes, stage safety, shadow immutability, enforced changes, strict sender rotation, stable sanitized fingerprints, bounded response processing, mailbox-full retry gating, and Mongo attempt-history round trips.

Verification result: solution build succeeded with zero warnings/errors; all 401 tests passed (372 Core, 24 API, 3 integration, 2 gRPC).

## Important files

- `EmailValidation.Domain/EvidenceModels.cs`: immutable normalized classification, decision, rollout, context, and evidence concepts.
- `EmailValidation.Application/SmtpResponseIntelligencePolicy.cs`: decision policy and rollout/shadow orchestration.
- `EmailValidation.Infrastructure/SmtpResponseRuleRegistry.cs`: bounded sanitization, enhanced-code parser, deterministic developer-owned rules, and semantic fingerprints.
- `EmailValidation.Infrastructure/SmtpResponseClassifier.cs`: stage/reply/structured/provider/generic classification pipeline.
- `EmailValidation.Infrastructure/SmtpResponseIntelligenceMetrics.cs`: low-cardinality rollout and disagreement telemetry.
- `EmailValidation.Core/RevalidationModels.cs` and `RevalidationServices.cs`: compact immutable attempt fields and existing retry integration.
- `EmailValidation.Infrastructure/SmtpMailboxProbe.cs` and `ProbeSenderHealthChecker.cs`: observation context capture and Enforced-only scoped health/rotation consumption.
- `tests/EmailValidation.Core.Tests/Fixtures/smtp-response-intelligence-v1.json`: sanitized replay corpus.
- `tests/EmailValidation.Core.Tests/SmtpResponseIntelligenceTests.cs`: parsing, policy, rollout, precedence, privacy, and safety coverage.

## Remaining risks and deliberately deferred work

- Provider wording changes can reduce a rule to conservative unknown. Update the versioned fixture corpus and classification version before changing rules.
- Source-IP cooldowns use the existing optional outbound-IP throttle dimension. Deployments that do not supply a concrete outbound IP safely collapse to the existing `default-ip` scope; automatic IP discovery or cycling is deliberately outside this P0.
- Rules are developer-controlled code, not runtime-supplied regex. Runtime rule authoring and a separate rules datastore are deliberately deferred to avoid unsafe regex/config drift and a parallel persistence path.
- Promotion to Enforced should be based on shadow disagreement/error rates by provider and normalized reason. There is intentionally no automatic promotion.

Recommended exit criteria before promotion: replay corpus remains green; stage-safety and retry/persistence suites remain green; material shadow disagreements are reviewed per provider; no provider/IP response becomes mailbox invalidity; no RCPT result rotates a sender; candidate failure/regex-timeout rates remain acceptable; and the production change is explicitly approved.
