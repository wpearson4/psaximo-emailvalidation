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

- Validation, classification, confidence-model, and provider-strategy versions were advanced so persisted
  results created under the previous policy are not incorrectly reused.
- A `LocalCooldown` result is provisional and should be retried at or after `Retry After`; it is not evidence
  that the recipient provider rejected mailbox verification.
- Mailing-risk signals such as role accounts, disposable domains, abuse indicators, and suppressions remain
  separate from technical catch-all deliverability.

### Verification

- The release is covered by 237 offline core tests and one opt-in integration-test assembly.
