# Probe sender mailbox forwarder

The production host accepts mail for `validation.email.digitalwarehouse.io` on
its management address only and forwards every recipient to
`contact@appendpros.com`. It does not provide authenticated submission, IMAP,
POP3, or general SMTP relay service. The `.162`-`.174` validation identities
remain outbound-only.

Before publishing the MX record, create these Namecheap records:

| Type | Host | Value | Priority |
| --- | --- | --- | --- |
| MX | `validation.email` | `email.digitalwarehouse.io` | 10 |
| MX | `srs.validation.email` | `email.digitalwarehouse.io` | 10 |
| TXT | `srs.validation.email` | `v=spf1 ip4:64.182.20.183 -all` | n/a |

The existing SPF and DMARC records for `validation.email` remain unchanged.
The SRS secret and Postfix queue are stored in Docker named volumes. The secret
is generated with 256 bits of operating-system randomness on first startup and
is never stored in Git.

The deployment verifies that the forwarder accepts an owned-domain recipient,
rejects an arbitrary relay recipient, and is not listening on any validation
source address. The SMTP test stops before `DATA`, so it sends no message.
