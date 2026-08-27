# Provider-aware outbound identities

## Repository gap analysis

| Capability | Initial status | Existing implementation | Gap and action |
|---|---|---|---|
| Provider detection | PARTIALLY IMPLEMENTED | Central MX/signature detector and provider strategies | Added the small provider-owned-domain precedence list to the existing detector; custom domains continue to use MX topology. |
| Domain/provider cache | PARTIALLY IMPLEMENTED | Domain Intelligence already persisted topology fingerprints, observations, and expiry | Kept normalized-domain keys and DNS TTL freshness, and added configurable lower/upper TTL bounds without creating a second cache. |
| Result reuse and single-flight | IMPLEMENTED | Memory/persistent reuse and address/domain single-flight | Reused unchanged; outbound selection occurs only inside an actual SMTP probe. |
| Provider/domain pacing and durable retry | IMPLEMENTED | Provider circuit breaker, domain scheduler, lifecycle, Service Bus retry | Reused unchanged; policy blocks now stop the immediate SMTP retry loop. |
| Outbound identity model | NOT IMPLEMENTED | None | Added immutable identity, FCrDNS, health, selection, and outcome models in Domain. |
| Provider affinity groups | NOT IMPLEMENTED | Provider enums and policies existed | Added configuration-driven provider-to-group and group-to-identity mappings. No cross-group fallback is present. |
| Stable identity affinity | NOT IMPLEMENTED | Sender affinity existed, but no source identity selection | Added versioned SHA-256 rendezvous hashing over provider, normalized recipient domain, and stable identity ID. |
| Local identity eligibility | NOT IMPLEMENTED | None | Added CIDR/reserved-address validation, interface enumeration, bound-address filtering, enabled-state checks, FCrDNS gating, and startup validation. |
| Identity health/cooldown | PARTIALLY IMPLEMENTED | Sender health and provider-wide circuit state existed | Added separate global/provider identity health with expiring cooldown/quarantine, in-memory fallback, and Mongo persistence. Recipient failure and a single timeout have no identity penalty. |
| SMTP source binding and EHLO | NOT IMPLEMENTED | `TcpClient.ConnectAsync` used default source; EHLO came from the envelope sender domain | Added a testable connection factory that binds the selected IPv4 address before connect and uses the identity's EHLO hostname. The transport never selects another identity. |
| Attempt evidence | PARTIALLY IMPLEMENTED | Immutable lifecycle attempts already captured provider, topology, SMTP stage/reply, response fingerprint, and sender identity | Added recipient domain, classification source/confidence/version, MX host, outbound identity, source address, interface, EHLO, and selection version. Older Mongo JSON remains additive-field compatible. |
| Container namespace/security | IMPLEMENTED BUT INCORRECT | API and worker already use host networking, no published ports, read-only roots, `privileged: false`, and `cap_drop: ALL`; API binds loopback | This is the documented temporary compatibility option because API requests still execute live SMTP. It is safe for source binding but should eventually delegate all live execution to the worker. |
| Reproducible host networking | IMPLEMENTED | `.162/28`, table 200, narrow source rule, and outbound-only firewall were manually present | Added and applied an idempotent `--check/--apply/--rollback` NetworkManager artifact. All 13 source paths and persistence across interface reactivation were verified. |

## Configuration

Use `examples/appsettings.outbound-identities.json` as the source for Azure App Configuration keys. Identity values are not secrets. Production should retain `RequireAddressToBeBound=true` and `RequireForwardConfirmedReverseDns=true`.

The example assigns:

- Microsoft: `.162`–`.166`
- Google: `.167`–`.170`
- Yahoo/AOL: `.171`–`.172`
- General: `.173`–`.174`

The General group is mapped only from explicitly configured generic/other provider classifications. It is not a fallback for an unavailable Microsoft, Google, or Yahoo/AOL group.

## Host operation

Run from the repository checkout on `10.10.252.31`:

```bash
sudo ops/email-validation/configure-outbound-identities.sh --check
sudo ops/email-validation/configure-outbound-identities.sh --apply
sudo ops/email-validation/configure-outbound-identities.sh --rollback
```

