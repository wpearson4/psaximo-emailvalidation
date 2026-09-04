# Evidence-backed classification implementation report

Assessment date: 2026-08-26. Repository contents and repository-accessible data are the evidence source. The configured production Mongo connection is supplied outside the repository and was not available in this workspace; no claim is made about records that may exist in that external database.

## Outcome

The deterministic classifier remains canonical. Its numeric value is now explicitly available internally as `HeuristicEvidenceStrength`, is excluded from the public JSON contract, and is never represented as a probability. A versioned, privacy-safe ground-truth path now captures immutable prediction-time features, ingests outcomes idempotently, constructs maturation-aware reproducible datasets, enforces time/group separation, evaluates probability quality, and supports checksum-validated local logistic scoring with separate Platt calibration, uncertainty, decision policy, and Disabled/Shadow/Advisory/Enforced rollout behavior.

No model was trained, calibrated, registered, promoted, or enabled because the repository contains zero real delivery labels. `ClassificationModel:Mode` therefore defaults to `Disabled`. The next permissible runtime state is `Shadow`, and only after a held-out calibrated artifact and its checksum are configured.

## Architecture gap matrix

| Capability | Status | Existing implementation / final evidence | Gap | Required action |
|---|---|---|---|---|
| Deterministic rules | IMPLEMENTED | `EmailClassificationEngine` protects syntax, NXDOMAIN, null MX, routing, recipient-stage rejection, policy blocks, sender failures, and SMTP ambiguity; existing classification tests pass | None for this workstream | Preserve as canonical override |
| Heuristic score semantics | IMPLEMENTED | Classifier documentation already said the score was not calibrated; `EmailValidationResult.HeuristicEvidenceStrength` now gives the internal value its honest name while public `Confidence` remains unchanged | Historical aliases still exist for compatibility | Do not remove public/legacy aliases without contract versioning |
| Explicit prediction targets | IMPLEMENTED | `PredictionTargetKind` separates mailbox existence, technical delivery, hard bounce, and verification reliability | No inbox-placement or spam-trap target was invented | Add new targets only with trustworthy labels |
| Versioned outcome definitions | IMPLEMENTED | `OutcomeDefinitionCatalog` provides `mailbox-existence-v1`, `delivery-7d-v1`, `hard-bounce-7d-v1`, and `verification-reliability-v1` with label, maturation, duplicate, conflict, precedence, and normalization policies | Provider-specific reason mapping is intentionally limited to the existing normalization boundary | Create a new version for semantic changes |
| Outcome taxonomy | IMPLEMENTED | Additive taxonomy supports Delivered, HardBounce, SoftBounce, Complaint, Suppressed, RejectedBySenderPolicy, and UnknownOutcome | Legacy domain-level `DeliveryOutcome` remains for compatibility and is not a training label | Migrate authorized producers to the new observation boundary |
| Outcome confidence | IMPLEMENTED | Authoritative/High/Medium/Low/Untrusted quality is stored and filterable | No real confidence distribution is available | Measure after ingestion begins |
| Outcome ingestion boundary | IMPLEMENTED | `IEmailDeliveryOutcomeIngestionService` validates identity, normalization, confidence, and temporal ordering | No source integration exists in the repository | Connect an existing provider webhook/batch source when explicitly available |
| Outcome idempotency | IMPLEMENTED | Stable event ID is the key; duplicate redelivery is ignored; same natural event with a conflicting label is retained and reported | Producer-specific event-ID derivation remains source-owned | Document each producer's deterministic ID recipe |
| Immutable feature snapshots | IMPLEMENTED | Strongly typed `EmailValidationFeatureSnapshot` is created after canonical prediction, copied into append-only local/Mongo storage, and contains no raw email/local part | Capture is skipped when the tenant-scoped HMAC correlation key is unavailable | Configure the approved HMAC key before data collection |
| Feature schema versioning | IMPLEMENTED | `email-validation-features-v1` is stored on every snapshot and checked by dataset/runtime code | No schema migration tool is needed yet | Add a new schema version rather than changing v1 meanings |
| Mongo feature/outcome authority | IMPLEMENTED | `MongoClassificationEvidenceStore` uses dedicated snapshot/outcome collections, ordinary inserts/finds, `_id` idempotency, and normal indexes compatible with MongoDB 4.4 | Live integration requires deployment credentials | Run the guarded integration suite against MongoDB 4.4.31 |
| Local development persistence | IMPLEMENTED | JSON/local store persists the same privacy-safe records and behaves in memory when persistence is disabled | Not intended as production training authority | Use Mongo in deployed hosts |
| Leakage prevention | IMPLEMENTED | Dataset rows require send and outcome times after the snapshot; snapshots are never recomputed; conflicting labels are excluded | Historical rate feature generation is not yet implemented | When added, query observations strictly before `SnapshotAtUtc` and add target-row exclusion tests |
| Outcome maturation | IMPLEMENTED | Definitions carry windows; definitive labels may mature early; unresolved and right-censored rows are counted separately and never forced negative | Verification-reliability requires a separate validation-mechanism label source | Add that source before training the target |
| Reproducible dataset builder | IMPLEMENTED | Request pins target/definition/schema/window/cutoff/confidence/provider/tenant; manifest records counts, distributions, checkpoint, builder version, and stable SHA-256 | No file export was produced because there are no rows | Generate artifacts only after labels exist |
| Data-sufficiency gates | IMPLEMENTED | Configurable evaluator checks matured/positive/negative rows, time/provider/unseen-domain coverage, unresolved fraction, and label confidence | Deployment thresholds need governance approval | Approve thresholds before the first training run |
| Grouped mailbox split | IMPLEMENTED | Splitter collapses repeated email-correlation IDs across splits | None | Retain as a mandatory evaluation gate |
| Unseen-domain split | IMPLEMENTED | HMAC domain correlation creates a disjoint unseen-domain set without raw domain metric labels | Deterministic selection is simple, not stratified | Add stratification only if data volume justifies it |
| Out-of-time/calibration/test split | IMPLEMENTED | Explicit temporal boundaries keep training, calibration, and final out-of-time test sets separate | Final-test access governance is procedural | Lock the final split in the training command/pipeline |
| Heuristic calibration measurement | PARTIALLY IMPLEMENTED | Existing `ConfidenceCalibrationService` reports Brier/ECE/bands and refuses to call heuristic samples calibrated | Legacy records are unversioned, non-idempotent, raw-email-bearing, and not maturation safe | Re-run measurement from the new dataset once labels mature |
| Probability evaluation harness | IMPLEMENTED | `ProbabilityModelEvaluator` reports Brier, log loss, ECE, calibration line, false-valid/false-invalid rates, coverage, abstention, bands, providers, and unseen domains | No observations exist to populate a report | Evaluate out of time after data gates pass |
| Logistic-regression baseline | IMPLEMENTED | Offline deterministic L2 trainer and local coefficient-based runtime exist; missing values have explicit encoded defaults | No candidate was trained | Train only after gates pass |
| Separate probability calibration | IMPLEMENTED | Platt fitting requires held-out positive/negative raw scores; runtime rejects `uncalibrated` artifacts | No calibrator was fitted | Fit on calibration split, never training or final-test rows |
| Gradient-boosted challenger | NOT IMPLEMENTED | No supported package or sufficient labels exist | Not justified | Reconsider only after baseline evaluation and data sufficiency |
| Verification-reliability estimate | PARTIALLY IMPLEMENTED | Existing normalized verification reliability remains a separate snapshot feature and decision support signal | No separately trained reliability model or label source exists | Define/ingest mechanism outcomes before modeling |
| Uncertainty / abstention | IMPLEMENTED | Transparent policy abstains for near-threshold probability, unknown provider, excessive missingness, or low supported reliability | No formal coverage claim is made | Measure selective coverage out of time |
| Versioned commercial decision policy | IMPLEMENTED | Probability thresholds and abstention policy are configuration-owned and versioned outside the artifact; deterministic Valid/Invalid/CatchAll remain protected | Cost values are not caller-selectable | Approve one default policy before enforcement |
| Rollout modes | IMPLEMENTED | Disabled/Shadow/Advisory/Enforced are represented; Shadow/Advisory never alter canonical status; Enforced passes only through the protected decision policy | No artifact is configured | Stay Disabled, then explicitly move to Shadow |
| Runtime failure safety | IMPLEMENTED | Missing/bad artifact, schema mismatch, calibrator/scorer failure, snapshot failure, and optional scoring failure return the heuristic result | Operational alerts are deployment-owned | Alert on repeated scoring/snapshot failure |
| Artifact integrity | IMPLEMENTED | Trusted configured path, SHA-256 fixed-time comparison, JSON-only coefficients/metadata, schema/version validation, immutable checksum provenance | No external object-store registry is integrated | Use deployment-controlled immutable storage |
| Champion/challenger registry and rollback | PARTIALLY IMPLEMENTED | Configured checksum-identified artifact is an explicit champion and disabling restores heuristic behavior | Multiple challengers, approval history, and rollback pointer are not persisted | Add only when the first candidate exists |
| Model provenance | IMPLEMENTED | Prediction metadata carries model/schema/calibration/outcome/policy/dataset/cutoff/checksum/time/mode | None when scoring is disabled, by design | Never emit probability without it |
| Model cards | NOT IMPLEMENTED | No candidate was trained, so there is no model card to generate | Model-card artifact generator is not yet needed | Add before the first candidate promotion review |
| Shadow analytics projection | PARTIALLY IMPLEMENTED | Low-cardinality model/outcome/snapshot metrics exist and internal prediction holds comparison data | Elasticsearch event mapping was not expanded because no model can score | Add privacy-safe projection fields with the first Shadow candidate |
| Drift monitoring | PARTIALLY IMPLEMENTED | Required snapshot/model/outcome dimensions are available for later aggregation | No matured baseline distribution exists | Establish alerts after a Shadow baseline period |
| Automatic retraining/promotion | IMPLEMENTED | Neither was introduced | None | Keep manual governance |
| Public API compatibility | IMPLEMENTED | Public `Confidence`, status mapping, REST, and gRPC contracts are unchanged; internal score/prediction properties are JSON-ignored | None | Add probability only through an additive/versioned contract |

