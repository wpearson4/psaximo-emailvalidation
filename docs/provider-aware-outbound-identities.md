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
| Local identity eligibility | PARTIALLY IMPLEMENTED | CIDR/reserved-address validation, interface enumeration, bound-address filtering, enabled-state checks, and coarse FCrDNS gating | Extended the existing discovery/validator path with interface existence/operational state, wrong-interface detection, explicit expected PTR names, deterministic hostname normalization, and host-local readiness snapshots. |
| PTR/forward DNS readiness | PARTIALLY IMPLEMENTED | `IForwardConfirmedReverseDnsValidator` used `System.Net.Dns`, a fixed cache lifetime, and a coarse state enum | Extended the existing validator and DNS wire client with structured PTR/A results, TTLs, strict/compatible policy, negative/transient caching, bounded last-known-good grace, single-flight refresh, rollout modes, and background refresh. No second selector or DNS subsystem was added. |
| Identity health/cooldown | PARTIALLY IMPLEMENTED | Sender health and provider-wide circuit state existed | Added separate global/provider identity health with expiring cooldown/quarantine, in-memory fallback, and Mongo persistence. Recipient failure and a single timeout have no identity penalty. |
| SMTP source binding and EHLO | PARTIALLY IMPLEMENTED | The connection factory already bound the selected IPv4 address and used the selected EHLO hostname | Added post-connect local-endpoint verification. A mismatch stops before the SMTP greeting and is recorded as infrastructure/bind failure; default egress is never accepted. |
| Attempt evidence | PARTIALLY IMPLEMENTED | Immutable lifecycle attempts already captured provider, topology, SMTP stage/reply, response fingerprint, identity, source address, interface, EHLO, and selection version | Added configured and actual bound source IP, expected PTR hostname, FCrDNS state/evaluation time/policy version. Older Mongo documents remain additive-field compatible. |
| Container namespace/security | IMPLEMENTED BUT INCORRECT | API and worker already use host networking, no published ports, read-only roots, `privileged: false`, and `cap_drop: ALL`; API binds loopback | This is the documented temporary compatibility option because API requests still execute live SMTP. It is safe for source binding but should eventually delegate all live execution to the worker. |
| Reproducible host networking | IMPLEMENTED | `.162/28`, table 200, narrow source rule, and outbound-only firewall were manually present | Added and applied an idempotent `--check/--apply/--rollback` NetworkManager artifact. All 13 source paths and persistence across interface reactivation were verified. |

## Configuration

Use `examples/appsettings.outbound-identities.json` as the source for Azure App Configuration keys. Identity values are not secrets. Production must retain `RequireAddressToBeBound=true` and `RequireForwardConfirmedReverseDns=true`.

Each identity now explicitly configures both `ExpectedPtrHostName` and `EhloHostName`. The code default remains `Observe`; the verified production deployment configuration uses `Enforced` with `StrictOneToOne` validation, five-minute minimum freshness, 24-hour maximum freshness, 60-minute fallback freshness, five-minute negative caching, a 60-second transient retry, and a 15-minute bounded last-known-good grace. Configuration/policy version changes invalidate the in-memory DNS cache.

Rollout modes are:

- `Disabled`: skip DNS gating; local binding remains mandatory.
- `Observe`: evaluate, cache, log, and report DNS readiness without excluding an identity solely for DNS state.
- `Enforced`: require local binding, exact provider-group membership, DNS readiness, EHLO consistency, and operational health.

`diagnostics outbound-identities` performs a DNS-only, no-SMTP refresh and prints a concise readiness row per configured identity. The API liveness endpoint does not depend on DNS. Readiness is degraded in Observe mode when identities are not fully DNS-ready and unhealthy when a provider group has no usable identity; anonymous health output remains status-only.

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

## Current application deployment

The earlier App Configuration and registry authorization blockers were repaired. The API and worker are deployed through the production Azure DevOps pipeline, and the public liveness endpoint is healthy. Before this configuration promotion, readiness remained degraded because the running DNS-readiness cache still contained observations from the PTR cleanup window.

## DNS and FCrDNS readiness

The deployment hostname convention is now `outbound-162.email.digitalwarehouse.io` through `outbound-174.email.digitalwarehouse.io`. Identity IDs remain `smtp-162` through `smtp-174`; those are stable internal identifiers, not DNS names. Both `ExpectedPtrHostName` and `EhloHostName` use the `outbound-*` hostname.

The 2026-09-05 verification found exactly one expected `outbound-*` PTR on every address from all four authoritative Corespace nameservers. Every expected hostname returned exactly one matching IPv4 address. Cloudflare and the local recursive resolver returned the clean set for all 13 identities. Google Public DNS intermittently returned legacy answers from a few cache nodes with short remaining TTLs; because every authoritative server was already clean, these were propagation artifacts rather than authoritative defects.

The resulting production invariant is:

1. `outbound-N.email.digitalwarehouse.io A 64.182.22.N`.
2. Exactly one `64.182.22.N PTR outbound-N.email.digitalwarehouse.io`.
3. `EHLO outbound-N.email.digitalwarehouse.io` for the selected identity.

Do not disable `RequireForwardConfirmedReverseDns` to work around missing external DNS.

Latest read-only DNS matrix (2026-09-05 UTC):

| Identity range | Source IP range | Expected PTR/A/EHLO | Authoritative PTR count | Strict FCrDNS |
|---|---|---|---|---|
| `smtp-162`–`smtp-174` | `64.182.22.162`–`64.182.22.174` | `outbound-162`–`outbound-174.email.digitalwarehouse.io` | one per IP | `Valid` |

Production deployment configuration now uses `DnsReadiness:Mode=Enforced` and policy version `2026.09.2`. The policy-version change invalidates stale application readiness entries so the next deployment re-evaluates the corrected DNS.

## Post-rollout verification

After deploying the enforced configuration:

1. Confirm startup validation admits at least one identity in every configured group and all 13 after recursive caches expire.
2. Confirm `/health/ready` returns `Healthy` after the background refresh completes.
3. Confirm API ingress, worker health, loopback Kestrel binding, no worker listener, and `outbound-only` firewall state after application restart.
