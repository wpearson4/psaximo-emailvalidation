# Accuracy-hardening gap analysis

This review was performed against the existing implementation before the incremental hardening pass. The console command, argument, switch, JSON, CSV, and file-input contracts were treated as compatibility boundaries.

## Already implemented correctly

- Syntax normalization, DNS/MX validation, null-MX and implicit-MX handling.
- Provider detection and provider-specific SMTP interpretation, including Microsoft 365/EOP.
- Enhanced SMTP status parsing and conservative handling of unexplained `550`, `5.7.x`, policy, temporary, greylisting, rate-limit, timeout, and connection failures.
- Bounded transient retry, global/domain/provider throttling, domain cache, observation history, freshness through cache lifetimes, MX-topology fingerprints, and verification reliability.
- High-entropy cryptographic random catch-all recipients, catch-all evidence counts/confidence, and conservative gateway/catch-all classification.
- Centralized heuristic confidence contributions; the public `confidence` field was already documented as non-calibrated.

## Implemented but incomplete

- SMTP evidence recorded the command that produced the terminal response, but retained only that one response rather than the complete session. It now retains every command stage and the failed stage.
- A pre-RCPT rejection was categorized conservatively, but the result model could not prove that `MAIL FROM` succeeded before a recipient rejection. Session provenance now makes that invariant testable.
- Only the preferred MX was used for mailbox probing. The validator now escalates across at most three distinct MX hosts only after ambiguous results and calculates consensus.
- Catch-all probing supported one to three configured probes, but was not adaptive. It now begins with the configured minimum and adds a probe only when the result could materially change.
- Verbose output exposed the terminal SMTP command and response. It now exposes the session stages, failed stage, banner, EHLO identity, TLS advertisement/use, sender health, MX attempts/consensus, and individual catch-all results.
- Confidence was heuristic in implementation and documentation but not typed in the result. `confidenceType`, `evidenceConfidence`, and a structured-evidence-derived `confidenceReason` are now additive fields.

## Implemented differently but equivalently

- The existing `SmtpCommand` and `SmtpEvidence` types were extended instead of introducing a parallel `SmtpStage` hierarchy.
- Existing provider strategies remain responsible for provider context; stage provenance is enforced before their category can become definitive recipient evidence.
- Existing catch-all confidence and domain observation models remain authoritative instead of adding a second Boolean catch-all model.
- Existing `VerificationReliability` remains the reliability measure and is reduced for conflicting MX evidence or blocked verification.

## Missing before this pass

- Cached validation of the configured live probe sender and its return-path DNS/mail routing.
- Explicit sender/policy reason codes such as `SenderIdentityRejected`, `SenderDomainRejected`, `PolicyBlock`, `AuthenticationRequired`, and `RelayDenied`.
- `LikelyInvalid`, `MxConsensus`, MX-attempt provenance, session-wide SMTP evidence, and a human-readable confidence reason.
- Regression coverage for MAIL FROM rejection before RCPT, incomplete catch-all evidence, adaptive catch-all escalation, ambiguous MX fallback, and conflicting MX results.

## Deliberately not duplicated

- No new commands or mandatory command-line parameters were added.
- No alternative provider engine, confidence calculator, cache, observation store, domain-intelligence system, or retry/throttle implementation was introduced.
- Interleaved random/target/random probing is not forced. The current cached domain-first architecture adaptively reuses or gathers random-recipient evidence before target probing; adding target interleaving unconditionally would duplicate target probes and violate the over-probing constraint.
