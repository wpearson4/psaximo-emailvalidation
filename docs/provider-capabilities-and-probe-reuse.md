# Provider capabilities and accept-all evidence reuse (EV14 / EV08)

Provider capability policies govern useful actions. They do not decide whether a mailbox exists and do not replace the tested, stage-specific SMTP response classifier. The default mode is **Shadow**: normal validation continues and proposed probe skips are recorded. Deployment alone does not enable probe skipping.

## Profiles and versioning

Configure `EmailValidation:ProviderCapabilities`. Supported profile keys are `Gmail`, `GoogleWorkspace`, `MicrosoftConsumer`, `Microsoft365`, `Yahoo`, `AOL`, `Proofpoint`, `Mimecast`, `AmazonSes`, `AppleICloud`, `Comcast`, `Proton`, `Fastmail`, `Zoho`, `Generic`, and `Unknown`. Consumer domains select Gmail/AOL separately from their related provider families; existing scheduling aliases remain compatible.

Each profile supports:

| Setting | Default | Meaning when enforced |
| --- | --- | --- |
| `ProbeMailbox` | true | Permit target-recipient probes |
| `ProbeControls` | true | Permit randomized-recipient probes |
| `ReuseConfirmedNonDiscrimination` | true | Permit EV08 reuse only after evidence qualification |
| `NonDiscriminationRefreshMinutes` | 60 | Maximum accept-all reuse age, bounded further by evidence and routing expiry |
| `RequireTls` | false | Stop before MAIL FROM if STARTTLS cannot be used |
| `AllowSmtpUtf8` | true | Permit internationalized-recipient probes only when SMTPUTF8 is also advertised |
| `RetryVerificationBlocked` | true | Permit existing durable retries for verification restrictions |
| `RetryAmbiguousAcceptance` | true | Permit existing durable retries for ambiguous target acceptance |
| `MinimumRetrySeconds` | 5 | Floor for SMTP retries; transient sessions defer to the existing durable retry path |

These are conservative defaults, not claims that every provider offers recipient verification. Restricting Yahoo/AOL or a gateway leaves mailbox existence inconclusive. Only published MX hosts are used. Neither a restriction nor accept-all behavior triggers backend discovery, source rotation, or a different route around the public gateway.

An enforced configuration requires `ApprovedPolicyHash`, a nonempty human review `ApprovalReference`, and explicit `CanaryProviders`. The hash includes policy version, every profile setting, and canary membership. It excludes mode and approval metadata so the reviewed shadow configuration can be promoted unchanged. This is a configuration review gate, not a cryptographic identity or authorization system. Editing a reviewed setting fails startup until its new hash is reviewed. The effective provider strategy version includes the hash; domain and mailbox evidence from another enforced configuration or a rollback is invalidated. Version invalidation is deliberately global in this first implementation.

## Evidence qualification

EV08 requires the current recipient-evidence contract, confirmed accept-all behavior from independent observations, sufficient confidence, explicit unexpired evidence, and accepted RCPT controls with successful MAIL FROM provenance. The scope must match the current provider, gateway, strategy version, MX preference, host, and routing fingerprint.

Current storage describes controls for one endpoint. Therefore EV08 only skips probes when the published routing contains one distinct endpoint. Multiple MX endpoints, unscoped or legacy evidence, future timestamps, candidates, mixed controls, expired evidence, inconclusive refreshes, changed routing, and qualified newer contradictions retain live evaluation. Expired accept-all evidence triggers bounded control refresh, respecting the existing transient backoff. A retry that requests newer evidence still obtains a new mailbox probe when provider policy permits it.

Reuse returns `Unknown` with `NonDiscriminationEvidenceReused`, the original catch-all observation timestamp, and `ProviderCapabilities.NextUsefulCheckAt`. It creates no new mailbox evidence timestamp and does not imply catch-all routing or mailbox existence. The evidence age is the validation timestamp minus `CatchAllEvidence.ObservedAt`. Provider policy restrictions carry `ProviderCapabilityRestricted` and an explanation that identifies local policy.

### Mongo control evidence persistence

Domain intelligence retains compact control-probe evidence across Mongo saves and reloads: MAIL FROM / RCPT TO stages and structured responses, MX host, and original observation timestamps. Response text, session history, banners, sender identities, and outbound connection identity details are removed. Lifecycle and mailbox result snapshots remain summaries; the domain intelligence record supplies reusable controls.

Older Mongo records lost the entire control-probe array. On read, missing or incomplete command-stage provenance invalidates those controls and requires fresh observations under the existing refresh/backoff policy. Counts and an evidence-contract label cannot reconstruct missing probes. Routing evidence and original observation clocks are preserved; signed routing attestations continue through their existing verification path. No bulk database migration or new infrastructure is required.