`--apply` verifies the private route, table 200, NetworkManager ownership, and firewalld before changing anything; captures a root-only backup under `/var/backups/email-validation-outbound`; installs `.163`–`.174` as `/32` secondaries; migrates safely to the `/28` source rule; and validates every route. It never modifies `ens18`, the main default route, or the firewall zone.

`--rollback` restores `.162/28`, the `.162`-specific rule, table 200, `ipv4.never-default=yes`, and `outbound-only`.

## Verified server state (2026-08-27 UTC)

- SSH and administration return through `ens18` (`10.10.252.31/24`) via `10.10.252.2`.
- `ens19` is NetworkManager-managed as `Wired connection 1` and owns `64.182.22.162/28` plus `.163`–`.174` as `/32` secondaries.
- Gateway `.161` answers ARP. Duplicate-address probes report `.163`–`.174` available.
- Rule priority 100 routes `64.182.22.160/28` to table 200; the temporary migration rule was removed.
- Table 200 defaults through `.161` on `ens19`; the main default remains through `ens18`.
- Firewalld is active; `ens19` is in `outbound-only`, target `DROP`, with no services, ports, forwarding, or masquerade.
- API and worker containers use host networking, are non-privileged and read-only, drop all capabilities, and publish no Docker ports. Kestrel is configured for loopback.
- Every `.162`–`.174` HTTPS reflection check returned the selected source address.
- Unbound HTTPS egress remained `64.182.232.51`.
- Greeting/QUIT-only TCP 25 checks succeeded from `.162`, `.167`, `.171`, and `.173`.
- External probes confirmed TCP 80, 443, 2020, and 27017 are blocked on `.162` and `.174`.
- A NetworkManager down/up cycle restored all 13 addresses, the `/28` rule, table 200, and `outbound-only`.
- `--rollback` was executed successfully, restoring only `.162/28` and the narrow `.162` rule; `--apply` then restored and reverified the 13-address target state.

## Application deployment blocker

The host-network configuration was applied after the user explicitly accepted the pre-existing Azure outage. Application image/configuration deployment remains blocked:

- `emailvalidation-api` and `emailvalidation-worker` were each in a restart loop (206 restarts observed).
- Nginx health returned 502 and the API did not listen on loopback port 8080.
- The mounted App Configuration connection string returned HTTP 401 `Invalid Credential`.
- The certificate fallback also failed with Entra error `AADSTS700026` (the client application has no configured keys).
- The Compose `.env` selected image tag `64694cf...`, which could not be pulled with the host's current registry authorization; running containers used `0fdaf15...`.

Restore a valid App Configuration credential (or register the configured certificate with the Entra application), restore ACR pull authorization for the selected immutable tag, and confirm `/health/live` and public readiness are healthy before deploying and enabling the application feature.

## DNS and FCrDNS readiness

At inspection time none of `smtp-162.email.digitalwarehouse.io` through `smtp-174.email.digitalwarehouse.io` had an A record. `.162` had no PTR; `.163`–`.174` had PTRs pointing to unrelated historical hostnames. Therefore all 13 identities must remain ineligible while production requires FCrDNS.

For every identity, create:

1. `smtp-N.email.digitalwarehouse.io A 64.182.22.N` in authoritative forward DNS.
2. `64.182.22.N PTR smtp-N.email.digitalwarehouse.io` through the IP provider or delegated reverse zone.
3. Recheck `IP -> PTR hostname -> A same IP` before enabling outbound identities.

Do not disable `RequireForwardConfirmedReverseDns` to work around missing external DNS.

## Remaining verification after application blockers are cleared

After deploying a healthy application image:

1. Configure and verify matching A/PTR records for all identities.
2. Publish the outbound identity settings through Azure App Configuration.
3. Confirm startup validation admits every configured group.
4. Confirm API ingress, worker health, loopback Kestrel binding, no worker listener, and `outbound-only` firewall state after application restart.
