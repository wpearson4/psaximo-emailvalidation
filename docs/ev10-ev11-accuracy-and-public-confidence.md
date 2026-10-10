# EV10 / EV11: outcome benchmark and public confidence

These changes add measurement and safer result interpretation. They do not establish a production accuracy improvement. Model mode remains Disabled by default. No SMTP probes or deliveries are initiated by the benchmark or outcome importer.

## Authorized outcome imports

`POST /v1/validation-outcomes` requires `emailvalidation.admin` and a tenant claim. Import one existing, authorized observation at a time. Use the frozen feature snapshot's ID and timestamp; the server derives the validation ID, pseudonymous mailbox correlation, tenant, and provider from that stored snapshot. Cross-tenant or missing snapshots return 404. A source event is immutable: identical replay is successful, a changed payload returns 409. New conflicting events remain auditable and exclude conflicting truth from datasets.

Example (opaque identifiers only):

```json
{
  "snapshotId": "snapshot-id-from-authorized-export",
  "snapshotAtUtc": "2026-09-01T12:00:00Z",
  "source": "managed-directory",
  "sourceEventId": "directory-event-123",
  "authorizationReference": "approved-import-123",
  "cohort": "AuthorizedReal",
  "truthSource": "ManagedDirectory",
  "outcome": "MailboxConfirmed",
  "sendAttemptAtUtc": "2026-09-01T12:01:00Z",
  "observedAtUtc": "2026-09-01T12:02:00Z"
}
```

For directory/recipient confirmation, `sendAttemptAtUtc` is the observation attempt time; importing does not imply that a message was sent. The administrator is responsible for verifying permission and the provenance reference. The service records the submitting principal. This is an approved-import adapter, not an automatically trusted delivery-provider webhook.

`MailboxConfirmed` requires recipient confirmation or managed-directory truth. `MailboxAbsent` requires directory truth (or a separately classified recipient-specific hard bounce). A delivery event alone cannot claim either directory fact. SMTP acceptance, delivered events, missing bounces, policy failures, and missing outcomes do not establish mailbox existence. Existing v1/v2 evidence stays stored; production promotion now requires `mailbox-existence-v3-authorized`. Legacy outcomes are not silently upgraded to authorized truth.

V3 keeps Synthetic and AuthorizedReal cohorts separate. It requires explicit provenance and snapshot association, excludes conflicting labels and non-recipient-specific hard bounces, waits for a seven-day maturation period, and ignores events outside that window. A 5.1.1 hard bounce, or the existing Microsoft-specific 5.1.10 rule, can label absence. Unresolved and right-censored observations are counted separately from labeled rows.

## Offline reproducible benchmark

Run without starting the host, loading cloud configuration, opening persistent stores, or contacting DNS/SMTP:

```bash
dotnet run --project src/EmailValidation.Console --configuration Release --no-build -- \
  benchmark examples/benchmark-synthetic.json /tmp/ev10-report.json
```

An optional fourth argument supplies a calibrated candidate artifact JSON file. Without it, candidate equals the heuristic baseline, useful for checking the measurement pipeline. CI runs this synthetic example after its regression suite and publishes the report and frozen dataset as `synthetic-accuracy-benchmark`. Those artifacts are explicitly synthetic.

An input bundle contains a `TrainingDatasetRequest`, `calibrationStartsUtc`, `testStartsUtc`, a versioned action policy, captured snapshots, and imported outcomes. Optional `modelPolicy` supplies the exact classification thresholds and uncertainty configuration; otherwise the current defaults are frozen. Operators can export these existing classification collections through their approved database access. Keep the tenant scope and cohort explicit. Reports contain pseudonymous evidence only; do not put raw recipients, message text, or secrets in identifiers. Access and retention for frozen files must follow the same controls as their source collections.

The command writes the report and `<report>.dataset.json`. It records the input hash, full feature/label dataset hash, candidate checksum, feature/outcome/policy versions (including captured baseline rule versions), temporal boundaries, row counts, and unresolved/excluded/censored counts. Frozen report contents are reproducible; the declared maturation cutoff supplies the dataset creation clock.

Strict action policy treats only Valid as positive and Invalid as negative. Likely statuses, CatchAll, Risky, and Unknown abstain. `actOnLikely` enables an explicitly separate consumer-policy evaluation. False-positive/negative rates divide by truth-negative/positive cases; precision divides by positive actions; recall includes abstained positives in its denominator. Reports include confusion counts, actionable coverage, Brier score and log loss when probabilities exist. Unestimable rates are JSON null, never zero.

Mailbox groups are deduplicated; whole pseudonymous domains are held out. Remaining rows split chronologically into training, calibration, and out-of-time partitions. Training/calibration rows whose label maturation crosses the next boundary are ineligible. Provider and recipient-behavior slices accompany paired, seeded domain-cluster bootstrap intervals for candidate-minus-baseline false-positive, false-negative and coverage rates. Fewer than two domain clusters produce no interval. Small or selected datasets do not justify population accuracy claims.