## Data sufficiency matrix

Repository search found no outcome JSON/JSONL/CSV/BSON dataset and no model/training artifact. The only delivery observations are synthetic unit-test fixtures. Synthetic tests are excluded from all counts.

| Prediction target | Matured positives | Matured negatives | Unresolved | Time coverage | Provider coverage | Label-confidence distribution | Ready to model? |
|---|---:|---:|---:|---|---|---|---|
| Mailbox existence (`mailbox-existence-v1`) | 0 | 0 | 0 observed records | None | None | None | No |
| Technical delivery within 7 days (`delivery-7d-v1`) | 0 | 0 | 0 observed records | None | None | None | No |
| Hard bounce within 7 days (`hard-bounce-7d-v1`) | 0 | 0 | 0 observed records | None | None | None | No |
| Verification reliability (`verification-reliability-v1`) | 0 | 0 | 0 observed records | None | None | None | No |

Repository-accessible raw counts are also zero for confirmed deliveries, hard bounces, soft bounces, complaints, suppressions tied to prediction snapshots, sender context, tenant context, and versioned immutable snapshots. Existing code contracts and synthetic tests do not constitute training data.

## Score semantics report

- `Confidence` remains the public v1 classification-confidence value. Its meaning was not changed.
- `HeuristicEvidenceStrength` is the internal honest name for that existing deterministic/heuristic score.
- `ConfidenceType.Heuristic` remains the value on current results.
- `RawModelPrediction.RawScore` is an uncalibrated logit and is never exposed as probability.
- A probability can exist only in `CalibratedPrediction` after a versioned calibrator succeeds and full provenance is present.
- `VerificationReliability` remains separate from mailbox existence and technical delivery.
- Risk signals (role, disposable, suppression, abuse, spam-trap indicators) remain separate from mailbox probability.

