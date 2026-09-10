# Probe sender catch-all

Inbound mail for the stable probe identities is delivered directly to Google
Workspace. The production EmailValidation host is outbound-only and must not
run an SMTP listener or expose TCP port 25.

## DNS

Namecheap Mail Settings remains set to **Custom MX** with this record:

| Type | Host | Value | Priority |
| --- | --- | --- | --- |
| MX | `validation.email` | `smtp.google.com` | 1 |

Keep the Google-generated domain-verification TXT record and the existing SPF
and DMARC records for `validation.email.digitalwarehouse.io`. Keep all forward
and reverse DNS records for the `.162`-`.174` outbound identities.

Do not publish MX or SPF records for `srs.validation.email`. SRS was required
only by the retired Postfix forwarding hop.

## Google Workspace routing

Add `validation.email.digitalwarehouse.io` to the Google Workspace account that
owns `appendpros.com`, verify the domain, and activate Gmail. Configure a Gmail
routing rule with these properties:

- apply only to inbound messages;
- apply to inactive and unrecognized accounts, not active users or groups;
- restrict the envelope-recipient pattern to
  `.*@validation\.email\.digitalwarehouse\.io`;
- replace the envelope recipient with `contact@appendpros.com`.

Verify the route from an unrelated external mailbox by sending to at least two
stable identities, such as `probe-162@validation.email.digitalwarehouse.io`
and `probe-174@validation.email.digitalwarehouse.io`. Do not use the catch-all
destination as the test sender.

## Retired server components

The production rollout removes the former `emailvalidation-mail-forwarder`
container, deletes its inbound firewalld exception, and removes its installed
configuration. Its detached Docker volumes are intentionally retained for a
short rollback window because they may contain queued mail. Remove those
volumes manually only after confirming that no retained queue data is needed.
