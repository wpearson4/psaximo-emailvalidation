# EV06: versioned mailbox identity

Mailbox identity now uses the explicit `exact-local-v1` policy. The existing normalizer still trims surrounding whitespace, lowercases the domain, applies IDNA, and removes a trailing domain dot. It preserves local-part case and spelling, including Unicode, dots, and plus tags. `User@example.test` and `user@example.test` are distinct; `User@BÜCHER.example` and `User@xn--bcher-kva.example` are equivalent.

The key format is `mailbox-v2:exact-local-v1:<lowercase SHA-256 hex of UTF-8 normalized address>`. `MailboxIdentity` owns this contract. No provider-specific equivalences are enabled. MX/provider detection cannot change identity. Any future provider exception must be an explicit policy with a distinct policy/key version, narrowly scoped applicability, and tests covering both equivalence and non-equivalence; it must not reinterpret existing evidence.

The identity applies to mailbox persistence, result caches, single-flight requests, lifecycle lookup and retry results, mailbox probe budgets, suppression/risk address matching, correlation hashes, and job request fingerprints. Domain evidence remains shared by domain. New validation results carry `MailboxKey`; old serialized records deserialize with a null key and cannot pass reuse checks.

## Migration behavior

- **JSON:** mailbox and suppression filenames hash the versioned key. Legacy files remain untouched and are never fallback reads. Fresh observations populate new files independently for each exact mailbox.
- **Mongo mailbox evidence:** `_id` and `MailboxKey` use the new key. The initializer creates partial unique `ux_mailbox_key_v2` before removing `ux_mailbox_normalized`. It retains legacy documents and validates key/address/payload agreement on reads. Unstamped or mismatched evidence is not promoted on writes.
- **Mongo lifecycles:** partial unique `ux_lifecycle_active_mailbox_v2` replaces `ux_lifecycle_active_email`, again creating the replacement first. Only keyed provisional records participate. Re-running initialization is safe. A new submission cannot attach to a legacy validation ID or to another local-case variant.
- **Pending legacy retries:** the worker marks the old lifecycle failed without probing its folded recipient. The caller must submit the original address with its intended case as a new validation. The implementation cannot recover that information from the folded record.
- **Downstream evidence:** old delivery snapshots cannot create current suppressions; old validation results cannot create current mailbox training snapshots. Replayed legacy lifecycles omit mailbox correlation. Historical records remain available for audit. Current mailbox HMAC correlation versions include the identity version; domain correlations retain their independent semantics. Mailbox probe budgets and all job request fingerprints also move to the new namespace.

No records are split, copied to guessed case variants, or bulk relabeled. An existing lowercase record does not establish that the original recipient was lowercase. Both lowercase and mixed-case submissions require fresh validation after migration.

## Rollout

Stop old validation hosts and retry workers before starting the updated fleet, so old writers cannot recreate legacy indexes or emit ambiguous evidence during transition. Run the normal persistence initializers with existing index-management permissions, then start all updated consumers. The migration uses existing collections and storage; it adds no service or infrastructure.

Clients resubmitting an old job idempotency key receive the existing fingerprint-conflict behavior because the request fingerprint version changed. Use a new idempotency key and validation ID when requesting refreshed validation. Explicit configured suppression/risk addresses retain their supplied local-part spelling; review any lists previously prepared by folding entire addresses before treating those entries as exact identities.

Do not roll old binaries back onto these collections after v2 writes: they can recreate uniqueness constraints that collide with retained legacy records. Roll forward, or restore an appropriate pre-migration backup with the old fleet stopped. Deployment has not been performed.

## Regression coverage

Synthetic tests cover actual validation through memory reuse and durable JSON restart, IDNA/domain aliases, case-distinct SMTP recipients, concurrent single-flight isolation, legacy evidence refresh, suppression/outcome migration, retry identity, HMAC/training/replay boundaries, idempotency, and mailbox probe budgets. No external DNS or SMTP probes are required.

Mongo integration tests run against a temporary localhost database. They seed the legacy indexes and records, initialize twice, write both case variants alongside legacy records, reload from another store instance, and verify active-lifecycle uniqueness. Legacy mailbox data is retained.

Verified locally on 2026-10-09: **640 Core, 52 API, 2 gRPC, and 3 Mongo integration tests passed** (697 total). The solution build completed with zero warnings and errors; `git diff --check` passed. External live DNS/SMTP tests were excluded. The Mongo tests used an isolated temporary database, not an existing deployment.

```sh
dotnet test tests/EmailValidation.Core.Tests/EmailValidation.Core.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test tests/EmailValidation.Api.Tests/EmailValidation.Api.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test tests/EmailValidation.Grpc.Tests/EmailValidation.Grpc.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false
# Set EMAIL_VALIDATION_TEST_MONGO to an isolated test Mongo instance first:
dotnet test tests/EmailValidation.IntegrationTests/EmailValidation.IntegrationTests.csproj --no-restore -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~MongoValidationIntelligenceStoreTests|FullyQualifiedName~MongoValidationLifecycleStoreTests'
dotnet build EmailValidation.sln --no-restore -m:1 -p:UseSharedCompilation=false
```

The wider Mongo suite also exposes a pre-existing `MongoProjectionOutboxTests.Outbox_IsIdempotentAtomicallyClaimedReclaimableAndTtlSafe` failure: Mongo rejects parallel array indexing of `LockExpiresAtUtc` and `NextPublishAttemptAtUtc`. This was reproduced independently on baseline commit `ef1d6c4`; EV06 does not modify that outbox behavior. Keep that failure visible as separate work rather than treating the full integration suite as green.