Optional `measurements` entries reference snapshot IDs and supply measured `latencyMilliseconds`, `retryScheduled`, `allocatedCostUsd`, or authorized/synthetic `recipientBehaviorTruth`. The report includes their denominators, latency mean/p50/p95, retry frequency, allocated cost per measured request, and a recipient-behavior confusion matrix/accuracy. Missing measurements remain null. Cost is supplied from the existing infrastructure accounting; the benchmark does not invent infrastructure prices. All-request baseline inconclusive rate is separate from labeled-cohort abstention.

## Model release gate

Shadow evaluation remains possible with a checksum-bound calibrated artifact. Advisory and Enforced modes additionally require `EmailValidation:ClassificationModel:ReleaseApprovalPath` and `ReleaseApprovalChecksum`. The JSON approval (see `ModelReleaseApproval`) binds:

- Reviewer, approval ID/time/expiry, exact model checksum, and rollback artifact checksum.
- Dataset, evaluation report, and calibration evidence paths and SHA-256 checksums. Relative paths resolve beside the approval file.
- Decision-policy version and supported provider list, a subset of the candidate's declared providers.
- Explicit minimum positive/negative support per provider, maximum false-positive/negative rates, and maximum paired regression.

The evaluation must use AuthorizedReal v3 truth, match the dataset and candidate, include independent temporal and unseen-domain support, and satisfy every supported provider's gates. Threshold and uncertainty configuration must match the evaluated policy hash. `CalibrationEvidence` identifies its dataset/calibration version, calibration snapshot IDs, and training snapshot IDs; IDs must belong exclusively to the correct partitions, both labels must be present, and training cutoff must precede calibration. Fit only those training rows, calibrate only the held-out calibration rows, and evaluate the final artifact independently. This release supports mailbox-existence models; other targets remain shadow-only pending their own authorized outcome/release contract.

These files are operator-approved evidence, pinned by trusted deployment configuration; they are not a cryptographic attestation that every source observation is true. An approval never self-generates from a benchmark run. Missing, expired, tampered, insufficient, synthetic, or mismatched evidence prevents model scoring; the existing heuristic fallback remains available. Unsupported provider segments abstain even at extreme probabilities. Calibration/probability quality and operational tradeoffs remain part of human release review; do not interpret passing numerical gates alone as a delivery guarantee.

## Additive public result contract

REST validation, job results and status responses now include `assessment`. Both validation and status-stream gRPC responses carry the same assessment message. CSV appends equivalent named columns, preserving existing column order and legacy fields.

| Field | Meaning |
| --- | --- |
| `summaryStatus` | Valid, Invalid, Risky, Inconclusive. LikelyValid/LikelyInvalid/CatchAll map to Risky; their original detailed `status` remains unchanged. |
| `confidenceType` | Whether the existing numeric classification confidence is Heuristic or CalibratedProbability. It is not automatically delivery probability. |
| `heuristicEvidenceStrength` | Original heuristic score, preserved even when an enforced prediction changes classification confidence. |
| `evidenceLevel` | Existing qualitative HIGH/MEDIUM/LOW evidence assessment. Mailing risk and retry finality remain separate. |
| `recipientBehavior` | Target-scoped effective recipient behavior, consistent with EV09. |
| `mailboxEvidenceObservedAtUtc`, `routingEvidenceObservedAtUtc` | Separate original observation clocks; not cache-write or status-update times. |
| `mailboxEvidenceAgeSeconds`, `routingEvidenceAgeSeconds` | Age at presentation; null if the corresponding observation time is absent. |
| `probability` | Null unless a supported calibrated Advisory/Enforced prediction is available. Shadow, unsupported and abstained predictions expose no probability. |

A probability includes value, named target, outcome window, model/calibration/outcome/decision versions, training dataset ID, artifact checksum, rollout mode, and score timestamp. A zero-second MailboxExistence window denotes a point-in-time existence target; maturation is a separate labeling safeguard. A seven-day technical-delivery or bounce target is explicitly named, not inferred from a confidence number. The console's legacy Deliverability Probability column is populated only for a technical-delivery probability with provenance.

Final Unknown is always Inconclusive. “Final” means no more automatic attempts, not proof of invalidity. Existing `confidence`, detailed statuses, retry fields and risk decisions remain compatible.

## Verification and rollout

Focused synthetic regressions exercise authorized import identity/tenancy/idempotency/conflicts, label censoring, domain/time separation, deterministic reports, denominator math, checksum-only promotion rejection, expired/mismatched approval, calibration leakage, unsupported-provider abstention, and REST/gRPC/CSV contracts. A local Mongo regression checks concurrent immutable event writes and tenant separation.

Deploy the changes together, run a small validation smoke test against your existing authorized test set, and inspect Unknown/CatchAll/policy-block/timeout responses plus CSV and status updates. Then collect an authorized-real baseline and candidate benchmark before making an accuracy claim or activating a model. No new recurring service is required.
