# EV13 / EV15 and optional cleaned downloads

## Current risk after evidence reuse (EV13)

Every validation response now evaluates current mailing risk after shared technical
cache, persistent reuse, or single-flight execution. Tenant context belongs to that
response; it is never saved in shared mailbox evidence. Current address intelligence
is refreshed through the existing provider boundary. Suppression reads use Mongo in
Mongo deployments, with tenant-scoped keys and global policy fallback. Compatible
identity-v2 legacy JSON suppressions migrate on read with insert-only writes.
Case-folded historical identities are not guessed apart.

A new suppression changes the next cache-hit recommendation without changing the
mailbox classification or any observation clock. Failed risk lookups cannot become
an affirmative send recommendation through another source's low-risk response.
Risk evaluation time and policy version are separate metadata. The shared
`SendRecommendationPolicy` keeps live and reused recommendation rules consistent.

Retry freshness still uses `RetryEvidencePolicy`: a counted retry needs a qualifying
observation strictly newer than its preceding attempt. Cache access, presentation,
and risk evaluation timestamps do not satisfy that test. Cooldown deferrals do not
consume an observation attempt. No new SMTP probes are initiated by retention or
cleaned downloads.

## Retention

Defaults under `EmailValidation:Retention`:

| Setting | Default |
| --- | --- |
| Enabled | true |
| DetailDays | 90 |
| BenchmarkDays | 365 |
| BatchSize | 200 |
| SweepIntervalMinutes | 60 |

The existing worker sweeps terminal lifecycle records, completed job/result copies,
old mailbox/domain intelligence, orphaned access/idempotency records, benchmark
snapshots/outcomes, and projection envelopes. Active jobs, provisional results,
execution ownership, pending dispatches, and active mailbox retry references are
protected. Canonical lifecycle checks protect against stale job-item projections.
Mongo deletes compare the inspected version or clock. An atomic terminal-job cleanup
claim fences concurrent failed-job restart and survives interrupted cleanup. Legacy DateTimeOffset arrays
and scalar UTC dates are supported. Batches advance through retained candidates so
an old active record cannot indefinitely hide other expired records.

Pseudonymous benchmark and projection evidence uses the longer window; published
outbox envelopes can expire earlier under their existing seven-day TTL. Configured
Elasticsearch projections receive bounded deletion requests. Local JSON copies are
also cleaned; legacy outcome arrays are rewritten as streams. Oversized individual
legacy documents are retained and reported as skipped for explicit remediation.
Suppression policies are preserved: expiring a validation must not silently permit
sending to a suppressed address. Original purchased files belong to the purchase
service and are not deleted by validation retention. Operator-exported benchmark
bundles and backups require the same retention policy in their owning storage.

Dry run (does not start broker workers or send SMTP):

```sh
dotnet EmailValidation.Worker.dll --retention
```

Apply one bounded sweep:

```sh
dotnet EmailValidation.Worker.dll --retention --retention-apply
```

Reports contain counts, not recipient addresses. `Retention.Enabled=false` disables
scheduled cleanup. Disable it temporarily when inspecting a migration dry run;
otherwise normal worker startup applies the configured policy. No production
cleanup was run as part of this implementation.

## Honest auxiliary metadata (EV15)

SPF and DMARC retain their existing record-state fields for compatibility and add
an evaluation scope. A syntactically accepted record indicates partial parsing,
not recursive SPF evaluation, organizational DMARC discovery, or message alignment.
DKIM is not signature-verified merely because observation is enabled. Missing
records never establish recipient invalidity.

`DisposableEmail:DatasetPublishedAtUtc` is the configured dataset publication time;
it remains null when unknown. Lookup time does not renew it. Detection time remains
separate. Current logistic-model support no longer counts unused DNSSEC,
authentication, or topology metadata as missing model inputs. Decision policy
advances to `classification-decision-policy-v3`, requiring new release evidence
before an enforced model uses the changed support policy. No model or provider
Shadow policy was promoted by this change.

## Optional cleaned CSV

`GET /v1/email-validation-jobs/{jobId}/file?removeInvalidEmails=true` blanks only the
selected email column where a completed item's result is final and exactly Invalid.
All source rows and other fields remain. Unknown, LikelyInvalid, provisional,
missing, failed, and source-value-mismatched entries are preserved. The selected
column must be unambiguous; cleaning never guesses a column from its name.

The default remains the original values plus validation columns. Cleaned downloads
use `-email-cleaned.csv`; ordinary downloads keep `-email-validated.csv`. CSV and JSON
sources both stream to CSV using the existing parser and bounded result pages.
The original source and stored results are unchanged. Authorization is unchanged.

Deploy the backend before exposing the frontend option. The frontend keeps the
choice local to the page and sends it through the existing API adapter. Server-side
`InvalidEmailRemovalPolicy` owns the decision; the browser does not classify rows.

## Design and validation

Existing adapters isolate Mongo/JSON/HTTP mechanics; the retention application
contract carries explicit cutoffs and dry-run intent. The existing validator
wrapper keeps caching separate from current risk. Shared domain policies own
removal and send-recommendation rules. No new service or dependency was added.

Focused regressions cover fresh suppression on cached evidence, tenant isolation,
unchanged observation clocks, full retry execution, local and Mongo retention,
legacy timestamps, active/stale retry projections, dataset publication metadata,
model support, and CSV/JSON cleaning. Frontend checks cover keyboard selection,
both download requests/names, recoverable errors, and narrow-screen reflow.