## Dataset manifest

No production dataset was generated: `TrainingRowCount = 0` before execution because no real outcome source is present. Creating an empty artifact would provide no reproducibility value and could be mistaken for training evidence. The implemented builder will emit a stable manifest and SHA-256 dataset hash when repository-authorized observations exist.

## Evaluation report

No candidate evaluation was run. Consequently Brier score, log loss, calibration intercept/slope, ECE, false-valid rate, false-invalid rate, abstention, coverage, provider segments, unseen-domain results, and out-of-time performance are **not available**, rather than reported as zero. Zero would incorrectly imply perfect performance.

The evaluation harness and probability-band tables are implemented and tested with synthetic rows solely for code correctness; those numbers are not model evidence.

## Model cards

None. No candidate model was trained. The logistic code is a baseline implementation, not a candidate artifact. A model card is required before any future candidate can be approved or promoted.

## Code changes

- Domain: versioned targets/outcomes, confidence and maturation states, strongly typed immutable feature groups/snapshots, dataset/manifest contracts, rollout/uncertainty/decision/provenance concepts, probability evaluation contracts, and the internal `HeuristicEvidenceStrength` name.
- Application: definition catalog, idempotent ingestion service, privacy-safe snapshot factory, capture/scoring decorator, reproducible maturation-aware dataset builder, sufficiency gates, leakage-safe grouped/time splits, probability evaluation, uncertainty, decision, and rollout orchestration.
- Infrastructure: local and Mongo authoritative stores, Mongo 4.4-compatible indexes, classification metrics, checksum-validated JSON logistic artifact loader, deterministic logistic scorer, separate Platt calibrator, offline L2 baseline trainer, and persistence initialization.
- Configuration/DI: new classification model policy and two Mongo collection names; Disabled is the default; non-disabled modes require a trusted artifact path and checksum.
- Tests: idempotency/conflicts/time ordering, immutable privacy-safe snapshots, leakage/maturation/conflict handling, stable dataset hash, grouped/time/domain splits, sufficiency refusal, shadow recommendation, deterministic protection, abstention, failure fallback, public-contract protection, evaluation metrics, and Mongo document compatibility.

## Rollout recommendation

**Disabled.** There are no real labels, no generated dataset, no held-out calibration set, no out-of-time evaluation, no model card, and no approved artifact. Heuristic behavior should remain canonical while the new snapshot and outcome-ingestion foundations are enabled with the approved tenant HMAC key.

After sufficient labels mature and governance artifacts are complete, the next state should be **Shadow**, never Enforced directly. Promotion to Advisory or Enforced requires explicit approval plus acceptable out-of-time Brier/log loss, calibration, false-valid rate, provider stability, unseen-domain performance, latency, coverage, and abstention.
