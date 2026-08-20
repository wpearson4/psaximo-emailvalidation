# Email validation capability gap analysis

This matrix compares the prototype with the publicly documented feature categories in the
ZeroBounce Email Validation service as a capability benchmark. It does not assume equivalent
data, accuracy, status semantics, or scoring, and it deliberately excludes proprietary
intelligence that the application does not possess.

| Capability | Existing implementation | Completeness | Accuracy limitations | Missing work | Recommended action |
|---|---|---:|---|---|---|
| Syntax validation | Normalization and structural validation | Strong | Does not prove ownership or existence | None | Preserve |
| Domain existence | DNS resolver distinguishes NXDOMAIN, timeout, and failure | Strong | DNS can change after observation | None | Preserve and expose provenance |
| MX validation | Explicit MX, null MX, and RFC implicit A/AAAA fallback | Strong | MX existence does not prove reachability | Route usability | Resolve MX targets and distinguish definitive unroutability from transient failure |
| Mailbox probing | Bounded SMTP `RCPT TO` probing | Strong prototype | Providers may block or defer recipient validation | Mailbox-full distinction | Add mailbox-full category; preserve Unknown when verification is blocked |
| Catch-all detection | Random-recipient probes, confidence, provider strategies, cache, history | Strong prototype | Gateway acceptance is not always catch-all behavior | Continue conservative provider-aware interpretation | Preserve; do not reduce to Boolean |
| Provider detection | MX fingerprints with family, gateway, mailbox provider, confidence | Strong | A gateway does not reveal the backend mailbox provider | None | Preserve gateway/mailbox distinction |
| Disposable detection | Configured domain set and Boolean result | Partial | Absence from one local list is not proof of non-disposable status | Typed status, provenance, replaceable data source | Add an intelligence provider while retaining the Boolean compatibility field |
| Role-account detection | Configured local-part set | Good | Custom organizational roles are not discoverable | Broaden defaults | Add common roles such as `webmaster`; never make role status invalid by itself |
| Typo detection | None | Missing | Fuzzy matching can create unsafe suggestions | Conservative known-domain matcher | Add Damerau-Levenshtein/transposition detection with strict threshold and separate suggestion |
| Free-email detection | None | Missing | Provider offerings can change | Curated, configurable known-provider set | Add informational detector; do not affect technical validity |
| Toxic-domain detection | None | Missing | Requires reputation intelligence; SMTP is not evidence | Extension point and local configured intelligence | Add `IToxicDomainDetector`; default to NoEvidence/Unknown, never fabricate toxicity |
| Spam-trap risk | None | Missing | Confirmation requires authoritative address intelligence | Extension point and narrow heuristic layer | Add configured known-trap support and `PossibleSpamTrap` only for conservative heuristics |
| Abuse/complainer risk | None | Missing | Cannot be inferred from SMTP | Provider abstraction | Add `IAbuseRiskProvider`; default to Unknown |
| Global suppression | None | Missing | Competitor lists are proprietary | Application-owned provider abstraction | Add local/configured provider; never scrape or reconstruct third-party lists |
| MX forwarding | No explicit model | Missing | Third-party MX normally means hosted mail, not forwarding | Conservative configured signatures | Add detector and result model; default to Unknown unless explicit evidence exists |
| Alias detection | None | Not locally supportable | SMTP acceptance does not identify aliases | Extension point | Add identity-intelligence abstraction; default to Unknown |
| Alternate address | None | Not locally supportable | Requires directory/account identity data | Extension point | Share identity abstraction; default to Unknown |
| Mailbox-full detection | `5.2.x` treated as recipient rejection | Incomplete | Conflates existing-but-full with nonexistent | Dedicated SMTP category | Recognize enhanced code 4.2.2/5.2.2 and strong quota/full text; classify as Risky/Unknown, not Invalid |
| Greylisting handling | Explicit category and transient retry | Good prototype | Current generic backoff is not greylist-specific | Bounded greylist delay and history metric | Add configurable bounded delay and greylisting probability |
| Anti-greylisting retry | Retry count and exponential transient backoff | Partial | Same delay for all transient failures | Response-aware policy | Use greylist-specific bounded delay while respecting existing retry count and throttles |
| Unroutable infrastructure | Null/no-MX detection | Partial | Does not resolve explicit MX targets | MX address resolution | Add routability inspection with Routable/Unroutable/Unknown distinction |
| Domain age | None | Missing but optional | Reliable registration dates need RDAP/registrar data and vary by TLD | Interface only | Add provider abstraction and Unknown default; do not add a fragile mandatory dependency |
| SMTP provider information | Structured family/gateway/mailbox provider/MX host | Strong | Backend provider may remain Unknown behind a gateway | None | Preserve and expose in enriched domain result |
| Detailed status/sub-status | Reason codes only | Partial | No typed primary/all detailed classifications | Typed model | Add evidence-derived detailed statuses without changing top-level statuses |
| Historical intelligence | In-memory topology-scoped observations and delivery outcomes | Good prototype | Process-local, not durable or distributed | Greylisting metrics; persistent provider remains future work | Extend aggregation; preserve store abstraction |
| Confidence scoring | Explainable contributions plus provider/catch-all/reliability confidence | Strong prototype | Scores are evidence strength, not calibrated delivery probabilities | Per-signal confidence and provenance | Add typo/toxic/trap/domain-age confidence without manufacturing precision |
| Bounce risk | None | Missing | Cannot guarantee future delivery | Derived evidence model | Add Low/Moderate/High/Unknown with explicit rules |
| Do-not-mail recommendation | None | Missing | Business policy differs from technical validity | Separate policy layer | Add recommendation independent of technical status |
| Performance diagnostics | DNS, SMTP, cache, catch-all timings/counts | Good | No intelligence/routability timings | Additive metrics | Add timings and retain cache reuse behavior |

## Implementation boundary

The implementation should make definitive claims only from direct DNS/SMTP evidence or an
explicitly configured intelligence source. Reputation and identity signals that need external
or proprietary datasets remain typed extension points and return `Unknown` or `NoEvidence` by
default. This is intentional accuracy, not a missing Boolean shortcut.
