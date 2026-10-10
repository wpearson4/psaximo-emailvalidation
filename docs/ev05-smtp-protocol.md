# EV05: bounded SMTP replies and STARTTLS

SMTP observations now use a bounded byte reader and a real STARTTLS handshake. No new service, package, database collection, or public probe is required. Deployment has not been performed.

## Reply and session boundaries

Replies require CRLF framing, a valid three-digit reply code, a space or hyphen separator, and one consistent code across all multiline reply lines. A bare three-digit final line is accepted. Invalid UTF-8, embedded control characters, incomplete replies, unexpected successful command codes, and exceeded byte or line limits produce `ProtocolFailure`. Partial replies never reach the response classifier as recipient evidence. Limits count wire bytes, including CRLF, rather than Unicode characters.

One session deadline includes connection establishment, greeting, commands, TLS negotiation, and cleanup. Each command additionally shares a timeout across its write and reply read; receiving another byte does not reset it. RSET and QUIT share a shorter cleanup budget, bounded by the remaining session lifetime. Failed cleanup preserves an already-completed recipient observation. Caller cancellation still propagates, including during cleanup. Existing retry/session budgets continue to bound the number of sessions; this is a per-session deadline, not a total validation or scheduler-wait deadline.

## TLS and capabilities

When a successful EHLO advertises the exact STARTTLS extension and TLS is enabled, the probe sends STARTTLS, requires a 220 reply, performs an `SslStream` handshake, and issues EHLO again. Extension parsing uses actual reply lines, excludes the greeting line, and cannot interpret a pipe in response text as another extension. Failed EHLO followed by HELO clears all extension capabilities. SMTPUTF8 is determined anew after TLS.

The certificate must pass platform chain, validity, and MX hostname checks. TLS versions follow the platform defaults. Online revocation retrieval is disabled; this release does not implement MTA-STS, DANE, or revocation fetching. There is no certificate-validation bypass or automatic plaintext fallback after an attempted handshake. The internal test seam supplies an in-memory custom trust store without modifying the machine certificate store.

TLS refusals, disconnects, certificate/handshake failures, and required-TLS responses remain Inconclusive (`Unknown` in the detailed status enum), with sanitized stage evidence. Required-TLS text cannot turn into mailbox rejection even when paired with a contradictory recipient error code. `StartTls` is appended to the command enum, preserving existing numeric values. `TlsAdvertised` describes the initial advertisement; `TlsUsed` means the handshake completed successfully. The probe never sends DATA or AUTH.

## Configuration and rollout

Settings are under `EmailValidation:Smtp`:

| Setting | Default |
| --- | ---: |
| `EnableStartTls` | `true` |
| `SessionTimeoutSeconds` | 60 |
| `ConnectionTimeoutSeconds` | 10 |
| `CommandTimeoutSeconds` | 10 |
| `CleanupTimeoutSeconds` | 2 |
| `MaximumReplyLineBytes` | 512 |
| `MaximumReplyBytes` | 16384 |
| `MaximumReplyLines` | 64 |

Startup rejects nonpositive timeouts and invalid/unbounded reply settings. Deployments may set `EnableStartTls=false` to stage the parser change first; TLS-required destinations then remain Inconclusive. Strict certificate validation can increase Inconclusive results for misconfigured destinations, while valid TLS-required destinations become observable. The handshake adds latency; no speedup is claimed.

Default policy versions advance to engine `1.2.0`, provider strategy `1.3.0`, and SMTP response rules `smtp-response-rules-1.1.0`. Existing policy checks reject cached mailbox results and discard old domain control snapshots before new probing. Historical observations remain stored. If external configuration overrides these version settings, advance those overrides too. Deploy writers together to avoid old workers replacing newer evidence; concurrent stale-writer protection remains EV07 work. The EV04 broker integration gate and existing projection-outbox issue remain separate rollout prerequisites documented in `ev04-retry-execution.md`.

## Verification

Verified locally on 2026-10-09: **695 Core, 52 API, and 2 gRPC tests passed** (749 total, including 47 additional cases). The full solution build completed with zero warnings and errors, and `git diff --check` passed. Database and cloud broker tests were not rerun for this transport change.

Synthetic full-validator cases cover inconsistent multiline codes, malformed framing, unexpected successes, incomplete replies, and TLS requirements under Disabled, Shadow, and Enforced response intelligence. Byte-boundary, multibyte UTF-8, invalid encoding, line-count, capability-injection, HELO fallback, recipient-preserving cleanup, and invalid configuration tests cover the parser directly. Cache regressions require live mailbox refresh and discard pre-EV05 domain controls.

Loopback SMTP/TLS fixtures cover trusted, untrusted, expired, and wrong-host certificates; capabilities added and removed after TLS; failed post-TLS EHLO; handshake disconnects; slow greetings, handshake stalls, a shared session deadline, stalled RSET/QUIT, and caller cancellation. All network fixtures use localhost with ephemeral ports and test certificates. No external DNS/SMTP or production/cloud integration was used for EV05.

Protocol references: [RFC 5321 reply syntax and limits](https://www.rfc-editor.org/rfc/rfc5321.html), [RFC 3207 STARTTLS and capability reset](https://www.rfc-editor.org/rfc/rfc3207.html).
