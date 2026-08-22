# Changelog

This file records user-visible behavior, output-contract changes, and operational migration notes.

## Unreleased — 2026-08-21

### Changed

- Catch-all-related gateway acceptance now returns the public `CatchAll` status with a more specific
  `Confirmed`, `Likely`, `GatewayAmbiguous`, or `Historical` classification. Explicit randomized-recipient
  rejection still supports `LikelyValid` and is not over-labeled as catch-all.
- Provider policy circuits are scoped by normalized MX host, with optional outbound-IP and tenant dimensions.
  Provider-wide concurrency and pacing remain in effect, but a block from one tenant MX no longer suppresses
  unrelated tenants hosted by the same provider.
- Locally deferred SMTP work is reported as `LocalCooldown` instead of a remote provider block. Results expose
  whether a probe was attempted, the probe disposition, evidence quality, and the earliest useful retry time.
- Heuristic confidence remains confidence in the assigned classification. It is not a delivery probability;
  `Deliverability Probability` stays empty until outcome data supports calibration.

### CSV output

- New exports use one `Classification Confidence` column instead of adding both `Confidence` and
  `Classification Confidence`.
- Existing input files containing the legacy `Confidence` column retain it and receive an updated value for
  backward compatibility.
- Exports now include `Evidence Quality`, `Catch-All Classification`, `Probe Attempted`, `Probe Disposition`,
  `SMTP Response Category`, and `Retry After`.

### Operational notes

- Fresh reusable results now follow memory cache → persistent mailbox/domain policy → single-flight → live
  validation. Persistent hits warm a bounded cache, live results replace stale entries, and temporary/provider-block
  outcomes use a short configurable reuse window.
- Concurrent equivalent requests share one process-local validation after reuse misses. Source metadata distinguishes
  live, memory, persistent, and joined results while preserving the original evidence timestamp. Duplicate CSV rows
  benefit automatically and retain row order.
- Domain, provider, catch-all, structured observation, and mailbox intelligence now persist in two dedicated
  MongoDB collections behind the existing host-neutral store contracts. Startup creates missing indexes
  idempotently; temporary runtime Mongo failures degrade to live validation without changing classification.
- The console loads `EmailValidation:*` settings from Azure App Configuration with the configured environment
  label and resolves the Mongo connection through an existing Key Vault secret using `DefaultAzureCredential`.
  No connection string or credential is stored in the repository or written to logs.
- Validation, classification, confidence-model, and provider-strategy versions were advanced so persisted
  results created under the previous policy are not incorrectly reused.
- A `LocalCooldown` result is provisional and should be retried at or after `Retry After`; it is not evidence
  that the recipient provider rejected mailbox verification.
- Mailing-risk signals such as role accounts, disposable domains, abuse indicators, and suppressions remain
  separate from technical catch-all deliverability.

### Verification

- The release is covered by 260 offline core tests and an integration-test assembly with opt-in live DNS and
  real-Mongo collection/index coverage.
