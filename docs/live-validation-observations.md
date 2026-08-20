# Live provider observations

Controlled on 2026-08-19 from one outbound IP. Each domain received at most one randomized catch-all probe and one `postmaster@` or owner-authorized target probe. Retries were disabled and timeouts were five seconds. No `DATA` command or message content was sent.

| Provider route | Provider confidence | Target behavior | Random behavior | Effective category | Final interpretation |
|---|---:|---|---|---|---|
| Google Workspace (`appendpros.com`) | 99% | Accepted, `2.1.5` | Accepted once | `GatewayAccepted` | `LikelyValid`; catch-all remains unknown |
| Microsoft 365 (`microsoft.com`) | 99% | Verification blocked, `5.4.1` policy response | Ambiguous | `VerificationBlocked` | `Unknown` |
| Proofpoint (`proofpoint.com`) | 97% | Accepted, `2.1.5` | Accepted once | `GatewayAccepted` | `LikelyValid`; downstream mailbox is not proven |
| Mimecast (`mimecast.com`) | 96% | Verification blocked by policy | Ambiguous | `VerificationBlocked` | `Unknown` |
| Generic SMTP (`iana.org`) | 55% | Timed out | Ambiguous | `Timeout` | `Unknown` |

These are time- and IP-dependent observations, not permanent provider guarantees. They demonstrate why the classifier treats SMTP acceptance as provider-sensitive evidence and preserves uncertainty when a gateway refuses or defers mailbox verification.