The persistence regressions run the validator after BSON serialization for recipient-specific Microsoft 365 decisions, enforced accept-all reuse, and Shadow comparisons. They also exercise incomplete historical records. To additionally run actual Mongo save/reload tests with new store instances and repeated validations, use a local test Mongo instance:

```sh
EMAIL_VALIDATION_TEST_MONGO=mongodb://127.0.0.1:27017 \
dotnet test tests/EmailValidation.Core.Tests --filter FullyQualifiedName~MongoControlEvidenceRegressionTests
```

These tests create and remove isolated temporary databases. Production stays in Shadow; after deployment, rerun the same 200-address sample and compare final results and eligible shadow comparisons before considering enforcement.

## Review, canary, and rollback

1. Deploy in Shadow and run the same authorized validation sample. Export existing feature snapshots as a JSON array. Each snapshot now carries an optional `ProviderCapabilities` assessment; no new collection or service is needed.
2. Generate an aggregate report:

   ```sh
   dotnet run --project src/EmailValidation.Console --configuration Release -- provider-shadow-report snapshots.json provider-report.json
   ```

   The report groups by profile, policy hash, mode, and whether enforcement applied. It counts proposed mailbox/control skips, compared statuses, disagreements, and unknown response fingerprints. Duplicate snapshot IDs are counted once. It emits no raw recipients. Status comparisons cover proposed mailbox skips; a comparison not performed is null, not agreement. This report does not measure real-world accuracy or automatically approve a policy.
3. Review every status disagreement and unknown fingerprint using existing sanitized SMTP evidence. Acceptance frequency alone is not evidence of mailbox existence or justification for promotion. The `EmailValidation.ProviderCapabilities` meter exposes `provider_capability_plan_difference_total` (including `status_disagreement`) and `provider_capability_unknown_response_total` for an existing metrics collector to consume; reports are available without a collector.
4. Choose the provider canary keys, configure candidate profiles, and rerun shadow review for that exact configuration. Compute its hash without starting the application or connecting to services:

   ```sh
   dotnet run --project src/EmailValidation.Console --configuration Release -- provider-policy-hash examples/provider-capabilities-shadow.json
   ```

5. Record the review reference and approved hash, then set `Mode` to `Enforced` on the canary deployment. Keep the same approved profile configuration on API and worker. Run one bounded, authorized sample before expanding. Compare classifications, retries, probe counts, and evidence ages. A disagreement or newly ambiguous response calls for review, not automatic promotion.
6. Roll back by setting `Mode` to `Shadow` or `Disabled` and redeploying API and worker together. Shadow preserves diagnostics; Disabled restores baseline actions without capability assessments. Existing SMTP response intelligence rollout modes remain independent.

The example configuration intentionally has no approval and cannot enforce. No live production canary or third-party SMTP probe is part of the synthetic test suite.

## Verification (2026-10-10)

`dotnet test EmailValidation.sln --configuration Release --no-restore --verbosity minimal -m:1 /nodeReuse:false` reported 878 passed and one Service Bus test skipped. Environment-dependent integration coverage is not evidence of a live production canary. Focused regressions include repeated-address call counts, shadow rejection disagreements, evidence expiry and changed scopes, configurable confirmation thresholds, policy approval/rollback, DNS-authoritative retry behavior, provider retry floors, local TLS/UTF8 restrictions, and snapshot/report persistence.

The frozen synthetic benchmark (`examples/benchmark-synthetic.json`) completed, and the offline hash/report commands were exercised against synthetic inputs. These checks establish regression behavior; they do not establish a production validation accuracy percentage. Review a matched production shadow run before enforcement.

Persistence follow-up: the default Release suite passed 891 tests with one Service Bus test skipped. The focused BSON/mapping/validator suite passed all 19 cases with local Mongo enabled, including three real Mongo save/reload scenarios; the frozen benchmark also completed. Enabling every optional Mongo integration test additionally exposed the existing `MongoProjectionOutboxTests.Outbox_IsIdempotentAtomicallyClaimedReclaimableAndTtlSafe` failure (`cannot index parallel arrays [LockExpiresAtUtc] [NextPublishAttemptAtUtc]`). The same failure was reproduced on unchanged commit `e10266a` in an isolated checkout. The outbox issue was separate from the control-evidence persistence fix. The subsequent outbox timestamp fix and compatibility requirements are documented in [the projection guide](elasticsearch-observation-projection.md#outbox-timestamp-compatibility-2026-10-10).
